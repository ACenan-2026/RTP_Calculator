using AgtEngine.Core.Geometry;
using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Analytic evaluator for ways pays ("PatternWin" pay conditions), following the engine's rules
/// (AGTLayout.MatchPattern / ProcessPatternPayLine):
///
///  * Pattern conditions are grouped in blocks (same block name, consecutive in the game file), sorted by
///    length. Column x of the layout plane matches condition c when at least one of its cells takes part
///    in pattern evaluation and equals c's x-th symbol (the engine's symbol comparison, wilds included).
///  * In a block the conditions are tried shortest first; the winner is the last one in an unbroken run of
///    matches, i.e. the longest one the leading matching columns reach.
///  * The winner pays its credit for every way: the product of the numbers of matching cells in its columns.
///
/// Different columns come from different reels, so they are independent and the expectation factorises:
///   E[win of block] = sum_j pay_j * prod_{x &lt; L_j} E[S_x] * (1 - prod_{L_j &lt;= x &lt; L_{j+1}} q_x)
/// where S_x is the number of matching cells in column x and q_x the probability that column x matches.
/// Each column's cells come from one reel; S_x and q_x are averaged over that reel's stops, with the cells'
/// replacement distributions (mystery outcomes, random per-cell picks) applied cell by cell.
///
/// Not supported (refused with a message): any-order, multiplied or wild-only ("SubSymBlock") patterns,
/// multiplier symbols, dynamic or pooled pays, a column whose cells come from several reels, and
/// replacements this tool cannot model that can change a ways cell.
/// </summary>
public sealed class WaysEvaluator
{
    private sealed class Condition
    {
        public string Name = "";
        public AGTPayCondition Pc = null!;
        public int Length;
        public double Pay; // credits per way
    }

    private sealed class Block
    {
        public string Name = "";
        public List<Condition> Conditions = new List<Condition>();
        public AGTSymbol[] Symbols = Array.Empty<AGTSymbol>(); // symbol per column (longest condition)
    }

    private readonly ReelModel model;
    private readonly List<Block> blocks = new List<Block>();
    // Column x: (reel, windows of that reel in the column), cells that take part in pattern evaluation.
    private readonly List<(int reel, int[] windows)> columns = new List<(int, int[])>();

    public HashSet<string> PatternConditions { get; } = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> substituteNames = new HashSet<string>(StringComparer.Ordinal);
    public bool Active => blocks.Count > 0;
    public string Description { get; }

