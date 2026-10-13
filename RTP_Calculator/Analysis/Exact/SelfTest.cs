using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Exact;

/// <summary>
/// Empirical checks of every assumption the exact base-game calculation relies on.
/// Each check prints PASS/FAIL with counts; any FAIL means the exact numbers must not be trusted
/// for that game until the cause is understood.
/// </summary>
public sealed class SelfTest
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;
    public SelfTest(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    public int Samples { get; set; } = 200;

    public bool Run()
    {
        bool ok = true;
        var rng = new System.Random(2024);
        var host = GameHost.Load(dat, betPerLine, lines, 11);
        var setup = BaseSetup.Read(host);
        int baseIndex = host.BaseLayoutIndex;

        // A. Reel strips seen by the model do not change as the game is played.
        {
            for (int i = 0; i < 2000; i++) { host.EnsureBase(); host.PlayRandomAct(); while (host.IsAwayFromBase) host.PlayRandomAct(); }
            var after = BaseSetup.Read(host);
            int diffs = 0;
            for (int r = 0; r < setup.Model.Reels.Count; r++)
                foreach (var (p, _) in setup.Model.Reels[r].Stops)
                    if (!setup.Model.Reels[r].Windows[p].SequenceEqual(after.Model.Reels[r].Windows[p])) diffs++;
            ok &= Report("A", "reel strips are unchanged after 2,000 played games", diffs == 0, $"{diffs} differing stop windows");
        }

        // B. The per-layout credited win equals the sum of the win records (lines + others) in base acts.
        {
            int mismatch = 0, n = 20000;
            for (int i = 0; i < n; i++)
            {
                host.EnsureBase();
                var res = host.PlayRandomAct();
                if (res.PlayedLayout == baseIndex && res.Credited[baseIndex] != host.AllReportedWins()) mismatch++;
            }
            ok &= Report("B", "credited win == sum of reported win records (base acts)", mismatch == 0, $"{mismatch} of {n:N0} acts differ");
        }

        var partition = new DrawPartition(host.Game);
        var restoring = new ActEnumerator(host, partition) { AlwaysRestore = true };
        var host2 = GameHost.Load(dat, betPerLine, lines, 12);
        var lazy = new ActEnumerator(host2, new DrawPartition(host2.Game)) { AlwaysRestore = false };

        double[] Expect(ActEnumerator e, uint[] values, int line)
        {
            var acc = new double[4]; double mass = 0;
            e.Enumerate(values, (h, res, w) =>
            {
                double lineWins = h.AllLineWins();
                acc[0] += w * res.Credited[baseIndex];
                acc[1] += w * (line >= 0 ? h.LineWin(line) : 0);
                acc[2] += w * (res.Credited[baseIndex] - lineWins);
                acc[3] += res.LeftBase ? w : 0;
                mass += w;
            });
            if (Math.Abs(mass - 1) > 1e-9) throw new InvalidOperationException($"Internal path probabilities sum to {mass}, not 1.");
            return acc;
        }
        uint[] RandomStops() => setup.Ranges.Select(r => (uint)rng.Next((int)r)).ToArray();
        bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));

        // C. Replaying an act from the snapshot is deterministic, and skipping the restore after
        //    non-triggering base acts gives identical results (no hidden state carried between acts).
        {
            var configs = Enumerable.Range(0, Samples).Select(_ => RandomStops()).ToList();
            var first = configs.Select(c => Expect(restoring, c, -1)).ToList();
            int diffs = 0;
            for (int i = configs.Count; i-- > 0;)
            {
                var second = Expect(lazy, configs[i], -1);
                if (!Same(first[i][0], second[0]) || !Same(first[i][3], second[3])) diffs++;
            }
            ok &= Report("C", "act replay is deterministic and independent of play order", diffs == 0, $"{diffs} of {configs.Count} configurations differ");
        }

        // D. Line part: the expected win on a line depends only on that line's cells.
        {
            int line = setup.BetLines.Count > 0 ? setup.BetLines[0] : -1;
            int diffs = 0, tested = 0;
            if (line >= 0)
            {
                for (int i = 0; i < Samples; i++)
                {
                    var a = RandomStops();
                    int r = rng.Next(a.Length);
                    var b = (uint[])a.Clone();
                    if (!TrySwap(setup.Model.Reels[r], setup.LineKey(line, r), a[r], rng, out b[r])) continue;
                    tested++;
                    if (!Same(Expect(lazy, a, line)[1], Expect(lazy, b, line)[1])) diffs++;
                }
            }
            ok &= Report("D", "a line's expected win depends only on the symbols in its own cells", diffs == 0, $"{diffs} of {tested} swaps changed the result");
        }

        // E. Non-line part: non-line wins and the trigger depend only on scatter-symbol positions.
        {
            int diffs = 0, tested = 0;
            for (int i = 0; i < Samples; i++)
            {
                var a = RandomStops();
                int r = rng.Next(a.Length);
                var b = (uint[])a.Clone();
                if (!TrySwap(setup.Model.Reels[r], setup.NonLineKey(setup.Model.Reels[r]), a[r], rng, out b[r])) continue;
                tested++;
                var ea = Expect(lazy, a, -1); var eb = Expect(lazy, b, -1);
                if (!Same(ea[2], eb[2]) || !Same(ea[3], eb[3])) diffs++;
            }
            ok &= Report("E", "non-line wins and trigger depend only on scatter positions", diffs == 0, $"{diffs} of {tested} swaps changed the result");
        }

        // F. Exact enumeration of internal draws matches random play for a fixed set of stops.
        {
            var stops = Enumerable.Range(0, 400).Select(_ => RandomStops()).ToList();
            double exact = stops.Sum(s => Expect(lazy, s, -1)[0]) / stops.Count;
            double sum = 0, sum2 = 0; int n = 0;
            for (int rep = 0; rep < 50; rep++)
                foreach (var s in stops)
                {
                    host2.EnsureBase();
                    host2.Agent.BeginRandom(); host2.BeginAccounting();
                    host2.Session.Prepare(betPerLine, lines);
                    int played = (int)host2.Game.CurrentLayoutIndex;
                    host2.Session.Complete(s);
                    double v = host2.EndAccounting(played).Credited[baseIndex];
                    sum += v; sum2 += v * v; n++;
                }
            double mean = sum / n, se = Math.Sqrt(Math.Max(sum2 / n - mean * mean, 0) / n);
            bool pass = Math.Abs(mean - exact) <= 4 * se + 1e-9;
            ok &= Report("F", "internal-draw enumeration agrees with random play (same stops)", pass,
                $"exact {exact:F6} vs simulated {mean:F6} +/- {se:F6}");
        }

        // G. Feature-entry suppression (used by the non-line part) changes nothing but the transition:
        //    same credited base win, and the trigger pay condition fires exactly when the real engine
        //    leaves the base game. Tested on scatter configurations that actually trigger.
        {
            GameHost? suppressed = null;
            try { suppressed = GameHost.Load(dat, betPerLine, lines, 13, suppressFeatureEntry: true); }
            catch (NotSupportedException e) { Console.WriteLine("  [SKIP] G. " + e.Message); }
            if (suppressed is not null)
            {
                var quiet = new ActEnumerator(suppressed, new DrawPartition(suppressed.Game));
                var groups = setup.NonLineGroups();
                int tested = 0, diffs = 0, triggering = 0, attempts = 0;
                while (tested < Math.Max(8, Samples / 10) && attempts++ < 100_000)
                {
                    var stops = groups.Select(g => g[rng.Next(g.Count)].Representative).ToArray();
                    int scatterReels = setup.Model.Reels.Select((reel, r) => reel.Windows[stops[r]].Any(setup.ScatterSymbols.Contains) ? 1 : 0).Sum();
                    if (scatterReels < 3 && tested % 2 == 0) continue; // bias the sample towards triggering configurations
                    double c1 = 0, t1 = 0, c2 = 0, t2 = 0;
                    restoring.Enumerate(stops, (h, res, w) => { c1 += w * res.Credited[baseIndex]; t1 += res.LeftBase ? w : 0; });
                    quiet.Enumerate(stops, (h, res, w) => { c2 += w * res.Credited[baseIndex]; t2 += h.FeatureEntryTriggered() ? w : 0; if (res.LeftBase) t2 = double.NaN; });
                    tested++; if (t1 > 0) triggering++;
                    if (!Same(c1, c2) || !Same(t1, t2)) diffs++;
                }
                ok &= Report("G", "feature-entry suppression: same credited win, trigger detected exactly", diffs == 0,
                    $"{diffs} of {tested} configurations differ; {triggering} of them trigger; triggers: {string.Join(", ", suppressed.FeatureEntryTriggers)}");
            }
        }

        foreach (var w in partition.Warnings.Distinct()) Console.WriteLine("  note: " + w);
        return ok;
    }

    private static bool TrySwap(ReelInfo reel, Func<string[], string> key, uint stop, System.Random rng, out uint other)
    {
        string k = key(reel.Windows[stop]);
        var candidates = new List<uint>();
        foreach (var (p, _) in reel.Stops) if (p != stop && key(reel.Windows[p]) == k) candidates.Add(p);
        if (candidates.Count == 0) { other = stop; return false; }
        other = candidates[rng.Next(candidates.Count)];
        return true;
    }

    private static bool Report(string id, string text, bool pass, string detail)
    {
        Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {id}. {text} ({detail})");
        return pass;
    }
}
