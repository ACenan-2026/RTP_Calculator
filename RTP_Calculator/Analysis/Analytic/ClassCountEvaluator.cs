using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Non-line evaluator for coin / prize games (e.g. Cash Stacks): the non-line result depends on how many of
/// each relevant symbol land on a set of interchangeable reels, plus the exact content of a few other reels.
///
///  * Count reels: visible reels whose every cell has the same set of relevant symbols (from
///    <see cref="NonLineRelevance"/>). Each stop is reduced to a vector of class counts (how many SCAT, MEGA,
///    X, ... it shows) and the reels are combined exactly by convolution into a distribution of total vectors.
///  * Keyed reels: every other reel (hidden selector reels, a reel with its own located condition such as a
///    full wild stack). Their stops are grouped by the relevance key as in the general evaluator.
///
/// For every keyed combination and total vector the real engine plays one arrangement with all in-spin draws
/// enumerated exactly (mystery outcomes, coin values, bonus values). The engine applies every rule; the model
/// only assumes the result depends on the counts, not on where on the count reels they landed. That
/// assumption is checked: other arrangements (other reel assignments, other positions in the window) of the
/// same counts are played and must give identical results, otherwise the model is not used.
/// </summary>
public sealed class ClassCountEvaluator
{
    private readonly ReelModel model;
    private readonly List<ActEnumerator> workers = new List<ActEnumerator>();
    private readonly IReadOnlyList<uint> scenarioValues, scenarioRanges;
    private readonly long pathLimit;
    private readonly int baseIndex;
    private readonly int[] countReels;
    private readonly int[] keyedReels;
    private readonly string[] classes;
    private readonly Func<int, uint, string> keyOf;
    private readonly List<Dictionary<string, int>> keyedIndex = new List<Dictionary<string, int>>();
    // Per count reel: vector key -> stops showing it.
    private readonly List<Dictionary<string, List<uint>>> reelVectors = new List<Dictionary<string, List<uint>>>();
    private readonly Dictionary<string, Total> totals = new Dictionary<string, Total>(StringComparer.Ordinal);
    private readonly Dictionary<string, double[]> results = new Dictionary<string, double[]>(StringComparer.Ordinal);
    // Non-line pay condition families (name without a trailing _<cell> suffix), for the breakdown.
    private readonly string[] families;
    private readonly Dictionary<string, int> familyOf = new Dictionary<string, int>(StringComparer.Ordinal);

    private sealed class Total
    {
        public double Probability;
        public int[] Vector = Array.Empty<int>();
        /// <summary>Some assignments of per-reel vectors (one per count reel) that add up to this total.</summary>
        public List<string[]> Realisations = new List<string[]>();
    }

