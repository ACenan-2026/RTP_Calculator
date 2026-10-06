using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Always-applying replacements that can change payline symbols, applied per cell in engine order:
///
///  * coordinate-based ("mystery"):  Replace &lt;always-true&gt; { X_set CoordinateBased {(5,0,5,0)} [ReplaceInReel] }
///    Original-set symbols in target cells become the symbol at the source cell. One draw is shared by
///    all target cells, so the calculation is done per joint outcome of the source cells and mixed.
///  * fixed:            Replace &lt;always-true&gt; { A_set B_set }   (A[i] -> B[i], or -> B[0])
///  * random per cell:  Replace &lt;always-true&gt; { A_set B_set RandomlySelectFromSet }
///    Each matching cell draws independently from B's weights, so cells stay independent.
///
/// The same symbol-matching calls as the engine are used (ContainsSymbol for coordinate-based,
/// ContainsExactSymbol for the others). With no such replacement there is a single outcome.
/// </summary>
public sealed class MysteryModel
{
    public sealed class Outcome
    {
        public Outcome(double probability, Dictionary<(int reel, int window), AGTSymbol> sources) { Probability = probability; Sources = sources; }
        public double Probability { get; }
        public Dictionary<(int reel, int window), AGTSymbol> Sources { get; }
    }

    private sealed class Rule
    {
        public string Kind = "";
        public AGTSymbolSet Original = null!;
        public AGTSymbolSet? Replacing;
        public HashSet<(int, int)> Targets = new HashSet<(int, int)>();
        public (int reel, int window) Source;
        public List<(double p, AGTSymbol s)> Weighted = new List<(double, AGTSymbol)>();
    }

    private readonly List<Rule> rules = new List<Rule>();
    public List<Outcome> Outcomes { get; } = new List<Outcome>();
    public HashSet<int> SourceReels { get; } = new HashSet<int>();
    public string Description { get; private set; } = "none";
    public bool HasRandomCells => rules.Any(r => r.Kind == "random-per-cell");

    /// <summary>Entries this model accounts for (so other checks can skip them).</summary>
    public HashSet<ReplacementCatalog.Entry> Handled { get; } = new HashSet<ReplacementCatalog.Entry>();

