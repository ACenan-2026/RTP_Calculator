using AgtEngine.Core.Graphic;
using AgtEngine.Core.Misc;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Analysis.Coverage;

/// <summary>
/// Lists the constructs a game's base layout uses, read from the engine's loaded objects (not from the
/// .agm text), and classifies each replacement the way the analytic evaluators need:
/// what kind of draw it makes, whether its trigger is always true, and whether it can change symbols
/// that take part in line wins or scatter counts.
/// </summary>
public sealed class ConstructScanner
{
    public sealed class ReplacementInfo
    {
        public string Trigger { get; set; } = "";
        public int TriggerIndex { get; set; }
        public int Order { get; set; }
        public string Kind { get; set; } = "";
        public string Original { get; set; } = "";
        public string Replacement { get; set; } = "";
        public bool TriggerAlwaysTrue { get; set; }
        public bool TouchesLines { get; set; }
        public bool TouchesScatter { get; set; }
        public uint Probability { get; set; }
        public uint MaxProbability { get; set; }
        public AGTReplacement Source { get; set; } = null!;
        public override string ToString() =>
            $"{Kind}[{Original}->{Replacement}] on '{Trigger}'{(TriggerAlwaysTrue ? "" : " (conditional)")}" +
            $"{(TouchesLines ? " lines" : "")}{(TouchesScatter ? " scatter" : "")}{(Probability < MaxProbability ? $" p={Probability}/{MaxProbability}" : "")}";
    }

    public sealed class Result
    {
        public string Game { get; set; } = "";
        public string BaseLayout { get; set; } = "";
        public int Layouts { get; set; }
        public int Paylines { get; set; }
        public int LinePayConditions { get; set; }
        public int ScatterPayConditions { get; set; }
        public int PatternPayConditions { get; set; }
        public int LocatedPayConditions { get; set; }
        public int CombinedPayConditions { get; set; }
        public int ReelGroupSets { get; set; }
        public int MultiplierSymbols { get; set; }
        public int MetaTokens { get; set; }
        public bool PoolCredits { get; set; }
        public List<ReplacementInfo> Replacements { get; } = new List<ReplacementInfo>();
        public HashSet<string> Constructs { get; } = new HashSet<string>(StringComparer.Ordinal);
    }

    public static Result Scan(GameHost host)
    {
        var game = host.Game;
        var layout = game.Layouts.Entry(host.BaseLayoutIndex).Value;
        var r = new Result
        {
            Game = game.UserGameName.CString,
            BaseLayout = host.LayoutName(host.BaseLayoutIndex),
            Layouts = game.Layouts.Count,
            Paylines = layout.PayLines.Count,
            ReelGroupSets = layout.ReelGroupSets.Count(g => g.Value.ReelGroups.Count > 1),
            MultiplierSymbols = (int)layout.MultiplierSymbols.NumSymbols(),
            MetaTokens = game.MetaTokens.Count
        };

        var lineSymbols = new List<AGTSymbol>();
        var scatterNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in layout.PayConditions)
        {
            var pc = entry.Value;
            if (!pc.Statistics.Pool.IsEmpty) r.PoolCredits = true;
            if (pc.IsPatternPayC) r.PatternPayConditions++;
            else if (pc.CombinedWin) r.CombinedPayConditions++;
            else if (pc.Scatter)
            {
                r.ScatterPayConditions++;
                for (uint i = 0; i < pc.Symbols.NumSymbols(); i++)
                {
                    scatterNames.Add(pc.Symbols[i].Name.CString);
                    if (!pc.Symbols[i].AliasName.IsEmpty) scatterNames.Add(pc.Symbols[i].AliasName.CString);
                }
            }
            else if (pc.Located) r.LocatedPayConditions++;
            else
            {
                r.LinePayConditions++;
                for (uint i = 0; i < pc.Symbols.NumSymbols(); i++) lineSymbols.Add(pc.Symbols[i]);
            }
        }

        bool TouchesLines(AGTSymbol s) => s.Qualifier.IsSubstitute || lineSymbols.Any(l => l == s);
        bool TouchesScatter(AGTSymbol s) => scatterNames.Contains(s.Name.CString) || (!s.AliasName.IsEmpty && scatterNames.Contains(s.AliasName.CString));
        IEnumerable<AGTSymbol> SetSymbols(string name)
        {
            if (name.Length == 0 || !layout.SymbolSets.Contains(name)) yield break;
            var set = layout.SymbolSets[name];
            for (uint i = 0; i < set.NumSymbols(); i++) yield return set[i];
        }

        int order = 0;
        foreach (var entry in layout.Replacements)
        {
            int triggerIndex = layout.PayConditions.KeyIndex(entry.Key);
            var trigger = layout.PayConditions.Count > 0 ? layout.PayConditions.Entry(triggerIndex).Value : null;
            foreach (var rpl in entry.Value)
            {
                var symbols = SetSymbols(rpl.Original.CString).Concat(SetSymbols(rpl.Replacement.CString)).ToList();
                var info = new ReplacementInfo
                {
                    Trigger = entry.Key,
                    TriggerIndex = triggerIndex,
                    Order = order++,
                    Kind = KindOf(rpl),
                    Original = rpl.Original.CString,
                    Replacement = rpl.Replacement.CString,
                    TriggerAlwaysTrue = trigger is not null && IsAlwaysTrue(trigger),
                    TouchesLines = symbols.Any(TouchesLines),
                    TouchesScatter = symbols.Any(TouchesScatter),
                    Probability = rpl.Probability,
                    MaxProbability = rpl.MaxProbabilityRange,
                    Source = rpl
                };
                r.Replacements.Add(info);
                r.Constructs.Add("replace:" + info.Kind);
            }
        }
        if (r.LinePayConditions > 0) r.Constructs.Add("lines");
        if (r.ScatterPayConditions > 0) r.Constructs.Add("scatter");
        if (r.PatternPayConditions > 0) r.Constructs.Add("pattern(ways)");
        if (r.ReelGroupSets > 0) r.Constructs.Add("reel-group-sets");
        if (r.MultiplierSymbols > 0) r.Constructs.Add("multiplier-symbols");
        if (r.MetaTokens > 0) r.Constructs.Add("metamorphic");
        if (r.PoolCredits) r.Constructs.Add("pool-credits");
        return r;
    }

    public static string KindOf(AGTReplacement r)
    {
        if (r.FloatingReplacement) return "floating";
        if (r.SymbolMappedReplacement) return "mapped";
        if (r.CoordinateBasedReplacement) return r.ReplaceInReel ? "mystery" : "coordinate";
        if (r.ChooseSymbolOnProbability) return "choose-on-probability";
        if (r.RandomlySelectFromSet) return r.RemoveReplacedSymbolFromSet ? "random-without-repeat" : "random-per-cell";
        return "fixed";
    }

    /// <summary>
    /// A located pay condition whose every symbol is a substitute (e.g. <c>Winl AlwaysTrueWin Win_Set {(0,0,0,0)}</c>
    /// with <c>Win_Set {win*}</c>) and that depends on nothing: it wins on every spin.
    /// </summary>
    public static bool IsAlwaysTrue(AGTPayCondition pc)
    {
        if (!pc.Located || pc.Scatter || pc.CombinedWin || pc.Dependency.DependentNames.Length > 0 || !pc.Enabled) return false;
        if (pc.Symbols.NumSymbols() == 0) return false;
        for (uint i = 0; i < pc.Symbols.NumSymbols(); i++) if (!pc.Symbols[i].Qualifier.IsSubstitute) return false;
        return true;
    }
}
