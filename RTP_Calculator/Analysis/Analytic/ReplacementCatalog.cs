using AgtEngine.Core.Graphic;
using AgtEngine.Core.Misc;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Analysis.Coverage;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Every replacement in the base layout with what it can read and write, worked out from the engine's
/// loaded objects:
///   Inputs   symbols it replaces (or "any symbol" for wildcard originals / choose-on-probability),
///   Outputs  symbols it can put down (replacement set, mapping sets, nested weighted sets, or for a
///            coordinate-based replacement whatever can appear at the source cell),
///   Targets  the cells it may act on.
/// A fixed-point pass then works out which symbols can possibly appear in each cell after all
/// replacements. From that, a replacement "acts on lines" only if it targets a payline cell where one
/// of its inputs can actually appear, and its inputs or outputs can take part in a line win.
/// </summary>
public sealed class ReplacementCatalog
{
    public sealed class Entry
    {
        public ConstructScanner.ReplacementInfo Info { get; set; } = null!;
        public AGTReplacement Replacement => Info.Source;
        public HashSet<string> Inputs { get; } = new HashSet<string>(StringComparer.Ordinal);
        public bool AnyInput { get; set; }
        public HashSet<string> Outputs { get; } = new HashSet<string>(StringComparer.Ordinal);
        public List<(int reel, int window)> Targets { get; } = new List<(int, int)>();
        public (int reel, int window)? Source { get; set; }
        public bool ActsOnLines { get; set; }
        public bool ActsOnScatter { get; set; }
        public override string ToString() => Info.ToString();
    }

    public List<Entry> Entries { get; } = new List<Entry>();
    /// <summary>Symbols that can appear in each cell after replacements (pre-replacement strip symbols included).</summary>
    public Dictionary<(int reel, int window), HashSet<string>> Possible { get; } = new Dictionary<(int, int), HashSet<string>>();
    private readonly Dictionary<string, AGTSymbol> symbolObjects = new Dictionary<string, AGTSymbol>(StringComparer.Ordinal);

    public static ReplacementCatalog Build(AGTLayout layout, ReelModel model, ConstructScanner.Result scan,
        HashSet<(int reel, int window)> lineCells, LineEvaluator? lines, HashSet<string> scatter)
    {
        var c = new ReplacementCatalog();
        // Pre-replacement symbols per cell.
        for (int r = 0; r < model.Reels.Count; r++)
            for (int w = 0; w < model.Reels[r].Coordinates.Length; w++)
            {
                var set = new HashSet<string>(StringComparer.Ordinal);
                foreach (var window in model.Reels[r].WindowSymbols) { set.Add(window[w].Name.CString); c.Remember(window[w]); }
                c.Possible[(r, w)] = set;
            }

        foreach (var info in scan.Replacements.OrderBy(i => i.Order))
        {
            var rpl = info.Source;
            var e = new Entry { Info = info };
            for (uint i = 0; i < rpl.ReplacementGraph.NumCoordinates; i++)
                if (model.TryLocate(rpl.ReplacementGraph.Coordinate(i), out int reel, out int window)) e.Targets.Add((reel, window));

            switch (info.Kind)
            {
                case "mapped":
                    {
                        string names = rpl.ReplacementMappingSymbolsetName.CString;
                        if (layout.MappedSymbolReplacementInfo.Contains(names))
                            foreach (var p in layout.MappedSymbolReplacementInfo[names]) { e.Inputs.Add(p.Key); c.AddOutputName(layout, e, p.Value.CString); }
                        c.AddMappingOutputs(layout, e, rpl.MappingSymbolsetName.CString);
                        break;
                    }
                case "floating":
                    c.AddInputs(layout, e, rpl.Original.CString);
                    c.AddSetOutputs(layout, e, rpl.Replacement.CString);
                    c.AddMappingOutputs(layout, e, rpl.MappingSymbolsetName.CString);
                    break;
                case "coordinate":
                case "mystery":
                    c.AddInputs(layout, e, rpl.Original.CString);
                    if (model.TryLocate(rpl.CoordinateOfSymbolToReplace, out int sr, out int sw)) e.Source = (sr, sw);
                    else e.AnyInput = true; // unknown source: be conservative
                    break;
                case "choose-on-probability":
                    e.AnyInput = true; // the engine replaces every target cell, whatever it shows
                    c.AddSetOutputs(layout, e, rpl.Replacement.CString);
                    break;
                default:
                    c.AddInputs(layout, e, rpl.Original.CString);
                    c.AddSetOutputs(layout, e, rpl.Replacement.CString);
                    break;
            }
            c.Entries.Add(e);
        }

        // Fixed point: outputs flow into every target cell where an input can appear.
        bool changed = true; int guard = 0;
        while (changed && guard++ < 50)
        {
            changed = false;
            foreach (var e in c.Entries)
            {
                var outputs = e.Source is (int, int) src ? c.Possible[src] : e.Outputs;
                foreach (var cell in e.Targets)
                {
                    if (!c.CanAct(e, cell)) continue;
                    foreach (var o in outputs.ToList()) if (c.Possible[cell].Add(o)) changed = true;
                }
            }
        }

        // A trigger that is a located win on cells that can only ever show matching symbols (for example a
        // hidden reel whose strip is all 'win') is always true, even if its symbols are not wildcards.
        foreach (var e in c.Entries)
        {
            if (e.Info.TriggerAlwaysTrue || e.Info.TriggerIndex < 0 || e.Info.TriggerIndex >= layout.PayConditions.Count) continue;
            var pc = layout.PayConditions.Entry(e.Info.TriggerIndex).Value;
            if (layout.PayConditions.Entry(e.Info.TriggerIndex).Key != e.Info.Trigger) continue;
            if (!pc.Located || pc.Scatter || pc.CombinedWin || !pc.Enabled || pc.Dependency.DependentNames.Length > 0) continue;
            if (pc.Graph.NumCoordinates == 0 || pc.Graph.NumCoordinates != pc.Symbols.NumSymbols()) continue;
            bool always = true;
            for (uint i = 0; always && i < pc.Graph.NumCoordinates; i++)
            {
                if (!model.TryLocate(pc.Graph.Coordinate(i), out int r, out int w)) { always = false; break; }
                var want = pc.Symbols[i];
                if (want.Qualifier.IsSubstitute) continue;
                foreach (var name in c.Possible[(r, w)])
                    if (!c.symbolObjects.TryGetValue(name, out var have) || !(want == have)) { always = false; break; }
            }
            if (always) e.Info.TriggerAlwaysTrue = true;
        }

        bool TouchesLines(string name) => lines is not null && c.symbolObjects.TryGetValue(name, out var s) && lines.TouchesLines(s);
        bool TouchesScatter(string name) => scatter.Contains(name) || (c.symbolObjects.TryGetValue(name, out var s) && !s.AliasName.IsEmpty && scatter.Contains(s.AliasName.CString));
        foreach (var e in c.Entries)
        {
            var outputs = e.Source is (int, int) src ? c.Possible[src] : e.Outputs;
            var active = e.Targets.Where(t => c.CanAct(e, t)).ToList();
            e.ActsOnLines = active.Any(lineCells.Contains) && (e.AnyInput || e.Inputs.Any(TouchesLines) || outputs.Any(TouchesLines));
            e.ActsOnScatter = active.Count > 0 && (e.Inputs.Any(TouchesScatter) || outputs.Any(TouchesScatter) || e.AnyInput);
        }
        return c;
    }