    public List<List<StopGroup>> KeyedGroups { get; }
    public long Configurations { get; }
    public long Acts => workers.Sum(w => w.Paths);
    public int InvarianceChecks { get; private set; }
    public double ExpectedWin { get; private set; }
    public double TriggerProbability { get; private set; }
    /// <summary>Expected credits per non-line pay condition family (as reported by the engine's win records).</summary>
    public Dictionary<string, double> Breakdown { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
    public string Description { get; }
    /// <summary>Optional progress messages.</summary>
    public Action<string>? Progress { get; set; }
    /// <summary>Worker threads; each needs its own engine from <see cref="HostFactory"/>.</summary>
    public int Threads { get; set; } = 1;
    public Func<GameHost>? HostFactory { get; set; }
    /// <summary>
    /// Optional file that keeps every engine measurement (keyed by reel stops), so an interrupted run resumes
    /// where it stopped. Only valid for the same game file, bet and lines.
    /// </summary>
    public string? CachePath { get; set; }
    public long CachedMeasurements { get; private set; }

    public ClassCountEvaluator(ReelModel model, NonLineRelevance relevance, GameHost host,
        IReadOnlyList<uint> scenarioValues, IReadOnlyList<uint> scenarioRanges, long pathLimit, long configurationLimit)
    {
        this.model = model;
        this.scenarioValues = scenarioValues; this.scenarioRanges = scenarioRanges; this.pathLimit = pathLimit;
        baseIndex = host.BaseLayoutIndex;
        var baseLayout = host.Game.Layouts.Entry(host.BaseLayoutIndex).Value;
        var fam = new List<string>();
        foreach (var pc in baseLayout.PayConditions)
        {
            if (!(pc.Value.Scatter || pc.Value.Located || pc.Value.CombinedWin || pc.Value.IsPatternPayC)) continue;
            string f = Family(pc.Key);
            if (!familyOf.ContainsKey(pc.Key)) { int i = fam.IndexOf(f); if (i < 0) { i = fam.Count; fam.Add(f); } familyOf[pc.Key] = i; }
        }
        families = fam.ToArray();
        workers.Add(NewWorker(host));
        keyOf = (r, p) => relevance.Key(r, model.Reels[r].WindowSymbols[p]);

        // Count reels: visible multi-cell reels whose cells all share one relevant-symbol set; take the
        // largest family of such reels.
        var signature = new Dictionary<int, string>();
        for (int r = 0; r < model.Reels.Count; r++)
        {
            var reel = model.Reels[r];
            if (reel.Hidden || reel.Coordinates.Length < 2) continue;
            string? sig = null; bool ok = true;
            for (int w = 0; w < reel.Coordinates.Length && ok; w++)
            {
                if (relevance.FullCells.Contains((r, w)) || !relevance.CellSymbols.TryGetValue((r, w), out var set)) { ok = false; break; }
                string s = string.Join(",", set.OrderBy(x => x, StringComparer.Ordinal));
                if (sig is null) sig = s; else if (sig != s) ok = false;
            }
            if (ok && sig is not null) signature[r] = sig;
        }
        var family = signature.GroupBy(kv => kv.Value).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (family is null || family.Count() < 2)
            throw new NotSupportedException("class-count model: fewer than two reels share the same relevant symbols.");
        var relevantSet = relevance.CellSymbols[(family.First().Key, 0)];
        countReels = family.Select(kv => kv.Key).OrderBy(r => r).ToArray();
        keyedReels = Enumerable.Range(0, model.Reels.Count).Except(countReels).ToArray();

        // Classes: relevant symbols that actually appear on the count reels (by name; matched like the relevance key).
        var present = new SortedSet<string>(StringComparer.Ordinal);
        foreach (int r in countReels)
            foreach (var window in model.Reels[r].WindowSymbols)
                foreach (var s in window)
                    if (relevantSet.Contains(s.Name.CString) || (!s.AliasName.IsEmpty && relevantSet.Contains(s.AliasName.CString)))
                        present.Add(s.Name.CString);
        classes = present.ToArray();
        var classIndex = classes.Select((c, i) => (c, i)).ToDictionary(t => t.c, t => t.i, StringComparer.Ordinal);

        foreach (int r in countReels)
        {
            var reel = model.Reels[r]; var map = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
            for (uint p = 0; p < reel.Range; p++)
            {
                var v = new int[classes.Length];
                foreach (var s in reel.WindowSymbols[p])
                    if (classIndex.TryGetValue(s.Name.CString, out int ci)) v[ci]++;
                string k = VectorKey(v);
                if (!map.TryGetValue(k, out var list)) map[k] = list = new List<uint>();
                list.Add(p);
            }
            reelVectors.Add(map);
        }

        KeyedGroups = keyedReels.Select(r => GroupBy(model.Reels[r], r)).ToList();
        foreach (var g in KeyedGroups) keyedIndex.Add(g.Select((x, i) => (x.Key, i)).ToDictionary(t => t.Key, t => t.i, StringComparer.Ordinal));

        Convolve();
        double keyedCombos = KeyedGroups.Aggregate(1.0, (a, g) => a * g.Count);
        double configurations = keyedCombos * totals.Count;
        if (configurations > configurationLimit)
            throw new NotSupportedException($"class-count model needs {configurations:N0} engine configurations ({keyedCombos:N0} keyed combinations x {totals.Count} count vectors); limit {configurationLimit:N0}.");
        Configurations = (long)configurations;
        Description = $"count reels {string.Join(",", countReels)} over classes [{string.Join(" ", classes)}] -> {totals.Count} count vectors; " +
                      $"keyed reels {string.Join(",", keyedReels)} ({string.Join(" x ", KeyedGroups.Select(g => g.Count))} groups)";
    }

    private static string VectorKey(int[] v) => string.Join(".", v);

    private List<StopGroup> GroupBy(ReelInfo reel, int r)
    {
        var groups = new Dictionary<string, StopGroup>(StringComparer.Ordinal); var order = new List<string>();
        for (uint p = 0; p < reel.Range; p++)
        {
            string k = keyOf(r, p);
            if (groups.TryGetValue(k, out var g)) g.Count++;
            else { groups[k] = new StopGroup(k, p, 1); order.Add(k); }
        }
        return order.Select(k => groups[k]).ToList();
    }

    /// <summary>Exact distribution of the total class-count vector over the count reels.</summary>
    private void Convolve()
    {
        const int keepRealisations = 4;
        var current = new Dictionary<string, Total>(StringComparer.Ordinal)
        {
            [VectorKey(new int[classes.Length])] = new Total { Probability = 1, Vector = new int[classes.Length], Realisations = { Array.Empty<string>() } }
        };
        for (int i = 0; i < countReels.Length; i++)
        {
            double range = model.Reels[countReels[i]].Range;
            var next = new Dictionary<string, Total>(StringComparer.Ordinal);
            foreach (var t in current.Values)
                foreach (var (vk, stops) in reelVectors[i])
                {
                    var add = vk.Split('.').Select(int.Parse).ToArray();
                    var v = t.Vector.Zip(add, (a, b) => a + b).ToArray();
                    string k = VectorKey(v);
                    if (!next.TryGetValue(k, out var n)) next[k] = n = new Total { Vector = v };
                    n.Probability += t.Probability * stops.Count / range;
                    foreach (var real in t.Realisations)
                        if (n.Realisations.Count < keepRealisations) n.Realisations.Add(real.Append(vk).ToArray());
                }
            current = next;
        }
        foreach (var kv in current) totals[kv.Key] = kv.Value;
    }

    private ActEnumerator NewWorker(GameHost h)
    {
        var coordinates = model.Reels.SelectMany(x => x.Coordinates).ToArray();
        var e = new ActEnumerator(h, new DrawPartition(h.Game, skipInitialiserTemplates: true))
        {
            PathLimitPerAct = pathLimit,
            // Screen after the act: outcomes of a pick that leave the same screen are merged.
            StateSignature = g => string.Join("|", coordinates.Select(c => g.Game.Layouts.Entry(g.BaseLayoutIndex).Value.SymbolAtCoordinate(c).Name.CString))
        };
        e.SetScenario(scenarioValues, scenarioRanges);
        return e;
    }

    public void Run(int invarianceChecks = 100, int seed = 7)
    {
        var rng = new System.Random(seed);
        // Jobs: one arrangement per keyed combination and count vector.
        var jobs = new List<(string key, uint[] stops, double weight)>();
        var choice = new int[keyedReels.Length];
        while (true)
        {
            var stops = new uint[model.Reels.Count];
            double weight = 1;
            for (int i = 0; i < keyedReels.Length; i++)
            {
                var g = KeyedGroups[i][choice[i]];
                stops[keyedReels[i]] = g.Representative;
                weight *= (double)g.Count / model.Reels[keyedReels[i]].Range;
            }
            string combo = string.Join(",", choice);
            foreach (var (tk, t) in totals)
            {
                var s = (uint[])stops.Clone();
                Place(s, t.Realisations[0], null);
                jobs.Add((combo + "|" + tk, s, weight * t.Probability));
            }
            int d = keyedReels.Length - 1;
            while (d >= 0 && ++choice[d] == KeyedGroups[d].Count) { choice[d] = 0; d--; }
            if (d < 0) break;
        }
        // Invariance checks: other stops of the same keyed groups, other reel assignments and window positions.
        var checks = new List<(string key, uint[] stops)>();
        for (int c = 0; c < Math.Min(invarianceChecks, jobs.Count); c++)
        {
            var job = jobs.Count <= invarianceChecks ? jobs[c] : jobs[rng.Next(jobs.Count)];
            var parts = job.key.Split('|');
            var idx = parts[0].Length == 0 ? Array.Empty<int>() : parts[0].Split(',').Select(int.Parse).ToArray();
            var stops = new uint[model.Reels.Count];
            for (int i = 0; i < keyedReels.Length; i++)
            {
                var g = KeyedGroups[i][idx[i]];
                var same = Enumerable.Range(0, (int)model.Reels[keyedReels[i]].Range).Where(p => keyOf(keyedReels[i], (uint)p) == g.Key).ToList();
                stops[keyedReels[i]] = (uint)same[rng.Next(same.Count)];
            }
            var t = totals[parts[1]];
            Place(stops, t.Realisations[rng.Next(t.Realisations.Count)], rng);
            checks.Add((job.key, stops));
        }

        var measured = MeasureAll(jobs.Select(j => j.stops).Concat(checks.Select(c => c.stops)).ToList());
        var sum = new double[2 + families.Length];
        foreach (var (key, stops, weight) in jobs)
        {
            var m = measured[StopsKey(stops)];
            results[key] = m;
            for (int x = 0; x < sum.Length; x++) sum[x] += weight * m[x];
        }
        ExpectedWin = sum[0]; TriggerProbability = sum[1];
        for (int f = 0; f < families.Length; f++) if (sum[2 + f] != 0) Breakdown[families[f]] = sum[2 + f];
        double reported = sum.Skip(2).Sum();
        if (!Same(reported, ExpectedWin)) Breakdown["(credited, not in win records)"] = ExpectedWin - reported;
        foreach (var (key, stops) in checks)
        {
            var m = measured[StopsKey(stops)];
            var expected = results[key];
            InvarianceChecks++;
            if (!Same(m[0], expected[0]) || !Same(m[1], expected[1]))
                throw new NotSupportedException($"class-count model: result depends on where the counted symbols land (counts {key.Split('|')[1]} [{string.Join(" ", classes)}], stops {StopsKey(stops)}: win {m[0]} vs {expected[0]}, trigger {m[1]} vs {expected[1]}).");
        }
    }

    private string CountsOf(uint[] stops)
    {
        var v = new int[classes.Length];
        for (int i = 0; i < countReels.Length; i++)
            v = v.Zip(reelVectors[i].First(kv => kv.Value.Contains(stops[countReels[i]])).Key.Split('.').Select(int.Parse), (a, b) => a + b).ToArray();
        return string.Join(" ", classes.Select((c, i) => $"{c}={v[i]}"));
    }

    /// <summary>WinCS_42 -> WinCS, WinBonus_BN_Mini_32 -> WinBonus_BN_Mini, K3_FEAT_St -> K3_FEAT_St.</summary>
    private static string Family(string name) => System.Text.RegularExpressions.Regex.Replace(name, "_[0-9]+$", "");

    private static string StopsKey(uint[] stops) => string.Join(",", stops);

    /// <summary>Engine measurement for every distinct stop vector, in parallel, resuming from the cache file.</summary>
    private Dictionary<string, double[]> MeasureAll(List<uint[]> all)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var done = new System.Collections.Concurrent.ConcurrentDictionary<string, double[]>(StringComparer.Ordinal);
        int width = 2 + families.Length;
        if (CachePath is not null && File.Exists(CachePath))
            foreach (var line in File.ReadLines(CachePath))
            {
                var f = line.Split('\t');
                if (f.Length != 1 + width) continue;
                var v = new double[width]; bool ok = true;
                for (int x = 0; x < width && ok; x++) ok = double.TryParse(f[1 + x], System.Globalization.NumberStyles.Float, inv, out v[x]);
                if (ok) done[f[0]] = v;
            }
        var todo = all.GroupBy(StopsKey).Select(g => g.First()).Where(s => !done.ContainsKey(StopsKey(s))).ToList();
        CachedMeasurements = all.Select(StopsKey).Distinct().Count() - todo.Count;
        int threads = Math.Max(1, HostFactory is null ? 1 : Math.Min(Threads, todo.Count));
        while (workers.Count < threads) workers.Add(NewWorker(HostFactory!()));
        var gate = new object(); int next = -1, finished = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew(); double lastReport = 0;
        StreamWriter? cache = CachePath is null ? null : new StreamWriter(CachePath, append: true);
        Exception? failure = null;
        var tasks = Enumerable.Range(0, threads).Select(w => System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    int i = System.Threading.Interlocked.Increment(ref next);
                    if (i >= todo.Count || failure is not null) return;
                    var t0 = System.Diagnostics.Stopwatch.StartNew(); long a0 = workers[w].Paths;
                    if (todo.Count <= 20) Progress?.Invoke($"  measuring {StopsKey(todo[i])} ({CountsOf(todo[i])})");
                    var r = Measure(workers[w], todo[i]);
                    if (t0.Elapsed.TotalSeconds > 5) Progress?.Invoke($"  slow configuration {StopsKey(todo[i])} ({CountsOf(todo[i])}): {workers[w].Paths - a0:N0} engine acts, {t0.Elapsed.TotalSeconds:F1} s");
                    string k = StopsKey(todo[i]);
                    done[k] = r;
                    lock (gate)
                    {
                        finished++;
                        cache?.WriteLine(k + "\t" + string.Join("\t", r.Select(x => x.ToString("R", inv))));
                        cache?.Flush();
                        if (sw.Elapsed.TotalSeconds - lastReport > 10)
                        {
                            lastReport = sw.Elapsed.TotalSeconds;
                            Progress?.Invoke($"class-count: {finished:N0}/{todo.Count:N0} engine configurations, {Acts:N0} engine acts");
                        }
                    }
                }
            }
            catch (Exception e) { failure ??= e; }
        })).ToArray();
        System.Threading.Tasks.Task.WaitAll(tasks);
        cache?.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return new Dictionary<string, double[]>(done, StringComparer.Ordinal);
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));

    private void Place(uint[] stops, string[] realisation, System.Random? rng)
    {
        for (int i = 0; i < countReels.Length; i++)
        {
            var list = reelVectors[i][realisation[i]];
            stops[countReels[i]] = rng is null ? list[0] : list[rng.Next(list.Count)];
        }
    }

    private double[] Measure(ActEnumerator enumerator, uint[] stops) => enumerator.Expect(stops, (h, res) =>
    {
        var m = new double[2 + families.Length];
        m[0] = (double)res.Credited[baseIndex] - h.AllLineWins();
        m[1] = (h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : res.LeftBase) ? 1.0 : 0.0;
        var layout = h.Game.Layouts.Entry(baseIndex).Value;
        void Add(IReadOnlyList<AgtEngine.Core.ReelGame.PayLineWinInfo> wins)
        {
            foreach (var w in wins) if (familyOf.TryGetValue(w.PayconditionName.CString, out int f)) m[2 + f] += w.WonCredits;
        }
        Add(layout.WinningLocatedPayconditions); Add(layout.WinningScatteredPayconditions); Add(layout.CombinedWinPayconditions);
        return m;
    });

    public string FactorSummary => $"{workers.Sum(w => w.MultisetRuns):N0} exchangeable pick runs, " +
                                   $"{workers.Sum(w => w.FactorChecks):N0} checks, {workers.Sum(w => w.FactorFailures):N0} runs multiplied out";

    /// <summary>Model value for arbitrary stops.</summary>
    public (double win, double trigger) Predict(uint[] stops)
    {
        var m = PredictAll(stops);
        return (m[0], m[1]);
    }

    private double[] PredictAll(uint[] stops)
    {
        var idx = new int[keyedReels.Length];
        for (int i = 0; i < keyedReels.Length; i++) idx[i] = keyedIndex[i][keyOf(keyedReels[i], stops[keyedReels[i]])];
        var v = new int[classes.Length];
        for (int i = 0; i < countReels.Length; i++)
        {
            var add = reelVectors[i].First(kv => kv.Value.Contains(stops[countReels[i]])).Key.Split('.').Select(int.Parse);
            v = v.Zip(add, (a, b) => a + b).ToArray();
        }
        return results[string.Join(",", idx) + "|" + VectorKey(v)];
    }
}
