using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Analytic evaluator for scatter-driven outcomes: scatter pays, the feature trigger, and prizes
/// attached to individual scatter cells (e.g. Lucky Break coin prizes).
///
/// Reel design: for each reel, the distribution of which window cells show a scatter symbol
/// ("mask"). The scatter count N is the sum over reels, so its distribution is a convolution.
///
/// Engine as oracle (a handful of exact engine evaluations, not millions):
///   T(k)        probability the feature is entered when k scatters are visible,
///   S(k)        expected non-line win not attributable to a single cell, with k scatters,
///   prize(c)    expected win of the located condition on cell c when c shows a scatter and the
///               feature is triggered (covers the whole replacement chain: Sc -> ScSpl -> prize).
/// Then:
///   P(trigger)   = sum_k P(N=k) T(k)
///   E[non-line]  = sum_k P(N=k) S(k) + sum_cells prize(c) * P(c shows scatter AND trigger)
///
/// Model assumptions (trigger and pays depend only on the count, prizes only on their own cell)
/// are checked by measuring with several arrangements, and by the verify step.
/// </summary>
public sealed class ScatterEvaluator
{
    private readonly ReelModel model;
    private readonly HashSet<string> scatter;
    private readonly GameHost host;
    private readonly ActEnumerator enumerator;
    private readonly int baseIndex;

    public ScatterEvaluator(ReelModel model, HashSet<string> scatterSymbols, GameHost host)
    {
        this.model = model; scatter = scatterSymbols; this.host = host;
        enumerator = new ActEnumerator(host, new DrawPartition(host.Game));
        baseIndex = host.BaseLayoutIndex;
        // Per reel: mask -> (count, representative stop)
        Masks = model.Reels.Select(reel =>
        {
            var d = new SortedDictionary<int, (long count, uint rep)>();
            for (uint p = 0; p < reel.Range; p++)
            {
                int mask = MaskOf(reel.Windows[p]);
                d[mask] = d.TryGetValue(mask, out var c) ? (c.count + 1, c.rep) : (1, p);
            }
            return d;
        }).ToList();
        MaxCount = Masks.Sum(d => d.Keys.Max(Pop));
    }

    public List<SortedDictionary<int, (long count, uint rep)>> Masks { get; }
    public int MaxCount { get; }
    public double[] T { get; private set; } = Array.Empty<double>();
    public double[] S { get; private set; } = Array.Empty<double>();
    public Dictionary<(int reel, int window), double> Prize { get; } = new Dictionary<(int, int), double>();
    public List<string> Notes { get; } = new List<string>();
    public long OracleActs => enumerator.Paths;
    public int OracleConfigurations { get; private set; }

    public int MaskOf(string[] window)
    {
        int m = 0;
        for (int w = 0; w < window.Length; w++) if (scatter.Contains(window[w])) m |= 1 << w;
        return m;
    }
    public static int Pop(int m) { int c = 0; while (m != 0) { c += m & 1; m >>= 1; } return c; }

    private double P(int reel, int mask) => (double)Masks[reel][mask].count / model.Reels[reel].Range;

    /// <summary>Distribution of the scatter count over all reels except <paramref name="skip"/> (-1: none).</summary>
    public double[] CountDistribution(int skip = -1)
    {
        var dist = new double[MaxCount + 1]; dist[0] = 1;
        for (int r = 0; r < Masks.Count; r++)
        {
            if (r == skip) continue;
            var next = new double[MaxCount + 1];
            foreach (var kv in Masks[r])
            {
                double p = P(r, kv.Key); int c = Pop(kv.Key);
                for (int k = 0; k + c <= MaxCount; k++) next[k + c] += dist[k] * p;
            }
            dist = next;
        }
        return dist;
    }

    public sealed class Measurement
    {
        public double Other, Trigger;
        public Dictionary<(int reel, int window), double> Cells { get; } = new Dictionary<(int, int), double>();
    }

    /// <summary>Exact engine evaluation (all in-act draws) of one configuration of stops.</summary>
    public Measurement Measure(uint[] stops)
    {
        OracleConfigurations++;
        var m = new Measurement();
        enumerator.Enumerate(stops, (h, res, w) =>
        {
            double credited = res.Credited[baseIndex];
            double attributed = h.AllLineWins();
            foreach (var win in h.Game.CurrentLayout.WinningLocatedPayconditions)
            {
                if (win.SymbolsInfo.Count != 1) continue;
                var c = win.SymbolsInfo[0].Coordinate;
                if (!model.TryLocate(c, out int reel, out int window)) continue;
                m.Cells[(reel, window)] = (m.Cells.TryGetValue((reel, window), out double v) ? v : 0) + w * win.WonCredits;
                attributed += win.WonCredits;
            }
            m.Other += w * (credited - attributed);
            bool triggered = h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : res.LeftBase;
            if (triggered) m.Trigger += w;
        });
        return m;
    }

