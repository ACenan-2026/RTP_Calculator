using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Works out which cells and symbols can influence the non-line result of a base spin (non-line credits
/// and feature entry), from the engine's loaded objects:
///
///  1. Start from non-line pay conditions that can pay (credit, dynamic pay, free games) or that a feature
///     strategy is keyed on, plus everything they depend on (combined wins, DependentOn).
///     A condition whose symbols can never appear where it looks (e.g. a client-only 'noWin' display win)
///     is dropped.
///  2. Their cells (located) or every cell where their symbols can appear (scatter) become relevant, for
///     those symbols.
///  3. Follow replacements backwards: a replacement that can write a relevant cell makes its own inputs at
///     its targets, its trigger condition and its source cell relevant. Repeat until nothing changes.
///
/// Every other replacement cannot affect the non-line result. The non-line evaluators then key reel stops
/// only by relevant symbols in relevant cells, and run the engine with the irrelevant replacements switched
/// off (so coin placements, line-symbol mysteries etc. are not enumerated).
/// </summary>
public sealed class NonLineRelevance
{
    /// <summary>Cells whose exact symbol matters.</summary>
    public HashSet<(int reel, int window)> FullCells { get; } = new HashSet<(int, int)>();
    /// <summary>Cells where only some symbols matter (anything else is equivalent).</summary>
    public Dictionary<(int reel, int window), HashSet<string>> CellSymbols { get; } = new Dictionary<(int, int), HashSet<string>>();
    public HashSet<string> RelevantConditions { get; } = new HashSet<string>(StringComparer.Ordinal);
    public List<ReplacementCatalog.Entry> Irrelevant { get; } = new List<ReplacementCatalog.Entry>();
    public string Description { get; private set; } = "";

    public static NonLineRelevance Build(AGTReelGame game, AGTLayout layout, string baseLayoutName, ReelModel model, ReplacementCatalog catalog)
    {
        var rel = new NonLineRelevance();
        var allCells = new List<(int, int)>();
        for (int r = 0; r < model.Reels.Count; r++) for (int w = 0; w < model.Reels[r].Coordinates.Length; w++) allCells.Add((r, w));

        var entryKeys = new HashSet<string>(game.Strategies.Where(s => s.Value.Layout.CString != baseLayoutName) /* include strategies an analysis engine has switched off */.Select(s => s.Key), StringComparer.Ordinal);
        var queue = new Queue<string>();
        foreach (var p in layout.PayConditions)
        {
            var pc = p.Value;
            if (!(pc.Scatter || pc.Located || pc.CombinedWin || pc.IsPatternPayC)) continue; // disabled ones may be enabled during the spin
            layout.Credits.ResolveCreditWeight(pc.Statistics, 1);
            bool pays = pc.Statistics.NominalCreditWeight > 0 || pc.Statistics.DynamicPay || !pc.Statistics.Pool.IsEmpty
                        || pc.QueuedDynamicPay || pc.FreeGamesAwarded > 0;
            if (pays || entryKeys.Contains(p.Key)) queue.Enqueue(p.Key);
        }

        void MarkSymbols((int, int) cell, IEnumerable<string> names)
        {
            if (rel.FullCells.Contains(cell)) return;
            if (!rel.CellSymbols.TryGetValue(cell, out var set)) rel.CellSymbols[cell] = set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in names) set.Add(n);
        }
        void MarkFull((int, int) cell) { rel.FullCells.Add(cell); rel.CellSymbols.Remove(cell); }
        IEnumerable<string> Names(AGTSymbolSet set)
        {
            for (uint i = 0; i < set.NumSymbols(); i++)
            {
                yield return set[i].Name.CString;
                if (!set[i].AliasName.IsEmpty) yield return set[i].AliasName.CString;
            }
        }
        bool AnySubstitute(AGTSymbolSet set)
        {
            if (set.Qualifier.IsSubstitute) return true;
            for (uint i = 0; i < set.NumSymbols(); i++) if (set[i].Qualifier.IsSubstitute) return true;
            return false;
        }

        bool changed = true;
        while (changed)
        {
            changed = false;
            while (queue.Count > 0)
            {
                string name = queue.Dequeue();
                if (!rel.RelevantConditions.Add(name) || !layout.PayConditions.Any(p => p.Key == name)) continue;
                var pc = layout.PayConditions.Entry(layout.PayConditions.KeyIndex(name)).Value;
                foreach (var d in pc.Dependency.DependentNames) queue.Enqueue(d);
                if (pc.CombinedWin) continue;
                bool substitute = AnySubstitute(pc.Symbols);
                var names = Names(pc.Symbols).ToList();
                if (pc.IsPatternPayC) { foreach (var c in allCells) MarkFull(c); continue; }
                if (pc.Located && !pc.Scatter)
                {
                    for (uint i = 0; i < pc.Graph.NumCoordinates; i++)
                        if (model.TryLocate(pc.Graph.Coordinate(i), out int r, out int w))
                        {
                            if (substitute) MarkFull((r, w));
                            else if (names.Any(n => catalog.Possible[(r, w)].Contains(n))) MarkSymbols((r, w), names);
                        }
                }
                else
                {
                    foreach (var c in allCells)
                        if (substitute) MarkFull(c);
                        else if (names.Any(n => catalog.Possible[c].Contains(n))) MarkSymbols(c, names);
                }
            }

            // Conditions that enable a relevant condition during the spin are relevant too.
            foreach (var p in layout.PayConditions)
                if (!rel.RelevantConditions.Contains(p.Key) && p.Value.DependentPayConditions.Any(rel.RelevantConditions.Contains)) queue.Enqueue(p.Key);

            // Replacement chains, backwards.
            foreach (var e in catalog.Entries)
            {
                var hits = e.Targets.Where(t => (rel.FullCells.Contains(t) || rel.CellSymbols.ContainsKey(t)) && catalog.CanAct(e, t)).ToList();
                if (hits.Count == 0) continue;
                int before = rel.FullCells.Count + rel.CellSymbols.Sum(kv => kv.Value.Count) + rel.RelevantConditions.Count;
                foreach (var t in e.Targets.Where(t => catalog.CanAct(e, t)))
                {
                    if (e.AnyInput) MarkFull(t);
                    else MarkSymbols(t, e.Inputs);
                }
                if (e.Source is (int, int) src) MarkFull(src);
                if (!rel.RelevantConditions.Contains(e.Info.Trigger)) queue.Enqueue(e.Info.Trigger);
                int after = rel.FullCells.Count + rel.CellSymbols.Sum(kv => kv.Value.Count) + rel.RelevantConditions.Count + queue.Count;
                if (after != before) changed = true;
            }
            if (queue.Count > 0) changed = true;
        }

        foreach (var e in catalog.Entries)
            if (!e.Targets.Any(t => (rel.FullCells.Contains(t) || rel.CellSymbols.ContainsKey(t)) && catalog.CanAct(e, t)))
                rel.Irrelevant.Add(e);
        rel.Description = $"{rel.RelevantConditions.Count} relevant condition(s), {rel.FullCells.Count} full + {rel.CellSymbols.Count} partial cell(s), " +
                          $"{rel.Irrelevant.Count} of {catalog.Entries.Count} replacement(s) irrelevant";
        return rel;
    }

    /// <summary>Key of one reel window for the non-line evaluators.</summary>
    public string Key(int reel, AGTSymbol[] window)
    {
        var parts = new string[window.Length];
        for (int w = 0; w < window.Length; w++)
        {
            var s = window[w];
            if (FullCells.Contains((reel, w))) parts[w] = s.Name.CString;
            else if (CellSymbols.TryGetValue((reel, w), out var set))
                parts[w] = set.Contains(s.Name.CString) || (!s.AliasName.IsEmpty && set.Contains(s.AliasName.CString)) ? s.Name.CString : ".";
            else parts[w] = "";
        }
        return string.Join("|", parts);
    }
}