    /// <summary>Can this replacement change the given cell (an input can appear there)?</summary>
    public bool CanAct(Entry e, (int reel, int window) cell) =>
        Possible.TryGetValue(cell, out var possible) && (e.AnyInput || e.Inputs.Any(possible.Contains));

    private void Remember(AGTSymbol s)
    {
        string n = s.Name.CString;
        if (!symbolObjects.ContainsKey(n)) symbolObjects[n] = new AGTSymbol(s);
    }

    private void AddInputs(AGTLayout layout, Entry e, string setName)
    {
        if (setName.Length == 0 || !layout.SymbolSets.Contains(setName)) { e.AnyInput = true; return; }
        var set = layout.SymbolSets[setName];
        if (set.Qualifier.IsSubstitute) e.AnyInput = true;
        for (uint i = 0; i < set.NumSymbols(); i++)
        {
            var s = set[i];
            if (s.Qualifier.IsSubstitute) e.AnyInput = true;
            e.Inputs.Add(s.Name.CString);
            if (!s.AliasName.IsEmpty) e.Inputs.Add(s.AliasName.CString);
            Remember(s);
        }
    }

    private void AddSetOutputs(AGTLayout layout, Entry e, string setName, int depth = 0)
    {
        if (setName.Length == 0 || !layout.SymbolSets.Contains(setName) || depth > 5) return;
        var set = layout.SymbolSets[setName];
        for (uint i = 0; i < set.NumSymbols(); i++) AddOutputName(layout, e, set[i].Name.CString, set[i], depth + 1);
    }

    private void AddOutputName(AGTLayout layout, Entry e, string name, AGTSymbol? symbol = null, int depth = 0)
    {
        // A name that is itself a symbol set is resolved by a further weighted pick in the engine.
        if (layout.SymbolSets.Contains(name) && depth < 5 && symbol is null) { AddSetOutputs(layout, e, name, depth + 1); return; }
        e.Outputs.Add(name);
        if (symbol is not null) Remember(symbol);
    }

    private void AddMappingOutputs(AGTLayout layout, Entry e, string mappingName)
    {
        if (mappingName.Length == 0 || !layout.FloatingReplacementSymbolMappingSets.Contains(mappingName)) return;
        foreach (var p in layout.FloatingReplacementSymbolMappingSets[mappingName])
        {
            e.Outputs.Add(p.Value.Name.CString); Remember(p.Value);
            if (layout.SymbolSets.Contains(p.Key)) AddSetOutputs(layout, e, p.Key);
        }
    }
}