    public static MysteryModel Build(AGTLayout layout, ReelModel model, ReplacementCatalog catalog)
    {
        var m = new MysteryModel();
        foreach (var e in catalog.Entries)
        {
            var rpl = e.Replacement;
            if (!e.Info.TriggerAlwaysTrue || rpl.Probability < rpl.MaxProbabilityRange) continue;
            if (!layout.SymbolSets.Contains(rpl.Original.CString)) continue;
            var original = layout.SymbolSets[rpl.Original.CString];
            if (e.Info.Kind is "mystery" or "coordinate")
            {
                if (e.Source is not (int, int) source) continue;
                m.rules.Add(new Rule { Kind = "coordinate", Original = original, Targets = new HashSet<(int, int)>(e.Targets), Source = source });
                m.SourceReels.Add(source.reel);
            }
            else if (e.Info.Kind is "fixed" or "random-per-cell")
            {
                if (!layout.SymbolSets.Contains(rpl.Replacement.CString)) continue;
                var replacing = layout.SymbolSets[rpl.Replacement.CString];
                var rule = new Rule { Kind = e.Info.Kind, Original = original, Replacing = replacing, Targets = new HashSet<(int, int)>(e.Targets) };
                if (e.Info.Kind == "random-per-cell")
                {
                    // Same distribution as replacing.ActualSymbolIndexInExtendedSet(random(NumSymbolsBasedOnExtension)).
                    uint range = replacing.NumSymbolsBasedOnExtension;
                    var counts = new Dictionary<uint, long>();
                    for (uint v = 0; v < range; v++) { uint i = replacing.ActualSymbolIndexInExtendedSet(v); counts[i] = counts.TryGetValue(i, out long c) ? c + 1 : 1; }
                    foreach (var kv in counts) rule.Weighted.Add(((double)kv.Value / range, new AGTSymbol(replacing[kv.Key])));
                }
                m.rules.Add(rule);
            }
            else continue;
            m.Handled.Add(e);
        }
        // A source cell must not itself be changed by a replacement (that would need ordering of chains).
        foreach (var rule in m.rules.Where(r => r.Kind == "coordinate"))
            foreach (var e in catalog.Entries)
                if (e.Targets.Contains(rule.Source) && catalog.CanAct(e, rule.Source))
                    throw new NotSupportedException($"Mystery source cell ({rule.Source.reel},{rule.Source.window}) is itself changed by '{e}'; chained mysteries not supported.");

        // Joint outcomes: product over source reels of that reel's window contents.
        var outcomes = new List<(double p, Dictionary<(int, int), AGTSymbol> s)> { (1.0, new Dictionary<(int, int), AGTSymbol>()) };
        foreach (int reel in m.SourceReels.OrderBy(r => r))
        {
            var info = model.Reels[reel];
            var windows = m.rules.Where(r => r.Kind == "coordinate" && r.Source.reel == reel).Select(r => r.Source.window).Distinct().ToArray();
            var groups = new Dictionary<string, (long count, AGTSymbol[] symbols)>(StringComparer.Ordinal);
            for (uint p = 0; p < info.Range; p++)
            {
                var syms = windows.Select(w => info.WindowSymbols[p][w]).ToArray();
                string key = string.Join("|", syms.Select(s => s.Name.CString));
                groups[key] = groups.TryGetValue(key, out var g) ? (g.count + 1, g.symbols) : (1, syms);
            }
            var next = new List<(double, Dictionary<(int, int), AGTSymbol>)>();
            foreach (var (p, s) in outcomes)
                foreach (var g in groups.Values)
                {
                    var d = new Dictionary<(int, int), AGTSymbol>(s);
                    for (int i = 0; i < windows.Length; i++) d[(reel, windows[i])] = g.symbols[i];
                    next.Add((p * g.count / info.Range, d));
                }
            outcomes = next;
            if (outcomes.Count > 10_000) throw new NotSupportedException($"Mystery sources give {outcomes.Count:N0}+ joint outcomes; too many.");
        }
        foreach (var (p, s) in outcomes) m.Outcomes.Add(new Outcome(p, s));
        int coordinate = m.rules.Count(r => r.Kind == "coordinate"), cellwise = m.rules.Count - coordinate;
        m.Description = m.rules.Count == 0 ? "none"
            : $"{coordinate} coordinate replacement(s) from {m.SourceReels.Count} source reel(s), {cellwise} per-cell replacement(s); {m.Outcomes.Count} outcomes";
        return m;
    }

    /// <summary>Distribution of the symbol a cell shows after these replacements, for one outcome.</summary>
    public List<(double p, AGTSymbol s)> Effective(int reel, int window, AGTSymbol symbol, Outcome outcome)
    {
        var dist = new List<(double p, AGTSymbol s)> { (1.0, symbol) };
        foreach (var rule in rules)
        {
            if (!rule.Targets.Contains((reel, window))) continue;
            var next = new List<(double p, AGTSymbol s)>();
            foreach (var (p, s) in dist)
            {
                uint index = 0;
                switch (rule.Kind)
                {
                    case "coordinate":
                        next.Add((p, rule.Original.ContainsSymbol(s) ? outcome.Sources[rule.Source] : s));
                        break;
                    case "fixed":
                        if (rule.Original.ContainsExactSymbol(s, ref index))
                            next.Add((p, new AGTSymbol(rule.Replacing![rule.Replacing.NumSymbols() == 1 ? 0 : rule.Original.SymbolIndex(s)])));
                        else next.Add((p, s));
                        break;
                    default: // random-per-cell
                        if (rule.Original.ContainsExactSymbol(s, ref index))
                            foreach (var (q, r) in rule.Weighted) next.Add((p * q, r));
                        else next.Add((p, s));
                        break;
                }
            }
            dist = next;
        }
        return dist;
    }

    /// <summary>The outcome selected by a given set of reel stops (for verification).</summary>
    public Outcome OutcomeFor(ReelModel model, uint[] stops)
    {
        foreach (var o in Outcomes)
            if (o.Sources.All(kv => model.Reels[kv.Key.reel].WindowSymbols[stops[kv.Key.reel]][kv.Key.window].Name.CString == kv.Value.Name.CString))
                return o;
        throw new InvalidOperationException("No mystery outcome matches the given stops.");
    }
}