    /// <summary>Stops giving exactly <paramref name="k"/> scatters; reels are filled in the given order.</summary>
    public uint[]? StopsForCount(int k, IEnumerable<int> reelOrder, int fixedReel = -1, int fixedMask = 0)
    {
        var stops = new uint[model.Reels.Count];
        var masks = new int[model.Reels.Count];
        int remaining = k;
        if (fixedReel >= 0)
        {
            if (!Masks[fixedReel].ContainsKey(fixedMask)) return null;
            masks[fixedReel] = fixedMask; remaining -= Pop(fixedMask);
        }
        foreach (int r in reelOrder)
        {
            if (r == fixedReel) continue;
            // Largest available count not exceeding what is still needed (prefer single cells).
            int best = Masks[r].ContainsKey(0) ? 0 : Masks[r].Keys.First();
            foreach (var mask in Masks[r].Keys)
            {
                int pop = Pop(mask);
                if (pop <= remaining && pop > Pop(best)) best = mask;
            }
            masks[r] = best; remaining -= Pop(best);
        }
        if (remaining != 0) return null;
        for (int r = 0; r < stops.Length; r++) stops[r] = Masks[r][masks[r]].rep;
        return stops;
    }

    /// <summary>Run the oracle measurements needed for the model.</summary>
    public void Calibrate()
    {
        var forward = Enumerable.Range(0, model.Reels.Count).ToList();
        var backward = Enumerable.Range(0, model.Reels.Count).Reverse().ToList();
        T = new double[MaxCount + 1]; S = new double[MaxCount + 1];
        var countDist = CountDistribution();
        for (int k = 0; k <= MaxCount; k++)
        {
            if (countDist[k] == 0) continue;
            var a = StopsForCount(k, forward);
            if (a is null) { Notes.Add($"no single arrangement found for {k} scatters"); continue; }
            var ma = Measure(a);
            T[k] = ma.Trigger; S[k] = ma.Other;
            var b = StopsForCount(k, backward);
            if (b is not null && !b.SequenceEqual(a))
            {
                var mb = Measure(b);
                if (Math.Abs(mb.Trigger - ma.Trigger) > 1e-12 || Math.Abs(mb.Other - ma.Other) > 1e-9)
                    throw new NotSupportedException($"Trigger/scatter pay with {k} scatters depends on where they land (T {ma.Trigger} vs {mb.Trigger}); count model invalid.");
            }
            if (T[k] > 1e-12 && T[k] < 1 - 1e-12)
                throw new NotSupportedException($"Trigger probability with {k} scatters is {T[k]}, not 0 or 1; not supported by this evaluator.");
        }

        // Smallest triggering count, and the per-cell prize measured there (and checked one count higher).
        int kTrigger = Enumerable.Range(0, MaxCount + 1).FirstOrDefault(k => T[k] > 0.5);
        if (T[kTrigger] < 0.5) { Notes.Add("feature never triggers from scatters"); return; }
        for (int r = 0; r < model.Reels.Count; r++)
        {
            for (int w = 0; w < model.Reels[r].Coordinates.Length; w++)
            {
                int single = 1 << w;
                if (!Masks[r].Keys.Any(m => (m & single) != 0)) continue;
                int mask = Masks[r].ContainsKey(single) ? single : Masks[r].Keys.First(m => (m & single) != 0);
                var others = forward.Where(x => x != r);
                var s1 = StopsForCount(Math.Max(kTrigger, Pop(mask)), others, r, mask);
                if (s1 is null) { Notes.Add($"cannot place a triggering arrangement around reel {r} row {w}"); continue; }
                double v1 = Measure(s1).Cells.TryGetValue((r, w), out double x1) ? x1 : 0;
                Prize[(r, w)] = v1;
                var s2 = StopsForCount(Math.Max(kTrigger, Pop(mask)) + 1, others, r, mask);
                if (s2 is not null)
                {
                    double v2 = Measure(s2).Cells.TryGetValue((r, w), out double x2) ? x2 : 0;
                    if (Math.Abs(v2 - v1) > 1e-9 * Math.Max(1, Math.Abs(v1)))
                        throw new NotSupportedException($"Prize on reel {r} row {w} depends on the scatter count ({v1} vs {v2}); per-cell model invalid.");
                }
            }
        }
    }

    public double TriggerProbability()
    {
        var d = CountDistribution();
        double p = 0; for (int k = 0; k < d.Length; k++) p += d[k] * T[k];
        return p;
    }

    public double ExpectedNonLineWin()
    {
        var d = CountDistribution();
        double e = 0;
        for (int k = 0; k < d.Length; k++) e += d[k] * S[k];
        for (int r = 0; r < model.Reels.Count; r++)
        {
            var rest = CountDistribution(skip: r);
            foreach (var kv in Masks[r])
            {
                int mask = kv.Key; if (mask == 0) continue;
                double pTrigger = 0;
                for (int j = 0; j < rest.Length; j++) if (Pop(mask) + j <= MaxCount) pTrigger += rest[j] * T[Pop(mask) + j];
                for (int w = 0; w < model.Reels[r].Coordinates.Length; w++)
                    if ((mask & (1 << w)) != 0 && Prize.TryGetValue((r, w), out double prize))
                        e += P(r, mask) * pTrigger * prize;
            }
        }
        return e;
    }

    /// <summary>Model prediction for one configuration of stops: (expected non-line win, trigger probability).</summary>
    public (double win, double trigger) Predict(uint[] stops)
    {
        int k = 0; double prizes = 0;
        for (int r = 0; r < stops.Length; r++)
        {
            int mask = MaskOf(model.Reels[r].Windows[stops[r]]);
            k += Pop(mask);
            for (int w = 0; w < model.Reels[r].Coordinates.Length; w++)
                if ((mask & (1 << w)) != 0 && Prize.TryGetValue((r, w), out double prize)) prizes += prize;
        }
        return (S[k] + T[k] * prizes, T[k]);
    }
}
