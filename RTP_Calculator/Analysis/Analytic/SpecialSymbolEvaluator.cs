using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// General evaluator for everything that is not a line win (scatter pays, located / dynamic prizes,
/// trigger chains, conditional replacement cascades), used when the scatter-count model does not apply.
///
/// Each reel's stops are grouped by where "special" symbols land (symbols that any non-line pay
/// condition, scatter or replacement reads). Reels whose full content matters (hidden selector reels,
/// reels targeted by wildcard replacements, mystery source reels) are grouped by their full window.
/// Every combination of groups is then played through the real engine with all in-spin draws
/// enumerated exactly. The engine applies all game rules; the evaluator only supplies probabilities.
/// Results are kept per combination so random reel stops can be checked against them.
/// </summary>
public sealed class SpecialSymbolEvaluator
{
    private readonly ReelModel model;
    private readonly GameHost host;
    private readonly ActEnumerator enumerator;
    private readonly int baseIndex;
    private readonly List<Dictionary<string, int>> groupIndex = new List<Dictionary<string, int>>();
    private readonly Func<int, uint, string> keyOf;
    private readonly Dictionary<string, double[]> results = new Dictionary<string, double[]>(StringComparer.Ordinal);
    /// <summary>Optional extra metrics per act (e.g. which state the game moves to), appended after win and trigger.</summary>
    public Func<GameHost, double[]>? Extra { get; set; }
    /// <summary>Expected values of <see cref="Extra"/>.</summary>
    public double[] ExpectedExtra { get; private set; } = Array.Empty<double>();
    public int GroupChecks { get; private set; }
    public Action<string>? Progress { get; set; }

    public List<List<StopGroup>> Groups { get; }
    public long Configurations { get; }
    public long Acts => enumerator.Paths + extraWorkers.Sum(w => w.enumerator.Paths);
    public double ExpectedWin { get; private set; }
    public double TriggerProbability { get; private set; }

    public SpecialSymbolEvaluator(ReelModel model, HashSet<string> special, HashSet<int> fullReels, GameHost host,
        IReadOnlyList<uint> scenarioValues, IReadOnlyList<uint> scenarioRanges, long pathLimit, long configurationLimit)
        : this(model, (r, p) => fullReels.Contains(r) ? string.Join("|", model.Reels[r].Windows[p])
                                : string.Join("|", model.Reels[r].Windows[p].Select(s => special.Contains(s) ? s : ".")),
               host, scenarioValues, scenarioRanges, pathLimit, configurationLimit) { }

    /// <summary>General form: <paramref name="key"/>(reel, stop) groups stops that are equivalent for the non-line result.</summary>
    public SpecialSymbolEvaluator(ReelModel model, Func<int, uint, string> key, GameHost host,
        IReadOnlyList<uint> scenarioValues, IReadOnlyList<uint> scenarioRanges, long pathLimit, long configurationLimit)
    {
        this.model = model; this.host = host;
        baseIndex = host.BaseLayoutIndex;
        var coordinates = model.Reels.SelectMany(x => x.Coordinates).ToArray();
        int bi = host.BaseLayoutIndex;
        this.pathLimit = pathLimit; this.scenarioValues = scenarioValues; this.scenarioRanges = scenarioRanges; this.coordinates = coordinates;
        enumerator = NewEnumerator(host);
        keyOf = key;
        Groups = model.Reels.Select((reel, r) => GroupBy(reel, r)).ToList();
        foreach (var g in Groups) groupIndex.Add(g.Select((x, i) => (x.Key, i)).ToDictionary(t => t.Key, t => t.i, StringComparer.Ordinal));
        double configurations = Groups.Aggregate(1.0, (a, g) => a * g.Count);
        if (configurations > configurationLimit)
            throw new NotSupportedException($"Non-line part needs {configurations:N0} engine configurations (special-symbol groups {string.Join(" x ", Groups.Select(g => g.Count))}); limit {configurationLimit:N0}.");
        Configurations = (long)configurations;
    }

    private readonly long pathLimit;
    private readonly IReadOnlyList<uint> scenarioValues, scenarioRanges;
    private readonly AgtEngine.Core.Geometry.AGTCoordinate[] coordinates;
    private readonly List<(GameHost host, ActEnumerator enumerator)> extraWorkers = new List<(GameHost, ActEnumerator)>();
    /// <summary>Worker threads; each needs its own engine from <see cref="HostFactory"/>.</summary>
    public int Threads { get; set; } = 1;
    public Func<GameHost>? HostFactory { get; set; }
    /// <summary>Optional file keeping every configuration's measurement, so an interrupted run resumes.</summary>
    public string? CachePath { get; set; }

    private ActEnumerator NewEnumerator(GameHost h)
    {
        int bi = h.BaseLayoutIndex;
        var e = new ActEnumerator(h, new DrawPartition(h.Game, skipInitialiserTemplates: true))
        {
            PathLimitPerAct = pathLimit,
            // Screen after the act: outcomes of a pick that leave the same screen are merged (see ActEnumerator.Expect).
            StateSignature = g => string.Join("|", coordinates.Select(c => g.Game.Layouts.Entry(bi).Value.SymbolAtCoordinate(c).Name.CString))
        };
        e.SetScenario(scenarioValues, scenarioRanges);
        return e;
    }

    private List<StopGroup> GroupBy(ReelInfo reel, int r)
    {
        var groups = new Dictionary<string, StopGroup>(StringComparer.Ordinal); var order = new List<string>();
        foreach (var (p, count) in reel.Stops)
        {
            string k = keyOf(r, p);
            if (groups.TryGetValue(k, out var g)) g.Count += count;
            else { groups[k] = new StopGroup(k, p, count); order.Add(k); }
        }
        return order.Select(k => groups[k]).ToList();
    }