    public WaysEvaluator(AGTLayout layout, ReelModel model, uint betPerLine, uint linesBet)
    {
        this.model = model;
        foreach (var set in layout.SymbolSets)
            for (uint i = 0; i < set.Value.NumSymbols(); i++)
                if (set.Value[i].Qualifier.IsSubstitute) substituteNames.Add(set.Value[i].Name.CString);
        foreach (var reel in layout.Reels)
            for (uint i = 0; i < reel.Value.Symbols.NumSymbols(); i++)
                if (reel.Value.Symbols[i].Qualifier.IsSubstitute) substituteNames.Add(reel.Value.Symbols[i].Name.CString);
        // Blocks as AGTLayout.PreparePayconditions builds them: consecutive conditions with the same block name.
        Block? current = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in layout.PayConditions)
        {
            var pc = entry.Value;
            if (!pc.IsPatternPayC) continue;
            PatternConditions.Add(entry.Key);
            string block = pc.PatternBlockName.CString;
            if (block == "SubSymBlock") throw new NotSupportedException($"Ways condition '{entry.Key}' is a wild-only (SubSymBlock) pattern; not supported analytically yet.");
            if (pc.Statistics.Multiplied) throw new NotSupportedException($"Ways condition '{entry.Key}' multiplies the win; not supported analytically yet.");
            if (pc.MatchOrder != AGTMatchOrder.InOrder) throw new NotSupportedException($"Ways condition '{entry.Key}' matches in any order; not supported analytically yet.");
            if (pc.Statistics.DynamicPay || pc.QueuedDynamicPay || !pc.Statistics.Pool.IsEmpty || pc.AwardEndOfFreeGames)
                throw new NotSupportedException($"Ways condition '{entry.Key}' uses dynamic, pooled or deferred pay; not supported analytically.");
            if (pc.Dependency.DependentNames.Length > 0)
                throw new NotSupportedException($"Ways condition '{entry.Key}' depends on '{string.Join(",", pc.Dependency.DependentNames)}'; not supported analytically.");
            if (pc.TokenEnabled || pc.MetaMultiplierEnabled)
                throw new NotSupportedException($"Ways condition '{entry.Key}' collects metamorphic tokens; not supported analytically.");
            if (current is null || current.Name != block)
            {
                // The engine keeps only the first block of a given name (TryAdd).
                current = seen.Add(block) ? new Block { Name = block } : new Block { Name = "\0ignored" };
                if (current.Name == block) blocks.Add(current);
            }
            if (current.Name != block) continue;
            if (!pc.Enabled) continue; // skipped by ProcessPatternPayLine
            layout.Credits.ResolveCreditWeight(pc.Statistics, 1);
            double multiplier = pc.Statistics.Scaled ? linesBet : 1;
            if (!pc.Statistics.IgnoreBetPerLine) multiplier *= betPerLine;
            else if (pc.Statistics.ApplyDenomWeight) multiplier *= layout.DenomWeight;
            current.Conditions.Add(new Condition { Name = entry.Key, Pc = pc, Length = (int)pc.Symbols.NumSymbols(), Pay = pc.Statistics.NominalCreditWeight * multiplier });
        }
        blocks.RemoveAll(b => b.Conditions.Count == 0);
        if (blocks.Count == 0) { Description = "none"; return; }
        if (layout.MultiplierSymbols.NumSymbols() > 0)
            throw new NotSupportedException("Ways game with multiplier symbols; not supported analytically yet.");

        int width = 0;
        foreach (var b in blocks)
        {
            b.Conditions = b.Conditions.OrderBy(c => c.Length).ToList();
            for (int i = 1; i < b.Conditions.Count; i++)
                if (b.Conditions[i].Length == b.Conditions[i - 1].Length)
                    throw new NotSupportedException($"Ways block '{b.Name}' has two conditions of length {b.Conditions[i].Length}; not supported analytically.");
            var longest = b.Conditions[b.Conditions.Count - 1];
            b.Symbols = Enumerable.Range(0, longest.Length).Select(x => longest.Pc[(uint)x]).ToArray();
            foreach (var c in b.Conditions)
                for (int x = 0; x < c.Length; x++)
                    if (!SameSymbol(c.Pc[(uint)x], b.Symbols[x]))
                        throw new NotSupportedException($"Ways block '{b.Name}': conditions disagree on the symbol of column {x}; not supported analytically.");
            width = Math.Max(width, longest.Length);
        }

