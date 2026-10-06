using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Evaluator for the "coordinate-based mystery" construct:
///   Replace &lt;always-true win&gt; { X_set CoordinateBased {(5,0,5,0)} ReplaceInReel } { target cells }
/// Every symbol of the original set (X) on the target reels becomes the symbol shown by a hidden
/// selector reel at the given coordinate. One draw is shared by all reels, so the reels are only
/// independent once that draw is fixed: the analytic calculation is done per mystery outcome and mixed.
/// When the layout has no such replacement there is a single outcome with no substitution.
/// </summary>
public sealed class MysteryModel
{
    public sealed class Outcome
    {
        public Outcome(double probability, AGTSymbol? symbol) { Probability = probability; Symbol = symbol; }
        public double Probability { get; }
        /// <summary>Replacement symbol, or null when there is no mystery mechanic.</summary>
        public AGTSymbol? Symbol { get; }
    }

    public List<Outcome> Outcomes { get; } = new List<Outcome>();
    public HashSet<string> Originals { get; } = new HashSet<string>(StringComparer.Ordinal);
    public HashSet<int> TargetReels { get; } = new HashSet<int>();
    public int SelectorReel { get; private set; } = -1;
    public int SelectorWindow { get; private set; }
    public string Description { get; private set; } = "none";

    /// <summary>Replacements that are not the mystery construct (they must be shown irrelevant to each evaluator).</summary>
    public List<AgtEngine.Core.Misc.AGTReplacement> OtherReplacements { get; } = new List<AgtEngine.Core.Misc.AGTReplacement>();

    public static MysteryModel Read(AGTLayout layout, ReelModel model)
    {
        var result = new MysteryModel();
        AgtEngine.Core.Misc.AGTReplacement? mystery = null;
        foreach (var entry in layout.Replacements)
            foreach (var r in entry.Value)
            {
                if (r.CoordinateBasedReplacement && r.ReplaceInReel)
                {
                    if (mystery is not null)
                        throw new NotSupportedException("More than one coordinate-based mystery replacement; not supported by the analytic evaluator.");
                    mystery = r;
                }
                else result.OtherReplacements.Add(r);
            }

        if (mystery is null)
        {
            result.Outcomes.Add(new Outcome(1.0, null));
            return result;
        }
        if (mystery.Probability < 100)
            throw new NotSupportedException($"Mystery replacement applies with probability {mystery.Probability}%; not supported.");
        if (!layout.SymbolSets.Contains(mystery.Original.CString))
            throw new NotSupportedException($"Mystery original set '{mystery.Original.CString}' not found.");
        var originalSet = layout.SymbolSets[mystery.Original.CString];
        for (uint i = 0; i < originalSet.NumSymbols(); i++) result.Originals.Add(originalSet[i].Name.CString);

        var source = mystery.CoordinateOfSymbolToReplace;
        if (!model.TryLocate(source, out int selector, out int window))
            throw new NotSupportedException($"Mystery source coordinate ({source.X},{source.Y}) is not on a reel.");
        result.SelectorReel = selector;
        result.SelectorWindow = window;
        for (uint i = 0; i < mystery.ReplacementGraph.NumCoordinates; i++)
            if (model.TryLocate(mystery.ReplacementGraph.Coordinate(i), out int reel, out _)) result.TargetReels.Add(reel);

        var reelInfo = model.Reels[selector];
        var counts = new Dictionary<string, (long count, AGTSymbol symbol)>(StringComparer.Ordinal);
        for (uint p = 0; p < reelInfo.Range; p++)
        {
            var s = reelInfo.WindowSymbols[p][window];
            string name = s.Name.CString;
            counts[name] = counts.TryGetValue(name, out var c) ? (c.count + 1, c.symbol) : (1, s);
        }
        foreach (var kv in counts) result.Outcomes.Add(new Outcome((double)kv.Value.count / reelInfo.Range, kv.Value.symbol));
        result.Description = $"{string.Join(",", result.Originals)} -> symbol on reel '{reelInfo.Name}' ({result.Outcomes.Count} outcomes), on {result.TargetReels.Count} reels";
        return result;
    }

    /// <summary>The symbol a cell shows after the mystery replacement for the given outcome.</summary>
    public AGTSymbol Effective(int reel, AGTSymbol symbol, Outcome outcome) =>
        outcome.Symbol is not null && TargetReels.Contains(reel) && Originals.Contains(symbol.Name.CString) ? outcome.Symbol : symbol;
}
