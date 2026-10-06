using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Floating replacements that change payline symbols (AGTLayout.UpdateFloatingReplacements):
/// when the trigger wins, up to N times the engine draws a replacement name (weighted); if the mapping
/// set maps it, a uniformly random still-eligible target cell gets the mapped symbol.
///
/// Two shapes are supported exactly:
///  * placement  - the original set matches any symbol (e.g. s_any): every target cell is eligible
///                 whatever it shows, so WHERE symbols land is independent of the reels. For one line the
///                 probability that a particular subset S of its cells is hit by K placements among m
///                 targets is C(m-c, K-|S|) / C(m, K); each hit cell gets an independent mapped symbol.
///  * per-cell   - the original set is specific (e.g. My) and every name is mapped. When N is at least the
///                 number of eligible cells on screen, every eligible cell is replaced independently. When more
///                 cells can be eligible than N ("partial"), only N of the M eligible cells are replaced, chosen
///                 uniformly: the main calculation treats the rule as replacing every eligible cell, and an exact
///                 correction is added over the (rare) screens with M &gt; N, enumerated per reel by which target
///                 windows show eligible symbols.
///
/// Triggers must be located wins on reels that carry no payline cells ("driver" reels, usually hidden).
/// The calculation is conditioned on the driver reels' windows; for each driver window combination the
/// engine itself says which of these triggers fire (and how often, if a random draw decides), using an
/// analysis-only engine instance in which these replacements are switched off so their placements are
/// not enumerated. Verification against the unmodified engine checks the whole construction.
/// </summary>
public sealed class FloatingModel
{
    public const string NeverSetName = EngineSurgery.NeverSetName;

    private sealed class Rule
    {
        public ReplacementCatalog.Entry Entry = null!;
        public int Bit;
        public int ArrayIndex;
        public bool Placement;
        /// <summary>Specific inputs, but more cells can be eligible than there are picks: only some get replaced.</summary>
        public bool Partial;
        public HashSet<(int, int)> Targets = new HashSet<(int, int)>();
        public AGTSymbolSet Original = null!;
        public List<(double p, int n)> Counts = new List<(double, int)>();
        public double MappedProbability;
        public List<(double p, AGTSymbol s)> Symbols = new List<(double, AGTSymbol)>(); // conditional on being mapped
    }

    private readonly List<Rule> rules = new List<Rule>();
    private readonly ReelModel model;
    private readonly LineEvaluator lines;
    private readonly MysteryModel mystery;
    private readonly Dictionary<string, AGTSymbol> inertRepresentative = new Dictionary<string, AGTSymbol>();
    private readonly Dictionary<string, List<(double p, ulong mask)>> firing = new Dictionary<string, List<(double, ulong)>>(StringComparer.Ordinal);

    public HashSet<ReplacementCatalog.Entry> Handled { get; } = new HashSet<ReplacementCatalog.Entry>();
    public List<int> DriverReels { get; } = new List<int>();
    public List<List<StopGroup>> DriverGroups { get; } = new List<List<StopGroup>>();
    public long Configurations { get; private set; }
    public long OracleActs { get; private set; }
    public string Description { get; private set; } = "none";
    public bool Active => rules.Count > 0;
    public Action<string>? Progress { get; set; }
    /// <summary>Replacements switched off in the oracle: the handled ones, and every one that cannot reach a trigger cell.</summary>
    private readonly List<(string trigger, int index, bool mapped)> neutralise = new List<(string, int, bool)>();
    private readonly List<string> keptForDebug = new List<string>();
    private List<uint>? debugRanges;
    private bool debugFirst;

    private FloatingModel(ReelModel model, LineEvaluator lines, MysteryModel mystery) { this.model = model; this.lines = lines; this.mystery = mystery; }