        // Columns: cells (x, y) for y = 0..plane height, located on the model's reels.
        int rows = layout.Plane.Limit.Y + 1;
        for (int x = 0; x < width; x++)
        {
            int reel = -1; var windows = new List<int>();
            for (int y = 0; y < rows; y++)
            {
                if (!model.TryLocate(new AGTCoordinate(x, y), out int r, out int w)) continue;
                if (!layout.Reels[model.Reels[r].Name].ParticipatingInPatternWin) continue;
                if (reel >= 0 && r != reel)
                    throw new NotSupportedException($"Ways column {x} shows cells of more than one reel; not supported analytically yet.");
                reel = r; windows.Add(w);
            }
            if (reel < 0) throw new NotSupportedException($"Ways column {x} has no reel cells.");
            if (columns.Any(c => c.reel == reel)) throw new NotSupportedException($"Reel {reel} spans several ways columns; not supported analytically yet.");
            columns.Add((reel, windows.ToArray()));
        }
        Description = $"{blocks.Count} ways block(s), {blocks.Sum(b => b.Conditions.Count)} condition(s), {width} columns";
    }

    private static bool SameSymbol(AGTSymbol a, AGTSymbol b) =>
        a.Name.CString == b.Name.CString && a.Qualifier.IsNotSubstitutable == b.Qualifier.IsNotSubstitutable && a.Qualifier.IsSubstitute == b.Qualifier.IsSubstitute;

    /// <summary>Cells the ways evaluation reads.</summary>
    public IEnumerable<(int reel, int window)> Cells => columns.SelectMany(c => c.windows.Select(w => (c.reel, w)));

    /// <summary>
    /// Refuses replacements that can change a ways cell unless the mystery model handles them (its per-cell
    /// distributions are applied here). <paramref name="unhandled"/> lists the catalog entries not modelled.
    /// </summary>
    public void CheckReplacements(IEnumerable<ReplacementCatalog.Entry> unhandled, ReplacementCatalog catalog)
    {
        var cells = new HashSet<(int, int)>(Cells);
        foreach (var e in unhandled)
        {
            if (!e.Targets.Any(t => cells.Contains(t) && catalog.CanAct(e, t))) continue;
            bool touches = e.AnyInput || e.Inputs.Concat(e.Outputs).Any(Touches);
            if (touches)
                throw new NotSupportedException($"Replacement {e} can change symbols in ways cells; not supported analytically yet.");
        }
    }

    private readonly Dictionary<string, bool> touchMemo = new Dictionary<string, bool>(StringComparer.Ordinal);

    /// <summary>Whether a symbol name can take part in a ways win (equals some condition symbol or substitutes).</summary>
    private bool Touches(string name)
    {
        if (touchMemo.TryGetValue(name, out bool t)) return t;
        t = false;
        foreach (var b in blocks)
            foreach (var s in b.Symbols)
                if (s.Name.CString == name) t = true;
        return touchMemo[name] = t || substituteNames.Contains(name);
    }

    /// <summary>Per-stop column statistics for one block column: (E[matching cells], P(at least one match)).</summary>
    private (double s, double q) ColumnAtStop(int x, Block b, uint stop, MysteryModel mystery, MysteryModel.Outcome outcome)
    {
        var (reel, windows) = columns[x];
        double s = 0, none = 1;
        foreach (int w in windows)
        {
            double match = 0;
            foreach (var (p, sym) in mystery.Effective(reel, w, model.Reels[reel].WindowSymbols[stop][w], outcome))
                if (sym == b.Symbols[x]) match += p;
            s += match; none *= 1 - match;
        }
        return (s, 1 - none);
    }

    private static double BlockWin(Block b, double[] s, double[] q)
    {
        double total = 0, prefix = 1; int x = 0;
        for (int j = 0; j < b.Conditions.Count; j++)
        {
            var c = b.Conditions[j];
            for (; x < c.Length; x++) prefix *= s[x];
            if (prefix == 0) break;
            double stop = 1;
            if (j + 1 < b.Conditions.Count)
            {
                double reach = 1;
                for (int k = c.Length; k < b.Conditions[j + 1].Length; k++) reach *= q[k];
                stop = 1 - reach;
            }
            total += c.Pay * prefix * stop;
        }
        return total;
    }

    /// <summary>Expected ways win (credits) per spin for one mystery outcome.</summary>
    public double Expected(MysteryModel mystery, MysteryModel.Outcome outcome)
    {
        double total = 0;
        foreach (var b in blocks)
        {
            int n = b.Symbols.Length;
            var s = new double[n]; var q = new double[n];
            for (int x = 0; x < n; x++)
            {
                var reel = model.Reels[columns[x].reel];
                foreach (var (p, count) in reel.Stops)
                {
                    var (sp, qp) = ColumnAtStop(x, b, p, mystery, outcome);
                    s[x] += count * sp; q[x] += count * qp;
                }
                s[x] /= reel.Range; q[x] /= reel.Range;
            }
            total += BlockWin(b, s, q);
        }
        return total;
    }

    /// <summary>Expected ways win for given reel stops (over the in-spin draws), for verification.</summary>
    public double Predict(uint[] stops, MysteryModel mystery, MysteryModel.Outcome outcome)
    {
        double total = 0;
        foreach (var b in blocks)
        {
            int n = b.Symbols.Length;
            var s = new double[n]; var q = new double[n];
            for (int x = 0; x < n; x++) (s[x], q[x]) = ColumnAtStop(x, b, stops[columns[x].reel], mystery, outcome);
            total += BlockWin(b, s, q);
        }
        return total;
    }
}