    public void Run(int groupChecks = 0)
    {
        int reels = Groups.Count;
        var choice = new int[reels];
        var stops = new uint[reels];
        var keys = new List<int[]>(); var jobStops = new List<uint[]>(); var weights = new List<double>();
        while (true)
        {
            double weight = 1;
            var st = new uint[reels];
            for (int r = 0; r < reels; r++)
            {
                var g = Groups[r][choice[r]];
                st[r] = g.Representative;
                weight *= (double)g.Count / model.Reels[r].Range;
            }
            keys.Add((int[])choice.Clone()); jobStops.Add(st); weights.Add(weight);
            int d = reels - 1;
            while (d >= 0 && ++choice[d] == Groups[d].Count) { choice[d] = 0; d--; }
            if (d < 0) break;
        }
        // Configurations in parallel, each worker on its own engine.
        int threads = HostFactory is null ? 1 : Math.Max(1, Math.Min(Threads, keys.Count / 20 + 1));
        while (extraWorkers.Count < threads - 1) { var h = HostFactory!(); extraWorkers.Add((h, NewEnumerator(h))); }
        var measured = new double[keys.Count][];
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var cached = new Dictionary<string, double[]>(StringComparer.Ordinal);
        if (CachePath is not null && File.Exists(CachePath))
            foreach (var line in File.ReadLines(CachePath))
            {
                var f = line.Split('\t');
                if (f.Length >= 2) cached[f[0]] = f.Skip(1).Select(x => double.Parse(x, inv)).ToArray();
            }
        var gate = new object();
        StreamWriter? cacheFile = CachePath is null ? null : new StreamWriter(CachePath, append: true);
        int next = -1, done = 0; Exception? failure = null;
        var tasks = Enumerable.Range(0, threads).Select(w => System.Threading.Tasks.Task.Run(() =>
        {
            var e = w == 0 ? enumerator : extraWorkers[w - 1].enumerator;
            try
            {
                while (failure is null)
                {
                    int i = System.Threading.Interlocked.Increment(ref next);
                    if (i >= keys.Count) return;
                    string k = (scenarioValues.Count > 0 ? "s" + string.Join(".", scenarioValues) + ":" : "") + string.Join(",", jobStops[i]);
                    if (cached.TryGetValue(k, out var hit)) { measured[i] = hit; continue; }
                    measured[i] = Measure(e, jobStops[i]);
                    if (cacheFile is not null)
                        lock (gate) { cacheFile.WriteLine(k + "\t" + string.Join("\t", measured[i].Select(x => x.ToString("R", inv)))); cacheFile.Flush(); }
                    int n = System.Threading.Interlocked.Increment(ref done);
                    if (Progress is not null && n % 200 == 0) Progress($"  general: {n:N0}/{Configurations:N0} configurations, {Acts:N0} engine acts");
                }
            }
            catch (Exception ex) { failure ??= ex; }
        })).ToArray();
        System.Threading.Tasks.Task.WaitAll(tasks);
        cacheFile?.Dispose();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        double[]? sum = null;
        for (int i = 0; i < keys.Count; i++)
        {
            var m = measured[i];
            results[string.Join(",", keys[i])] = m;
            sum ??= new double[m.Length];
            for (int x = 0; x < m.Length; x++) sum[x] += weights[i] * m[x];
        }
        ExpectedWin = sum![0]; TriggerProbability = sum[1];
        ExpectedExtra = sum.Skip(2).ToArray();
        // Stops in the same group must give the same result: other members of the groups are played.
        var rng = new System.Random(13);
        for (int c = 0; c < Math.Min(groupChecks, keys.Count); c++)
        {
            var key = keys[keys.Count <= groupChecks ? c : rng.Next(keys.Count)];
            for (int r = 0; r < reels; r++)
            {
                var g = Groups[r][key[r]];
                var members = model.Reels[r].Stops.Select(x => x.stop).Where(p => keyOf(r, p) == g.Key).ToList();
                stops[r] = members[rng.Next(members.Count)];
            }
            var m = Measure(enumerator, stops);
            var expected = results[string.Join(",", key)];
            GroupChecks++;
            for (int x = 0; x < m.Length; x++)
                if (Math.Abs(m[x] - expected[x]) > 1e-9 * Math.Max(1, Math.Abs(m[x])))
                    throw new NotSupportedException($"Stops grouped together give different results (stops {string.Join(",", stops)} vs {string.Join(",", key.Select((k, r) => Groups[r][k].Representative))}, metric {x}: {m[x]} vs {expected[x]}).");
        }
    }

    private double[] Measure(ActEnumerator e, uint[] stops) =>
        // Expected values over every in-spin draw, repeated picks factorised (ActEnumerator.Expect).
        e.Expect(stops, (h, res) =>
        {
            var extra = Extra?.Invoke(h) ?? Array.Empty<double>();
            var v = new double[2 + extra.Length];
            v[0] = (double)res.Credited[baseIndex] - h.AllLineWins();
            v[1] = h.EntryTriggered(res) ? 1.0 : 0.0;
            Array.Copy(extra, 0, v, 2, extra.Length);
            return v;
        });

    /// <summary>Model value for arbitrary stops: the measured value of the group combination they fall in.</summary>
    public (double win, double trigger) Predict(uint[] stops)
    {
        var idx = new int[stops.Length];
        for (int r = 0; r < stops.Length; r++) idx[r] = groupIndex[r][keyOf(r, stops[r])];
        var m = results[string.Join(",", idx)];
        return (m[0], m[1]);
    }
}
