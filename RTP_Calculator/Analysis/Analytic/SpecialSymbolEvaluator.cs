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
    private readonly Dictionary<string, (double win, double trigger)> results = new Dictionary<string, (double, double)>(StringComparer.Ordinal);

    public List<List<StopGroup>> Groups { get; }
    public long Configurations { get; }
    public long Acts => enumerator.Paths;
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
        enumerator = new ActEnumerator(host, new DrawPartition(host.Game)) { PathLimitPerAct = pathLimit };
        enumerator.SetScenario(scenarioValues, scenarioRanges);
        keyOf = key;
        Groups = model.Reels.Select((reel, r) => GroupBy(reel, r)).ToList();
        foreach (var g in Groups) groupIndex.Add(g.Select((x, i) => (x.Key, i)).ToDictionary(t => t.Key, t => t.i, StringComparer.Ordinal));
        double configurations = Groups.Aggregate(1.0, (a, g) => a * g.Count);
        if (configurations > configurationLimit)
            throw new NotSupportedException($"Non-line part needs {configurations:N0} engine configurations (special-symbol groups {string.Join(" x ", Groups.Select(g => g.Count))}); limit {configurationLimit:N0}.");
        Configurations = (long)configurations;
    }

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

    public void Run()
    {
        int reels = Groups.Count;
        var choice = new int[reels];
        var stops = new uint[reels];
        double win = 0, trigger = 0;
        while (true)
        {
            double weight = 1;
            for (int r = 0; r < reels; r++)
            {
                var g = Groups[r][choice[r]];
                stops[r] = g.Representative;
                weight *= (double)g.Count / model.Reels[r].Range;
            }
            var (w, t) = Measure(stops);
            results[string.Join(",", choice)] = (w, t);
            win += weight * w; trigger += weight * t;
            int d = reels - 1;
            while (d >= 0 && ++choice[d] == Groups[d].Count) { choice[d] = 0; d--; }
            if (d < 0) break;
        }
        ExpectedWin = win; TriggerProbability = trigger;
    }

    private (double win, double trigger) Measure(uint[] stops)
    {
        double win = 0, trigger = 0;
        enumerator.Enumerate(stops, (h, res, w) =>
        {
            win += w * ((double)res.Credited[baseIndex] - h.AllLineWins());
            bool t = h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : res.LeftBase;
            if (t) trigger += w;
        });
        return (win, trigger);
    }

    /// <summary>Model value for arbitrary stops: the measured value of the group combination they fall in.</summary>
    public (double win, double trigger) Predict(uint[] stops)
    {
        var idx = new int[stops.Length];
        for (int r = 0; r < stops.Length; r++) idx[r] = groupIndex[r][keyOf(r, stops[r])];
        return results[string.Join(",", idx)];
    }
}