    public static FloatingModel Build(AGTLayout layout, ReelModel model, ReplacementCatalog catalog, LineEvaluator lineEval,
        MysteryModel mystery, HashSet<(int, int)> lineCells)
    {
        var f = new FloatingModel(model, lineEval, mystery);
        var candidates = catalog.Entries.Where(e => e.ActsOnLines && !mystery.Handled.Contains(e) && e.Info.Kind == "floating").ToList();
        if (candidates.Count == 0) return f;

        var lineReels = new HashSet<int>(lineCells.Select(c => c.Item1));

        foreach (var e in candidates.OrderBy(e => e.Info.TriggerIndex).ThenBy(e => e.Info.Order))
        {
            var rpl = e.Replacement;
            string what = e.ToString();
            if (rpl.ReplaceInReel || rpl.CoordinateBasedReplacement || rpl.RemoveReplacedSymbolFromSet)
                throw new NotSupportedException($"Floating replacement {what} uses ReplaceInReel / coordinate source / remove-from-set; not supported yet.");
            // Trigger: a located win read only from driver reels (or always true).
            var pcEntry = layout.PayConditions.Entry(e.Info.TriggerIndex);
            if (pcEntry.Key != e.Info.Trigger) throw new NotSupportedException($"Trigger '{e.Info.Trigger}' of {what} not found.");
            var pc = pcEntry.Value;
            if (!e.Info.TriggerAlwaysTrue)
            {
                if (!pc.Located || pc.Scatter || pc.CombinedWin || pc.Dependency.DependentNames.Length > 0)
                    throw new NotSupportedException($"Floating replacement {what} is triggered by a non-located or dependent condition; not supported yet.");
                for (uint i = 0; i < pc.Graph.NumCoordinates; i++)
                    if (!model.TryLocate(pc.Graph.Coordinate(i), out int tr, out _) || lineReels.Contains(tr))
                        throw new NotSupportedException($"Floating replacement {what} is triggered by symbols on payline reels; not supported yet.");
            }
            if (!layout.SymbolSets.Contains(rpl.Original.CString) || !layout.SymbolSets.Contains(rpl.Replacement.CString)
                || !layout.FloatingReplacementSymbolMappingSets.Contains(rpl.MappingSymbolsetName.CString))
                throw new NotSupportedException($"Floating replacement {what} refers to sets that are not loaded.");

            var rule = new Rule { Entry = e, Bit = f.rules.Count, Targets = new HashSet<(int, int)>(e.Targets), Original = layout.SymbolSets[rpl.Original.CString],
                                  ArrayIndex = Array.IndexOf(layout.Replacements[e.Info.Trigger], rpl) };
            if (f.rules.Count >= 60) throw new NotSupportedException("More than 60 line-changing floating replacements.");

            // Number of picks.
            if (rpl.NumberOfFloatingReplacements > 0) rule.Counts.Add((1.0, (int)rpl.NumberOfFloatingReplacements));
            else
            {
                string picks = rpl.NumberOfFloatingPicksRandomSymbolsetName.CString;
                if (picks.Length == 0 || !layout.SymbolSets.Contains(picks)) throw new NotSupportedException($"Floating replacement {what} has no pick count.");
                foreach (var (p, s) in Weighted(layout.SymbolSets[picks]))
                {
                    string text = s.Name.CString; int k = 0, i = 0;
                    while (i < text.Length && char.IsDigit(text[i])) { k = k * 10 + (text[i] - '0'); i++; }
                    if (i == 0) throw new NotSupportedException($"Pick-count symbol '{text}' of {what} is not a number.");
                    rule.Counts.Add((p, k));
                }
            }

            // Names drawn, and what they map to.
            var mapping = layout.FloatingReplacementSymbolMappingSets[rpl.MappingSymbolsetName.CString];
            double mapped = 0;
            var symbols = new List<(double p, AGTSymbol s)>();
            foreach (var (p, s) in Weighted(layout.SymbolSets[rpl.Replacement.CString]))
            {
                string name = s.Name.CString;
                if (layout.SymbolSets.Contains(name)) throw new NotSupportedException($"Floating replacement {what} draws a nested set '{name}'; not supported yet.");
                if (!mapping.Contains(name)) continue;
                var target = mapping[name];
                mapped += p; symbols.Add((p, new AGTSymbol(target)));
            }
            if (mapped <= 0) continue; // never places anything
            rule.MappedProbability = mapped;
            rule.Symbols = symbols.Select(x => (x.p / mapped, x.s)).ToList();

            if (e.AnyInput) rule.Placement = true;
            else
            {
                int eligible = rule.Targets.Count(t => catalog.CanAct(e, t));
                int minPicks = rule.Counts.Min(c => c.n);
                if (mapped < 1 - 1e-12)
                    throw new NotSupportedException($"Floating replacement {what} on specific symbols draws names that are not mapped; not supported yet.");
                rule.Partial = minPicks < eligible;
            }
            f.rules.Add(rule);
            f.Handled.Add(e);
        }
        if (f.rules.Count == 0) return f;

        // Placements are applied after the per-cell rules: that is only the engine's result if no per-cell rule
        // could act on a placed symbol, and no placement can change a trigger cell.
        var placed = new HashSet<string>(f.rules.Where(r => r.Placement).SelectMany(r => r.Symbols).Select(x => x.s.Name.CString), StringComparer.Ordinal);
        foreach (var r in f.rules.Where(r => !r.Placement))
            if (r.Entry.Inputs.Any(placed.Contains)) throw new NotSupportedException($"Floating replacement {r.Entry} acts on symbols placed by another floating replacement.");
        foreach (var e in mystery.Handled) if (e.Inputs.Any(placed.Contains)) throw new NotSupportedException($"Replacement {e} acts on symbols placed by a floating replacement.");
        foreach (var r in f.rules)
        {
            var pc = layout.PayConditions.Entry(r.Entry.Info.TriggerIndex).Value;
            for (uint i = 0; i < pc.Graph.NumCoordinates; i++)
                if (model.TryLocate(pc.Graph.Coordinate(i), out int tr, out int tw) && f.rules.Any(o => o.Placement && o.Targets.Contains((tr, tw))))
                    throw new NotSupportedException($"A floating placement can change the trigger cell of {r.Entry}.");
        }
        // Driver reels: the reels whose content can decide these triggers (or the mystery outcome), following
        // replacement chains backwards (a trigger cell written by another replacement depends on that
        // replacement's own trigger and source). Reels that cannot matter are left out of the conditioning.
        var relevant = new HashSet<(int, int)>();
        void AddPcCells(AGTPayCondition pc)
        {
            for (uint i = 0; i < pc.Graph.NumCoordinates; i++)
                if (model.TryLocate(pc.Graph.Coordinate(i), out int r, out int w)) relevant.Add((r, w));
        }
        foreach (var r in f.rules) if (!r.Entry.Info.TriggerAlwaysTrue) AddPcCells(layout.PayConditions.Entry(r.Entry.Info.TriggerIndex).Value);
        foreach (int source in mystery.SourceReels)
            for (int w = 0; w < model.Reels[source].Coordinates.Length; w++) relevant.Add((source, w));
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var e in catalog.Entries)
            {
                if (!e.Targets.Any(t => relevant.Contains(t) && catalog.CanAct(e, t))) continue;
                int before = relevant.Count;
                if (!e.Info.TriggerAlwaysTrue)
                {
                    var pc = layout.PayConditions.Entry(e.Info.TriggerIndex).Value;
                    if (!pc.Located || pc.Scatter || pc.CombinedWin || pc.Dependency.DependentNames.Length > 0)
                        throw new NotSupportedException($"A floating trigger depends on {e}, which is triggered by a non-located condition; not supported yet.");
                    AddPcCells(pc);
                }
                if (e.Source is (int, int) src) relevant.Add(src);
                if (relevant.Count != before) grew = true;
            }
        }
        foreach (var cell in relevant)
            if (lineReels.Contains(cell.Item1))
                throw new NotSupportedException("A floating replacement's trigger depends on symbols on payline reels; not supported yet.");
        // Single-cell hidden reels are selector reels: their symbols can also enable or disable trigger conditions
        // indirectly, so they are always conditioned on. Multi-row hidden reels are included only when a chain
        // reaches them. The oracle checks that firing does not change when the other reels change.
        var drivers = new HashSet<int>(relevant.Select(c => c.Item1));
        for (int r = 0; r < model.Reels.Count; r++)
            if (!lineReels.Contains(r) && model.Reels[r].Coordinates.Length == 1) drivers.Add(r);
        f.DriverReels.AddRange(drivers.OrderBy(r => r));

        // Oracle: only cells in 'relevant' (plus single-cell driver reels) can decide the triggers, so every
        // replacement that cannot write such a cell is switched off there, together with the handled ones.
        // That keeps the oracle's in-spin enumeration small (no coin placements, prize draws, ...).
        var keep = new HashSet<(int, int)>(relevant);
        foreach (int r in drivers) for (int w = 0; w < model.Reels[r].Coordinates.Length; w++) keep.Add((r, w));
        foreach (var e in catalog.Entries)
        {
            bool handled = f.Handled.Contains(e);
            bool reaches = e.Targets.Any(t => keep.Contains(t) && catalog.CanAct(e, t));
            if (!handled && reaches) { f.keptForDebug.Add(e.ToString()); continue; }
            int index = Array.IndexOf(layout.Replacements[e.Info.Trigger], e.Replacement);
            if (index >= 0) f.neutralise.Add((e.Info.Trigger, index, e.Info.Kind == "mapped"));
        }

        foreach (int r in f.DriverReels)
            f.DriverGroups.Add(StopGrouping.Group(model.Reels[r], w => string.Join("|", w), w => 0));
        double configs = f.DriverGroups.Aggregate(1.0, (a, g) => a * g.Count);
        if (configs > 20_000) throw new NotSupportedException($"Floating replacements: {configs:N0} driver-reel window combinations; too many.");
        f.Configurations = (long)configs;
        f.Description = $"{f.rules.Count(r => r.Placement)} placement + {f.rules.Count(r => !r.Placement)} per-cell floating replacement(s), " +
                        $"{f.DriverReels.Count} driver reel(s), {f.Configurations:N0} driver configurations";
        return f;
    }

    private static List<(double p, AGTSymbol s)> Weighted(AGTSymbolSet set)
    {
        uint range = set.NumSymbolsBasedOnExtension;
        var counts = new Dictionary<uint, long>();
        for (uint v = 0; v < range; v++) { uint i = set.ActualSymbolIndexInExtendedSet(v); counts[i] = counts.TryGetValue(i, out long c) ? c + 1 : 1; }
        return counts.Select(kv => ((double)kv.Value / range, set[kv.Key])).ToList();
    }

    /// <summary>Key of the driver-reel windows shown by a set of stops.</summary>
    private string ConfigKey(uint[] stops) => string.Join("#", DriverReels.Select(r => string.Join("|", model.Reels[r].Windows[stops[r]])));

    /// <summary>
    /// Ask the engine, for every driver configuration, which of these replacements fire (with probabilities if
    /// in-spin draws decide). Uses a separate engine instance in which the replacements are switched off.
    /// </summary>
    public void Calibrate(byte[] dat, uint betPerLine, uint linesBet, BaseSetup setup, long pathLimit)
    {
        if (!Active) return;
        // Feature entry does not matter here (every path is restored), so suppress it only where possible.
        GameHost oracle;
        try { oracle = GameHost.Load(dat, betPerLine, linesBet, 31, suppressFeatureEntry: true); }
        catch (NotSupportedException) { oracle = GameHost.Load(dat, betPerLine, linesBet, 31); }
        int baseIndex = oracle.BaseLayoutIndex;
        // Switch off the handled replacements, and those that cannot reach a trigger cell, in the oracle.
        EngineSurgery.SwitchOff(oracle, neutralise.Select(n => new EngineSurgery.ReplacementRef(n.trigger, n.index, n.mapped)));
        if (Environment.GetEnvironmentVariable("RTP_DEBUG") == "1")
        {
            Progress?.Invoke("oracle switches off: " + string.Join("; ", neutralise.Select(n => $"{n.trigger}[{n.index}]")));
            Progress?.Invoke("oracle keeps: " + string.Join("; ", keptForDebug));
            oracle.Agent.Observer = r => { if (debugRanges is not null) debugRanges.Add(r); };
        }
        var enumerator = new ActEnumerator(oracle, new DrawPartition(oracle.Game)) { PathLimitPerAct = pathLimit, AlwaysRestore = false };
        enumerator.SetScenario(setup.PrepareValues, setup.PrepareRanges);
        var triggerNames = rules.Select(r => r.Entry.Info.Trigger).ToArray();

        var stops = setup.Model.Reels.Select(r => 0u).ToArray();
        var choice = new int[DriverReels.Count];
        var rng = new System.Random(77); int checks = Environment.GetEnvironmentVariable("RTP_NOCHECK") == "1" ? 0 : 25;
        while (true)
        {
            for (int i = 0; i < DriverReels.Count; i++) stops[DriverReels[i]] = DriverGroups[i][choice[i]].Representative;
            var dist = new Dictionary<ulong, double>();
            long before = enumerator.Paths;
            if (Environment.GetEnvironmentVariable("RTP_DEBUG") == "1") { debugRanges = new List<uint>(); debugFirst = true; }
            enumerator.Enumerate(stops, (h, res, w) =>
            {
                var layout = h.Game.Layouts.Entry(baseIndex).Value;
                ulong mask = 0;
                for (int i = 0; i < rules.Count; i++)
                    if (layout.PayConditions[triggerNames[i]].Dependency.PossibleWin) mask |= 1UL << rules[i].Bit;
                dist[mask] = (dist.TryGetValue(mask, out double v) ? v : 0) + w;
                if (debugFirst) { debugFirst = false; Progress?.Invoke("  first path ranges: " + string.Join(",", h.Agent.Ranges)); }
            });
            firing[ConfigKey(stops)] = dist.Select(kv => (kv.Value, kv.Key)).ToList();
            if (debugRanges is not null) { Progress?.Invoke("  ranges: " + string.Join(",", debugRanges.Take(60))); debugRanges = null; }
            Progress?.Invoke($"driver config {firing.Count}/{Configurations}: {enumerator.Paths - before:N0} engine acts, {dist.Count} firing pattern(s)");
            if (checks-- > 0)
            {
                // Same driver windows, different other reels: the firing distribution must not change.
                var other = (uint[])stops.Clone();
                for (int r = 0; r < other.Length; r++)
                    if (!DriverReels.Contains(r)) other[r] = (uint)rng.Next((int)setup.Model.Reels[r].Range);
                var dist2 = new Dictionary<ulong, double>();
                long limit = enumerator.PathLimitPerAct; enumerator.PathLimitPerAct = 20_000;
                try
                {
                enumerator.Enumerate(other, (h, res, w) =>
                {
                    var layout = h.Game.Layouts.Entry(baseIndex).Value;
                    ulong mask = 0;
                    for (int i = 0; i < rules.Count; i++)
                        if (layout.PayConditions[triggerNames[i]].Dependency.PossibleWin) mask |= 1UL << rules[i].Bit;
                    dist2[mask] = (dist2.TryGetValue(mask, out double v) ? v : 0) + w;
                });
                foreach (var kv in dist.Concat(dist2))
                    if (Math.Abs((dist.TryGetValue(kv.Key, out double a) ? a : 0) - (dist2.TryGetValue(kv.Key, out double b) ? b : 0)) > 1e-12)
                        throw new NotSupportedException("Which floating replacements fire depends on payline reels, not only on the driver reels; not supported yet.");
                }
                catch (NotSupportedException e) when (e.Message.Contains("internal random branches")) { checks = 0; } // too costly to check here; verification still compares with the engine
                finally { enumerator.PathLimitPerAct = limit; }
            }
            int d = DriverReels.Count - 1;
            while (d >= 0 && ++choice[d] == DriverGroups[d].Count) { choice[d] = 0; d--; }
            if (d < 0) break;
        }
        OracleActs = enumerator.Paths;
    }

    /// <summary>Iterate driver configurations: (weight, stops-with-driver-reels-set, key).</summary>
    public IEnumerable<(double weight, uint[] stops, string key)> DriverConfigurations()
    {
        var stops = model.Reels.Select(r => 0u).ToArray();
        var choice = new int[DriverReels.Count];
        while (true)
        {
            double w = 1;
            for (int i = 0; i < DriverReels.Count; i++)
            {
                var g = DriverGroups[i][choice[i]];
                stops[DriverReels[i]] = g.Representative;
                w *= (double)g.Count / model.Reels[DriverReels[i]].Range;
            }
            yield return (w, (uint[])stops.Clone(), ConfigKey(stops));
            int d = DriverReels.Count - 1;
            while (d >= 0 && ++choice[d] == DriverGroups[d].Count) { choice[d] = 0; d--; }
            if (d < 0) yield break;
        }
    }

    public List<(double p, ulong mask)> Firing(string key) =>
        firing.TryGetValue(key, out var f) ? f : throw new InvalidOperationException("No oracle result for this driver configuration.");

    public List<(double p, ulong mask)> FiringFor(uint[] stops) => Firing(ConfigKey(stops));

    private AGTSymbol Representative(AGTSymbol s)
    {
        if (lines.TouchesLines(s)) return s;
        // Symbols that can never take part in a line win are interchangeable for line pays.
        if (!inertRepresentative.TryGetValue("inert", out var r)) inertRepresentative["inert"] = r = s;
        return r;
    }

    /// <summary>
    /// Expected win on one line for given per-cell symbol distributions (after mystery / per-cell rules that
    /// are not floating) and a firing mask: applies fired per-cell floating rules, then enumerates which line
    /// cells the fired placements hit. A fired partial rule is applied to every eligible line cell unless
    /// <paramref name="screenEligible"/> (the number of eligible target cells on the whole screen) is given;
    /// then exactly the engine's uniform choice of min(N, M) cells is used, which needs point-mass line cells.
    /// </summary>
    public double ExpectedLineWin(List<(int reel, int window)> cells, List<List<(double p, AGTSymbol s)>> dists, ulong mask,
        Func<List<List<(double p, AGTSymbol s)>>, double> evaluate, int? screenEligible = null)
    {
        if (rules.Count(r => r.Partial && (mask & (1UL << r.Bit)) != 0) > 1)
            throw new NotSupportedException("Two partial floating replacements fire in the same spin; not supported yet.");

        // Per-cell rules (and partial rules), producing weighted variants of the line's cell distributions.
        var variants = new List<(double p, List<List<(double p, AGTSymbol s)>> d)> { (1.0, dists.Select(d => d.ToList()).ToList()) };
        foreach (var rule in rules)
        {
            if ((mask & (1UL << rule.Bit)) == 0 || rule.Placement) continue;
            if (rule.Partial && screenEligible is int m)
            {
                var next = new List<(double p, List<List<(double p, AGTSymbol s)>> d)>();
                foreach (var (pv, d) in variants)
                {
                    var eligible = new List<int>();
                    for (int i = 0; i < cells.Count; i++)
                    {
                        if (!rule.Targets.Contains(cells[i])) continue;
                        bool any = d[i].Any(x => rule.Original.ContainsSymbol(x.s)), all = d[i].All(x => rule.Original.ContainsSymbol(x.s));
                        if (any && !all) throw new InvalidOperationException("Partial floating replacement needs known eligibility per line cell.");
                        if (all) eligible.Add(i);
                    }
                    int e = eligible.Count;
                    if (m < e) throw new InvalidOperationException("Screen eligible count is smaller than the line's eligible cells.");
                    foreach (var (pn, n) in rule.Counts)
                    {
                        int k = Math.Min(n, m);
                        for (int subset = 0; subset < (1 << e); subset++)
                        {
                            int j = Popcount(subset);
                            if (j > k || k - j > m - e) continue;
                            double ps = Binomial(m - e, k - j) / Binomial(m, k);
                            if (ps == 0) continue;
                            var copy = d.Select(x => x).ToList();
                            for (int b = 0; b < e; b++)
                                if ((subset & (1 << b)) != 0) copy[eligible[b]] = rule.Symbols.ToList();
                            next.Add((pv * pn * ps, copy));
                        }
                    }
                }
                variants = next;
                continue;
            }
            // Every eligible cell is replaced, independently.
            foreach (var (_, d) in variants)
                for (int i = 0; i < cells.Count; i++)
                {
                    if (!rule.Targets.Contains(cells[i])) continue;
                    var next = new List<(double p, AGTSymbol s)>();
                    foreach (var (p, s) in d[i])
                        if (rule.Original.ContainsSymbol(s)) foreach (var (q, r) in rule.Symbols) next.Add((p * q, r));
                        else next.Add((p, s));
                    d[i] = next;
                }
        }

        // Placements: distribution over which line cells end with which placed symbol (null = untouched).
        var states = new Dictionary<string, (double p, AGTSymbol?[] over)>(StringComparer.Ordinal)
        { [""] = (1.0, new AGTSymbol?[cells.Count]) };
        foreach (var rule in rules)
        {
            if ((mask & (1UL << rule.Bit)) == 0 || !rule.Placement) continue;
            var inLine = Enumerable.Range(0, cells.Count).Where(i => rule.Targets.Contains(cells[i])).ToList();
            if (inLine.Count == 0) continue;
            int m = rule.Targets.Count, c = inLine.Count;
            // Distribution of the number of cells placed: min(Binomial(n, q), m), mixed over n.
            var placedK = new Dictionary<int, double>();
            foreach (var (pn, n) in rule.Counts)
                for (int k = 0; k <= n; k++)
                {
                    double pk = pn * Binomial(n, k) * Math.Pow(rule.MappedProbability, k) * Math.Pow(1 - rule.MappedProbability, n - k);
                    if (pk == 0) continue;
                    int kk = Math.Min(k, m);
                    placedK[kk] = (placedK.TryGetValue(kk, out double v) ? v : 0) + pk;
                }
            var classes = rule.Symbols.GroupBy(x => Representative(x.s)).Select(g => (p: g.Sum(x => x.p), s: g.Key)).ToList();
            var next = new Dictionary<string, (double p, AGTSymbol?[] over)>(StringComparer.Ordinal);
            foreach (var st in states.Values)
                foreach (var kv in placedK)
                {
                    int k = kv.Key;
                    for (int subset = 0; subset < (1 << c); subset++)
                    {
                        int j = Popcount(subset);
                        if (j > k || k - j > m - c) continue;
                        double ps = Binomial(m - c, k - j) / Binomial(m, k);
                        if (ps == 0) continue;
                        var hit = Enumerable.Range(0, c).Where(b => (subset & (1 << b)) != 0).Select(b => inLine[b]).ToList();
                        foreach (var (pa, over) in Assign(st.over, hit, classes))
                        {
                            string key = string.Join(",", over.Select(o => o is null ? "-" : o.Name.CString));
                            double add = st.p * kv.Value * ps * pa;
                            next[key] = next.TryGetValue(key, out var e) ? (e.p + add, e.over) : (add, over);
                        }
                    }
                }
            states = next;
        }

        double total = 0;
        foreach (var (pv, d) in variants)
            foreach (var st in states.Values)
            {
                if (st.p == 0 || pv == 0) continue;
                var final = d.Select((x, i) => st.over[i] is AGTSymbol o ? new List<(double p, AGTSymbol s)> { (1.0, o) } : x).ToList();
                total += pv * st.p * evaluate(final);
            }
        return total;
    }

    /// <summary>Number of eligible target cells of the fired partial rule on a concrete screen (for verification).</summary>
    public int? ScreenEligible(uint[] stops, ulong mask, MysteryModel.Outcome outcome)
    {
        var rule = rules.FirstOrDefault(r => r.Partial && (mask & (1UL << r.Bit)) != 0);
        if (rule is null) return null;
        int m = 0;
        foreach (var (reel, window) in rule.Targets)
        {
            var eff = mystery.Effective(reel, window, model.Reels[reel].WindowSymbols[stops[reel]][window], outcome);
            if (eff.Count == 1 && rule.Original.ContainsSymbol(eff[0].s)) m++;
            else if (eff.Count > 1) throw new NotSupportedException("Random per-cell replacement on a partial floating target; not supported yet.");
        }
        return m;
    }

    /// <summary>
    /// Exact correction for a fired partial rule: sum over screens on which more target cells are eligible
    /// than the smallest pick count, of P(screen pattern) x (line win if every eligible cell were replaced
    /// - line win with the engine's capped random choice). Patterns are enumerated per target reel by which
    /// target windows show an eligible symbol; line cells are conditioned on their reel's pattern.
    /// </summary>
    public double Correction(List<(int reel, int window)> cells, MysteryModel.Outcome outcome, ulong mask,
        Func<List<List<(double p, AGTSymbol s)>>, double> evaluate)
    {
        var rule = rules.FirstOrDefault(r => r.Partial && (mask & (1UL << r.Bit)) != 0);
        if (rule is null) return 0;
        int minPicks = rule.Counts.Min(c => c.n);
        var targetReels = rule.Targets.Select(t => t.Item1).Distinct().OrderBy(r => r).ToList();
        int lineIndexOf(int reel) => cells.FindIndex(c => c.reel == reel);

        // Per target reel: pattern -> (probability, conditional distribution of this line's cell on that reel).
        var perReel = new List<List<(int bits, double p, List<(double p, AGTSymbol s)>? line)>>();
        foreach (int r in targetReels)
        {
            var reel = model.Reels[r];
            var windows = rule.Targets.Where(t => t.Item1 == r).Select(t => t.Item2).OrderBy(w => w).ToList();
            int li = lineIndexOf(r);
            var groups = new Dictionary<int, (long count, Dictionary<string, (long c, AGTSymbol s)> line)>();
            for (uint p = 0; p < reel.Range; p++)
            {
                int bits = 0;
                for (int i = 0; i < windows.Count; i++)
                {
                    var eff = mystery.Effective(r, windows[i], reel.WindowSymbols[p][windows[i]], outcome);
                    if (eff.Count != 1) throw new NotSupportedException("Random per-cell replacement on a partial floating target; not supported yet.");
                    if (rule.Original.ContainsSymbol(eff[0].s)) bits |= 1 << i;
                }
                if (!groups.TryGetValue(bits, out var g)) groups[bits] = g = (0, new Dictionary<string, (long, AGTSymbol)>(StringComparer.Ordinal));
                var lineMap = g.line;
                if (li >= 0)
                {
                    var eff = mystery.Effective(r, cells[li].window, reel.WindowSymbols[p][cells[li].window], outcome)[0].s;
                    string n = eff.Name.CString;
                    lineMap[n] = lineMap.TryGetValue(n, out var x) ? (x.c + 1, x.s) : (1, eff);
                }
                groups[bits] = (g.count + 1, lineMap);
            }
            perReel.Add(groups.Select(kv => (kv.Key, (double)kv.Value.count / reel.Range,
                li < 0 ? null : kv.Value.line.Values.Select(v => ((double)v.c / kv.Value.count, v.s)).ToList())).ToList());
        }

        // Marginal distributions for line cells on reels the rule does not target.
        double total = 0;
        var choice = new int[perReel.Count];
        while (true)
        {
            int m = 0; double p = 1;
            for (int i = 0; i < perReel.Count; i++) { var x = perReel[i][choice[i]]; m += Popcount(x.bits); p *= x.p; }
            if (m > minPicks && p > 0)
            {
                var dists = cells.Select(c =>
                {
                    int ti = targetReels.IndexOf(c.reel);
                    if (ti >= 0) return perReel[ti][choice[ti]].line!;
                    return LineEvaluator.CellDistribution(model.Reels[c.reel], c.reel, c.window, mystery, outcome);
                }).ToList();
                double unlimited = ExpectedLineWin(cells, dists, mask, evaluate);
                double capped = ExpectedLineWin(cells, dists, mask, evaluate, screenEligible: m);
                total += p * (unlimited - capped);
            }
            int d = perReel.Count - 1;
            while (d >= 0 && ++choice[d] == perReel[d].Count) { choice[d] = 0; d--; }
            if (d < 0) break;
        }
        return total;
    }

    private static IEnumerable<(double p, AGTSymbol?[] over)> Assign(AGTSymbol?[] start, List<int> hit, List<(double p, AGTSymbol s)> classes)
    {
        if (hit.Count == 0) { yield return (1.0, start); yield break; }
        var idx = new int[hit.Count];
        while (true)
        {
            var over = (AGTSymbol?[])start.Clone(); double p = 1;
            for (int i = 0; i < hit.Count; i++) { over[hit[i]] = classes[idx[i]].s; p *= classes[idx[i]].p; }
            yield return (p, over);
            int d = hit.Count - 1;
            while (d >= 0 && ++idx[d] == classes.Count) { idx[d] = 0; d--; }
            if (d < 0) yield break;
        }
    }

    private static int Popcount(int x) { int c = 0; while (x != 0) { c += x & 1; x >>= 1; } return c; }

    private static double Binomial(int n, int k)
    {
        if (k < 0 || k > n) return 0;
        double r = 1;
        for (int i = 1; i <= k; i++) r = r * (n - k + i) / i;
        return r;
    }
}
