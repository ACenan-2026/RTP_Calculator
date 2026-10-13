using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Line pays when replacements change payline symbols in ways the rule-based models do not cover (e.g. Galaxy
/// Gold free games: random stacked-wild columns, wilds that expand over their reel, coins on wild reels turned
/// into wild coins). The engine itself measures what each reel ends up showing:
///
///  1. Global draws: in-spin draws whose ranges do not depend on the reel stops (e.g. which columns become wild)
///     come first in every act. Their outcomes are enumerated and grouped by the screen they produce, giving a
///     few "global scenarios" with probabilities.
///  2. For every global scenario, reel and stop, the engine plays the spin (other reels at fixed stops, every
///     remaining in-spin draw enumerated) and records the probability of each final symbol in each window.
///  3. Lines: given the scenario, the cells of a line come from different reels, so they are independent; each
///     cell's distribution is the stop-weighted average of step 2, and the line's expected pay comes from the
///     engine's matcher (<see cref="LineEvaluator"/>).
///
/// The model assumes a reel's final symbols depend only on its own stop and the global scenario ("reel-local"
/// replacements). That is checked: random other-reel stops must give the same line-relevant result.
/// </summary>
public sealed class ReelTransformLines
{
    private readonly GameHost host;
    private readonly BaseSetup setup;
    private readonly LineEvaluator lineEval;
    private readonly ReelModel model;
    private readonly DrawPartition partition;
    private readonly List<int> lineReels;
    private readonly Dictionary<string, int> symbolIndex = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly List<AGTSymbol> symbols = new List<AGTSymbol>();
    private readonly uint[] baseline;

    public sealed class GlobalScenario
    {
        public List<uint> Values = new List<uint>();
        public List<uint> Ranges = new List<uint>();
        public double Probability;
        public string Key = "";
        /// <summary>Stops of the reels that are not on any payline (hidden selector reels etc.).</summary>
        public uint[] Stops = Array.Empty<uint>();
    }

    public List<GlobalScenario> Scenarios { get; } = new List<GlobalScenario>();
    /// <summary>[scenario][reel][stop index in ReelInfo.Stops][window] -> (probability, symbol index)</summary>
    private readonly Dictionary<(int g, int r), List<(double p, int s)>[][]> measured = new Dictionary<(int, int), List<(double, int)>[][]>();
    public long Plays => enumerator.Paths;
    public int Checks { get; private set; }
    public string Description { get; private set; } = "";
    public Action<string>? Progress { get; set; }
    private readonly ActEnumerator enumerator;
    public int ScenarioLimit { get; set; } = 5000;

    public ReelTransformLines(GameHost host, BaseSetup setup, LineEvaluator lineEval, ISet<string>? busy = null)
    {
        this.host = host; this.setup = setup; this.lineEval = lineEval;
        model = setup.Model;
        partition = new DrawPartition(host.Game, skipInitialiserTemplates: true);
        enumerator = new ActEnumerator(host, partition) { PathLimitPerAct = 200_000 };
        lineReels = setup.LineCells.Values.SelectMany(c => c).Select(c => c.reel).Distinct().OrderBy(r => r).ToList();
        // Other reels are held at a quiet stop (no symbol that starts a replacement), so measuring one reel makes few draws.
        baseline = model.Reels.Select(r =>
        {
            foreach (var (stop, _) in r.Stops)
                if (busy is null || !r.Windows[stop].Any(busy.Contains)) return stop;
            return r.Stops[0].stop;
        }).ToArray();
    }

    private int Index(AGTSymbol s)
    {
        string key = s.Name.CString + (s.Qualifier.IsSubstitute ? "*" : "") + (s.Qualifier.IsNotSubstitutable ? "!" : "");
        if (!symbolIndex.TryGetValue(key, out int i))
        {
            i = symbols.Count; symbolIndex[key] = i; symbols.Add(new AGTSymbol(s));
        }
        return i;
    }

    private string Screen(GameHost h)
    {
        var layout = h.Game.Layouts.Entry(h.BaseLayoutIndex).Value;
        return string.Join("|", model.Reels.SelectMany(r => r.Coordinates).Select(c => layout.SymbolAtCoordinate(c).Name.CString));
    }

    /// <summary>Find the global draws and enumerate their outcomes, grouped by the screen they give.</summary>
    private void DiscoverGlobalDraws()
    {
        // Reels that are not on any payline are part of the scenario: every combination of their windows.
        var otherReels = Enumerable.Range(0, model.Reels.Count).Where(r => !lineReels.Contains(r)).ToList();
        var otherGroups = otherReels.Select(r => StopGrouping.Group(model.Reels[r], w => string.Join("|", w), w => 0)).ToList();
        double combos = otherGroups.Aggregate(1.0, (a, g) => a * g.Count);
        if (combos > 256) throw new NotSupportedException($"Reels off the paylines show {combos:N0} window combinations; too many for the reel-transform line model.");
        var choice = new int[otherReels.Count];
        int totalLeaves = 0;
        while (true)
        {
            var stops = (uint[])baseline.Clone(); double weight = 1;
            for (int i = 0; i < otherReels.Count; i++)
            {
                var g = otherGroups[i][choice[i]];
                stops[otherReels[i]] = g.Representative;
                weight *= (double)g.Count / model.Reels[otherReels[i]].Range;
            }
            totalLeaves += DiscoverGlobalDrawsAt(stops, weight);
            int d = otherReels.Count - 1;
            while (d >= 0 && ++choice[d] == otherGroups[d].Count) { choice[d] = 0; d--; }
            if (d < 0) break;
        }
        Description = $"{otherGroups.Aggregate(1, (a, g) => a * g.Count)} off-line reel combination(s), {totalLeaves} global draw outcome(s) in {Scenarios.Count} screen group(s)";
    }

    private int DiscoverGlobalDrawsAt(uint[] fixedStops, double weight)
    {
        var lineOnly = lineReels;
        var rng = new System.Random(5);
        List<uint>? common = null;
        for (int t = 0; t < 60; t++)
        {
            var stops = (uint[])fixedStops.Clone();
            foreach (int r in lineOnly) stops[r] = model.Reels[r].Stops[rng.Next(model.Reels[r].Stops.Count)].stop;
            if (t == 0) stops = fixedStops;
            var ranges = PlayRanges(setup.PrepareValues, setup.PrepareRanges, stops);
            var tail = ranges.Skip(setup.PrepareValues.Count).ToList();
            if (common is null) common = tail;
            else
            {
                int n = 0;
                while (n < common.Count && n < tail.Count && common[n] == tail[n]) n++;
                common = common.Take(n).ToList();
            }
        }
        int k = common!.Count;
        // Enumerate the k global draws depth-first (their ranges can depend on earlier global draws).
        var leaves = new List<GlobalScenario>();
        void Walk(List<uint> values, List<uint> ranges, double p)
        {
            if (values.Count == setup.PrepareValues.Count + k)
            {
                leaves.Add(new GlobalScenario { Values = values, Ranges = ranges, Probability = p });
                if (leaves.Count > ScenarioLimit) throw new NotSupportedException($"More than {ScenarioLimit} global in-spin draw outcomes.");
                return;
            }
            var played = PlayRanges(values, ranges, fixedStops);
            uint range = played[values.Count];
            var cells = partition.Cells(range);
            for (int c = 0; c < cells.Length; c++)
            {
                uint end = c + 1 < cells.Length ? cells[c + 1] : range;
                Walk(new List<uint>(values) { cells[c] }, new List<uint>(ranges) { range }, p * (end - cells[c]) / range);
            }
        }
        Walk(new List<uint>(setup.PrepareValues), new List<uint>(setup.PrepareRanges), weight);
        // Group by the screen of the baseline spin (e.g. which columns became wild).
        var byKey = new Dictionary<string, GlobalScenario>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            host.EnsureBase(true);
            host.Agent.BeginScripted(leaf.Values, leaf.Ranges, leaf.Values.Count);
            host.BeginAccounting();
            host.Session.Prepare(host.BetPerLine, host.Lines);
            int played = (int)host.Game.CurrentLayoutIndex;
            host.Session.Complete(fixedStops);
            host.EndAccounting(played);
            string key = Screen(host);
            if (byKey.TryGetValue(key, out var g)) g.Probability += leaf.Probability;
            else { leaf.Key = key; leaf.Stops = (uint[])fixedStops.Clone(); byKey[key] = leaf; Scenarios.Add(leaf); }
        }
        return leaves.Count;
    }

    private List<uint> PlayRanges(List<uint> values, List<uint> ranges, uint[] stops)
    {
        host.EnsureBase(true);
        host.Agent.BeginScripted(values, ranges, values.Count);
        host.BeginAccounting();
        var req = host.Session.Prepare(host.BetPerLine, host.Lines);
        int played = (int)host.Game.CurrentLayoutIndex;
        host.Session.Complete(stops);
        host.EndAccounting(played);
        return new List<uint>(host.Agent.Ranges);
    }

    /// <summary>Per-window symbol distributions of reel <paramref name="r"/> at <paramref name="stops"/> in scenario <paramref name="g"/>.</summary>
    private List<(double p, int s)>[] MeasureReel(int g, int r, uint[] stops)
    {
        var sc = Scenarios[g];
        enumerator.SetScenario(sc.Values, sc.Ranges);
        try { return MeasureReelCore(g, r, stops); }
        catch (ScriptDivergenceException e) { throw new InvalidOperationException($"Replay diverged measuring reel {r} at stops {string.Join(",", stops)} (scenario {g}): {e.Message}", e); }
    }

    private List<(double p, int s)>[] MeasureReelCore(int g, int r, uint[] stops)
    {
        int windows = model.Reels[r].Coordinates.Length;
        var coords = model.Reels[r].Coordinates;
        const int width = 256;
        // Only what lines can tell apart matters: symbols that take no part in line wins share one class. Outcomes
        // of in-spin picks that give this reel the same line classes are merged by the enumerator.
        int LineClass(AGTSymbol sym)
        {
            int i = Index(sym);
            if (TouchesLines(i)) return i;
            if (blankIndex < 0) blankIndex = i;
            return blankIndex;
        }
        enumerator.StateSignature = h =>
        {
            var layout = h.Game.Layouts.Entry(h.BaseLayoutIndex).Value;
            return string.Join("|", coords.Select(c => LineClass(layout.SymbolAtCoordinate(c))));
        };
        var e = enumerator.Expect(stops, (h, res) =>
        {
            var v = new double[windows * width];
            var layout = h.Game.Layouts.Entry(h.BaseLayoutIndex).Value;
            for (int w = 0; w < windows; w++)
            {
                int i = LineClass(layout.SymbolAtCoordinate(coords[w]));
                if (i >= width) throw new NotSupportedException("More than 256 distinct final symbols.");
                v[w * width + i] = 1;
            }
            return v;
        });
        var result = new List<(double, int)>[windows];
        for (int w = 0; w < windows; w++)
        {
            result[w] = new List<(double, int)>();
            for (int i = 0; i < width; i++) if (Math.Abs(e[w * width + i]) > 1e-15) result[w].Add((e[w * width + i], i));
        }
        return result;
    }

    public int SkippedChecks { get; private set; }

    public void Calibrate(int localityChecks = 12)
    {
        DiscoverGlobalDraws();
        Progress?.Invoke("reel-transform lines: " + Description);
        foreach (var sc in Scenarios) Progress?.Invoke($"  scenario p={sc.Probability:G6} draws [{string.Join(",", sc.Values.Zip(sc.Ranges, (v, r) => v + "/" + r))}] off-line stops {string.Join(",", sc.Stops)}");
        for (int g = 0; g < Scenarios.Count; g++)
            foreach (int r in lineReels)
            {
                var reel = model.Reels[r];
                var perStop = new List<(double, int)>[reel.Stops.Count][];
                for (int i = 0; i < reel.Stops.Count; i++)
                {
                    var stops = (uint[])Scenarios[g].Stops.Clone(); stops[r] = reel.Stops[i].stop;
                    perStop[i] = MeasureReel(g, r, stops);
                }
                measured[(g, r)] = perStop;
                Progress?.Invoke($"  scenario {g + 1}/{Scenarios.Count}, reel {r}: {reel.Stops.Count} stops measured, {Plays:N0} engine plays, {enumerator.Replays} replays (last from {enumerator.LastReplayState})");
            }
        // Reel-locality: other reels at random stops must not change what a reel shows (as far as lines can tell).
        var rng = new System.Random(9);
        for (int c = 0; c < localityChecks; c++)
        {
            int g = rng.Next(Scenarios.Count), r = lineReels[rng.Next(lineReels.Count)];
            int i = rng.Next(model.Reels[r].Stops.Count);
            var stops = (uint[])Scenarios[g].Stops.Clone();
            foreach (int lr in lineReels) stops[lr] = model.Reels[lr].Stops[rng.Next(model.Reels[lr].Stops.Count)].stop;
            stops[r] = model.Reels[r].Stops[i].stop;
            List<(double p, int s)>[] other;
            long limit = enumerator.PathLimitPerAct; enumerator.PathLimitPerAct = 20_000;
            try { other = MeasureReel(g, r, stops); }
            catch (NotSupportedException) { SkippedChecks++; continue; } // too many in-spin draws on this screen to check cheaply
            finally { enumerator.PathLimitPerAct = limit; }
            Checks++;
            Progress?.Invoke($"  reel-locality check {Checks}: reel {r}, stops {string.Join(",", stops)} ok so far");
            for (int w = 0; w < other.Length; w++)
                if (!SameForLines(other[w], measured[(g, r)][i][w]))
                    throw new NotSupportedException($"Reel {r} window {w}: its final symbols depend on other reels (stops {string.Join(",", stops)}); reel-transform line model not valid.");
        }
    }

    private bool SameForLines(List<(double p, int s)> a, List<(double p, int s)> b)
    {
        Dictionary<string, double> Class(List<(double p, int s)> d)
        {
            var m = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var (p, s) in d)
            {
                var sym = symbols[s];
                string k = lineEval.TouchesLines(sym) ? sym.Name.CString + (sym.Qualifier.IsSubstitute ? "*" : "") : ".";
                m[k] = (m.TryGetValue(k, out var x) ? x : 0) + p;
            }
            return m;
        }
        var ca = Class(a); var cb = Class(b);
        foreach (var k in ca.Keys.Union(cb.Keys))
            if (Math.Abs((ca.TryGetValue(k, out var x) ? x : 0) - (cb.TryGetValue(k, out var y) ? y : 0)) > 1e-9) return false;
        return true;
    }

    private readonly Dictionary<int, bool> touches = new Dictionary<int, bool>();
    private int blankIndex = -1;
    private bool TouchesLines(int s) => touches.TryGetValue(s, out var t) ? t : touches[s] = lineEval.TouchesLines(symbols[s]);

    private List<(double p, AGTSymbol s)> Cell(int g, int r, int w, int? stopIndex)
    {
        var reel = model.Reels[r];
        var map = new Dictionary<int, double>();
        if (stopIndex is int one)
            foreach (var (p, s) in measured[(g, r)][one][w]) map[s] = (map.TryGetValue(s, out var x) ? x : 0) + p;
        else
            for (int i = 0; i < reel.Stops.Count; i++)
            {
                double weight = (double)reel.Stops[i].count / reel.Range;
                foreach (var (p, s) in measured[(g, r)][i][w]) map[s] = (map.TryGetValue(s, out var x) ? x : 0) + weight * p;
            }
        // Symbols that cannot take part in a line win are all equivalent here: merge them (fewer tuples to evaluate).
        var merged = new Dictionary<int, double>();
        int blank = -1;
        foreach (var (k, p) in map)
        {
            int key = k;
            if (!TouchesLines(k)) { if (blank < 0) blank = k; key = blank; }
            merged[key] = (merged.TryGetValue(key, out var x) ? x : 0) + p;
        }
        return merged.Select(kv => (kv.Value, symbols[kv.Key])).ToList();
    }

    /// <summary>Expected line win (credits) per spin.</summary>
    public double ExpectedLineWin(Func<List<List<(double p, AGTSymbol s)>>, double> evaluate)
    {
        double total = 0;
        for (int g = 0; g < Scenarios.Count; g++)
            foreach (int line in setup.BetLines)
                total += Scenarios[g].Probability * evaluate(setup.LineCells[line].Select(c => Cell(g, c.reel, c.window, null)).ToList());
        return total;
    }

    /// <summary>Expected win on one line for given stops (for verification).</summary>
    public double PredictLine(int line, uint[] stops, Func<List<List<(double p, AGTSymbol s)>>, double> evaluate)
    {
        // Only the scenarios whose off-line reels show what these stops show, renormalised.
        var offLine = Enumerable.Range(0, model.Reels.Count).Where(r => !lineReels.Contains(r)).ToList();
        bool Matches(GlobalScenario g) => offLine.All(r => model.Reels[r].Windows[g.Stops[r]].SequenceEqual(model.Reels[r].Windows[stops[r]]));
        double total = 0, mass = 0;
        for (int g = 0; g < Scenarios.Count; g++)
        {
            if (!Matches(Scenarios[g])) continue;
            mass += Scenarios[g].Probability;
            total += Scenarios[g].Probability * evaluate(setup.LineCells[line].Select(c =>
            {
                var reel = model.Reels[c.reel];
                int idx = -1;
                for (int i = 0; i < reel.Stops.Count && idx < 0; i++)
                    if (reel.Windows[reel.Stops[i].stop].SequenceEqual(reel.Windows[stops[c.reel]])) idx = i;
                return Cell(g, c.reel, c.window, idx);
            }).ToList());
        }
        return mass > 0 ? total / mass : double.NaN;
    }
}
