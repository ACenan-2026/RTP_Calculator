using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Analytic evaluator for line pays ("Win" statements on paylines).
///
/// Expected total line win = sum over bet lines of sum over symbol tuples of
///   prod_reels P(reel shows that symbol in the line's cell)  x  pay(tuple)
/// computed separately for each mystery outcome and mixed.
///
/// The probabilities come from the reel design (stop counts per window, so stacking and weighted reels
/// are handled). The pay of a tuple is decided by the ENGINE's own matcher
/// (AGTSymbolSet.ContainsSymbolSet with each pay condition's match rules and wild qualifiers),
/// keeping the "highest win per line" rule of AGTLayout.PostProcessStandardPayLine.
/// Results are memoised per tuple, and lines whose per-reel distributions are identical are computed once.
/// </summary>
public sealed class LineEvaluator
{
    private readonly AGTLayout layout;
    private readonly List<(AGTPayCondition pc, ulong betMultiplier)> payConditions = new List<(AGTPayCondition, ulong)>();
    private readonly Dictionary<string, int> symbolIds = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly Dictionary<ulong, ulong> payMemo = new Dictionary<ulong, ulong>();
    private readonly AGTSymbolSet emptyMultipliers = new AGTSymbolSet();
    private readonly uint[] matchBuffer = new uint[64];
    private readonly uint[] multiplierBuffer = new uint[1];

    public long PayEvaluations { get; private set; }
    public long TuplesVisited { get; private set; }
    public int LinePayConditions => payConditions.Count;

    public LineEvaluator(AGTLayout layout, uint betPerLine, uint linesBet)
    {
        this.layout = layout;
        foreach (var entry in layout.PayConditions)
        {
            var pc = entry.Value;
            if (pc.Scatter || pc.Located || pc.CombinedWin || pc.IsPatternPayC || !pc.Enabled) continue;
            if (pc.Statistics.DynamicPay || pc.QueuedDynamicPay || pc.AwardEndOfFreeGames)
                throw new NotSupportedException($"Line pay condition '{entry.Key}' uses dynamic/queued/deferred pay; not supported analytically.");
            if (!pc.Statistics.Pool.IsEmpty)
                throw new NotSupportedException($"Line pay condition '{entry.Key}' pays from a random credit pool; not supported analytically.");
            if (pc.Dependency.DependentNames.Length > 0)
                throw new NotSupportedException($"Line pay condition '{entry.Key}' depends on '{string.Join(",", pc.Dependency.DependentNames)}'; not supported analytically.");
            layout.Credits.ResolveCreditWeight(pc.Statistics, 1);
            ulong multiplier = pc.Statistics.Scaled ? linesBet : 1UL;
            if (!pc.Statistics.IgnoreBetPerLine) multiplier *= betPerLine;
            payConditions.Add((pc, multiplier));
        }
        multipliers = layout.MultiplierSymbols;
    }

    private readonly AGTSymbolSet multipliers;

    /// <summary>All symbols that can take part in a line win (for checking that other replacements are irrelevant).</summary>
    public bool TouchesLines(AGTSymbol symbol)
    {
        if (symbol.Qualifier.IsSubstitute) return true;
        foreach (var (pc, _) in payConditions)
            for (uint i = 0; i < pc.Symbols.NumSymbols(); i++)
                if (pc.Symbols[i] == symbol) return true;
        return false;
    }

    private int Id(AGTSymbol s)
    {
        string name = s.Name.CString;
        if (!symbolIds.TryGetValue(name, out int id))
        {
            id = symbolIds.Count;
            if (id >= 255) throw new NotSupportedException("More than 255 distinct symbols.");
            symbolIds[name] = id;
        }
        return id;
    }

    /// <summary>Pay of one line showing <paramref name="tuple"/>, using the engine's matcher.</summary>
    public ulong Pay(IReadOnlyList<AGTSymbol> tuple)
    {
        ulong key = 0;
        for (int i = 0; i < tuple.Count; i++) key = key * 256 + (ulong)(Id(tuple[i]) + 1);
        if (tuple.Count > 7) return Evaluate(tuple); // key would overflow; do not memoise
        if (payMemo.TryGetValue(key, out ulong cached)) return cached;
        ulong pay = Evaluate(tuple);
        payMemo[key] = pay;
        return pay;
    }

    private ulong Evaluate(IReadOnlyList<AGTSymbol> tuple)
    {
        PayEvaluations++;
        // A fresh set per tuple, like a pay line's symbol set refreshed for each evaluation.
        var line = new AGTSymbolSet((uint)tuple.Count);
        for (int i = 0; i < tuple.Count; i++) line[(uint)i].Assign(tuple[i]);
        ulong best = 0; uint bestNominal = 0; bool any = false;
        bool hasMultipliers = multipliers.NumSymbols() > 0;
        foreach (var (pc, betMultiplier) in payConditions)
        {
            // Same order and rules as AGTLayout.PostProcessStandardPayLine: without multiplier symbols a
            // condition that cannot beat the current winner is skipped; with them every condition is tried.
            if (!hasMultipliers && any && bestNominal > pc.Statistics.NominalCreditWeight) continue;
            multiplierBuffer[0] = 1;
            if (!line.ContainsSymbolSet(pc.Symbols, matchBuffer, pc.MatchLocation, pc.MatchDistribution, pc.MatchOrder, null, multipliers, multiplierBuffer))
                continue;
            layout.Credits.ResolveCreditWeight(pc.Statistics, multiplierBuffer[0]);
            uint nominal = pc.Statistics.NominalCreditWeight;
            if (!any || nominal > bestNominal) { any = true; bestNominal = nominal; best = nominal * betMultiplier; }
        }
        // Leave the credit weights as resolved without multipliers for the next tuple.
        if (hasMultipliers) foreach (var (pc, _) in payConditions) layout.Credits.ResolveCreditWeight(pc.Statistics, 1);
        return best;
    }

    /// <summary>Per-reel distribution of the symbol in one cell: (probability, symbol) pairs.</summary>
    public static List<(double p, AGTSymbol s)> CellDistribution(ReelInfo reel, int reelIndex, int window, MysteryModel mystery, MysteryModel.Outcome outcome)
    {
        var map = new Dictionary<string, (double p, AGTSymbol symbol)>(StringComparer.Ordinal);
        double unit = 1.0 / reel.Range;
        for (uint p = 0; p < reel.Range; p++)
            foreach (var (q, s) in mystery.Effective(reelIndex, window, reel.WindowSymbols[p][window], outcome))
            {
                string name = s.Name.CString;
                map[name] = map.TryGetValue(name, out var c) ? (c.p + unit * q, c.symbol) : (unit * q, s);
            }
        return map.Values.Select(v => (v.p, v.symbol)).OrderBy(v => v.symbol.Name.CString, StringComparer.Ordinal).ToList();
    }

    /// <summary>Expected win on one line for fixed per-cell distributions (cells in line order).</summary>
    public double ExpectedLineWin(IReadOnlyList<List<(double p, AGTSymbol s)>> cells)
    {
        var tuple = new AGTSymbol[cells.Count];
        double total = 0;
        void Walk(int depth, double probability)
        {
            if (depth == cells.Count)
            {
                TuplesVisited++;
                ulong pay = Pay(tuple);
                if (pay != 0) total += probability * pay;
                return;
            }
            foreach (var (p, s) in cells[depth])
            {
                tuple[depth] = s;
                Walk(depth + 1, probability * p);
            }
        }
        Walk(0, 1.0);
        return total;
    }
}
