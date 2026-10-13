using System.Diagnostics;
using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Analysis.Coverage;
using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Base-game RTP from the reel design rather than from reel stops.
///
///   * Spin-preparation draws (e.g. ReelGroupSet choosing reel strips) -> scenarios, mixed by weight
///   * coordinate-based mystery replacements                          -> <see cref="MysteryModel"/>
///   * line pays (Win statements)                                      -> <see cref="LineEvaluator"/>
///   * everything else (scatters, prizes, trigger, other replacements) -> <see cref="ScatterEvaluator"/>
///     (count model, fast) or <see cref="SpecialSymbolEvaluator"/> (general, engine-enumerated)
/// <see cref="ReplacementCatalog"/> works out which replacements can change payline symbols; any such
/// replacement that is not a recognised mystery makes the run stop with "not supported".
///
/// The engine stays the judge of game rules. Verification replays random reel stops through the full
/// engine and compares them with the model's predictions for those stops.
/// </summary>
public sealed class AnalyticBaseGame
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;

    public AnalyticBaseGame(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    public int VerifySamples { get; set; } = 300;
    /// <summary>Seed for choosing verification screens (vary it to check different screens in separate runs).</summary>
    public int VerifySeed { get; set; } = 4242;
    /// <summary>Maximum internal branches per spin for the engine oracle / verification.</summary>
    public long OraclePathLimit { get; set; } = 5_000_000;
    /// <summary>Maximum engine configurations for the general non-line evaluator (per scenario).</summary>
    public long ConfigurationLimit { get; set; } = 20_000;
    /// <summary>Engine configurations allowed for the class-count model (keyed combinations x count vectors).</summary>
    public long ClassConfigurationLimit { get; set; } = 50_000;
    /// <summary>Engine acts allowed for one class-count configuration (one spin with every in-spin draw).</summary>
    public long ClassPathLimit { get; set; } = 200_000;
    /// <summary>Worker threads for the class-count model (each runs its own engine).</summary>
    public int Threads { get; set; } = 1;
    /// <summary>Optional cache file for class-count engine measurements (resumes an interrupted run).</summary>
    public string? CachePath { get; set; }
    public int ScenarioLimit { get; set; } = 64;
    /// <summary>When the rule-based line models refuse a replacement, measure each reel with the engine (<see cref="ReelTransformLines"/>).</summary>
    public bool AllowReelTransform { get; set; } = true;
    /// <summary>
    /// Analyse one spin from this game state (e.g. a feature state) instead of the base game. Every engine
    /// instance is then started there (<see cref="GameHost.LoadAt"/>).
    /// </summary>
    public AgtEngine.Core.ReelGame.AGTReelGame? StartState { get; set; }
    /// <summary>State mode: records the state each act leads to (filled into <see cref="Report.NextStates"/>).</summary>
    public Feature.StateTracker? Tracker { get; set; }
    /// <summary>State mode: free spins left set to this (so acts never end the feature); null keeps the state's count.</summary>
    public int? StateFreeSpins { get; set; }

    private GameHost NewHost(int seed, bool suppress = false, bool lenient = false)
    {
        if (StartState is not null) return GameHost.LoadAt(dat, betPerLine, lines, StartState, seed, StateFreeSpins, suppressExits: true);
        return GameHost.Load(dat, betPerLine, lines, seed, suppressFeatureEntry: suppress, lenient: lenient);
    }
    /// <summary>Compute line wins only (the non-line part is reported as skipped).</summary>
    public bool SkipNonLine { get; set; }
    /// <summary>Print progress of each stage.</summary>
    public bool Verbose { get; set; }
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private void Log(string message) { if (Verbose) Console.WriteLine($"    [{clock.Elapsed:mm\\:ss}] {message}"); }
    /// <summary>Random in-spin draws per check when exact enumeration of a spin is too large.</summary>
    public int SampledDraws { get; set; } = 2000;
    /// <summary>Random spins used to check feature-entry detection against the real engine.</summary>
    public int FeatureEntryChecks { get; set; } = 3000;

    public sealed class Report
    {
        public ulong TotalBet { get; set; }
        public string Mystery { get; set; } = "";
        public int Scenarios { get; set; }
        public string ReelGroupSets { get; set; } = "none";
        public int LinePayConditions { get; set; }
        public int BetLines { get; set; }
        public int DistinctLineModels { get; set; }
        public long TuplesVisited { get; set; }
        public long PayEvaluations { get; set; }
        public double ExpectedLineWin { get; set; }
        /// <summary>Part of <see cref="ExpectedLineWin"/> paid by ways (pattern) conditions.</summary>
        public double ExpectedWaysWin { get; set; }
        public string Ways { get; set; } = "none";
        public double ExpectedNonLineWin { get; set; }
        public double TriggerProbability { get; set; }
        public string NonLineMethod { get; set; } = "";
        public string Floating { get; set; } = "none";
        public string NonLineRelevance { get; set; } = "";
        /// <summary>Set when the non-line part could not be computed; line results are still exact.</summary>
        public string? NonLineError { get; set; }
        public bool LinesOnly => NonLineError is not null;
        public int SampledChecks { get; set; }
        public double[] T { get; set; } = Array.Empty<double>();
        public double[] CountDistribution { get; set; } = Array.Empty<double>();
        public Dictionary<(int reel, int window), double> Prizes { get; set; } = new Dictionary<(int, int), double>();
        public string[] ReelNames { get; set; } = Array.Empty<string>();
        public long OracleActs { get; set; }
        public long OracleConfigurations { get; set; }
        public TimeSpan LineTime { get; set; }
        public TimeSpan ScatterTime { get; set; }
        public List<string> Notes { get; } = new List<string>();
        public int VerifiedLines { get; set; }
        public int LineMismatches { get; set; }
        public int VerifiedScatter { get; set; }
        public int ScatterMismatches { get; set; }
        public List<string> MismatchExamples { get; } = new List<string>();
        public TimeSpan VerifyTime { get; set; }
        /// <summary>Expected non-line credits per pay condition family (class-count model only).</summary>
        public Dictionary<string, double> NonLineBreakdown { get; } = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>State mode: probability of each state (tracker id) after the act.</summary>
        public Dictionary<int, double> NextStates { get; } = new Dictionary<int, double>();
        public double LineRtp => ExpectedLineWin / TotalBet;
        public double NonLineRtp => ExpectedNonLineWin / TotalBet;
    }

    /// <summary>Everything computed for one spin-preparation scenario.</summary>
    private sealed class Scenario
    {
        public double Weight;
        public BaseSetup Setup = null!;
        public MysteryModel Mystery = null!;
        public ReplacementCatalog Catalog = null!;
        public ScatterEvaluator? Count;
        public SpecialSymbolEvaluator? General;
        public ClassCountEvaluator? Classes;
        public WaysEvaluator? Ways;
        public ReelTransformLines? Transform;
        /// <summary>Line win taken from the cache (the reel measurements are not repeated, nor line checks).</summary>
        public bool LinesCached;
        public int Index;
        public double WaysWin;
        public FloatingModel Floating = null!;
        public NonLineRelevance? Relevance;
        public GameHost? NonLineHost;
        public string? NonLineError;
        public double LineWin, NonLineWin, Trigger;
        public (double win, double trigger) Predict(uint[] stops) => Count is not null ? Count.Predict(stops) : Classes is not null ? Classes.Predict(stops) : General!.Predict(stops);
    }

    public Report Run(bool verify)
    {
        var report = new Report();
        bool suppress = TrySuppress();
        var host = NewHost(21, suppress);
        var layout = host.Game.Layouts.Entry(host.BaseLayoutIndex).Value;
        report.TotalBet = host.TotalBet;
        var scan = ConstructScanner.Scan(host);

        // Tokens collected in the base game carry over between spins: one spin is not independent of the last.
        var tokenConditions = layout.PayConditions.Where(p => p.Value.TokenEnabled || p.Value.MetaMultiplierEnabled).Select(p => p.Key).ToList();
        if (tokenConditions.Count > 0)
            throw new NotSupportedException($"Base pay conditions collect metamorphic tokens ({string.Join(", ", tokenConditions.Take(3))}{(tokenConditions.Count > 3 ? ", ..." : "")}); spins are not independent, needs a state model.");

        var lineEval = new LineEvaluator(layout, betPerLine, lines);
        report.LinePayConditions = lineEval.LinePayConditions;
        var partition = new DrawPartition(host.Game);

        // ---- Scenarios: every outcome of the draws made while preparing the spin ------------------
        CheckReelGroupSets(layout, report);
        var scenarios = DiscoverScenarios(host, partition);
        report.Scenarios = scenarios.Count;

        var lineCache = new Dictionary<string, double>(StringComparer.Ordinal);
        var lineTimer = new Stopwatch(); var otherTimer = new Stopwatch();
        foreach (var sc in scenarios)
        {
            var setup = sc.Setup;
            var model = setup.Model;
            report.ReelNames = model.Reels.Select(r => r.Name).ToArray();
            report.BetLines = setup.BetLines.Count;
            var lineCells = new HashSet<(int, int)>(setup.LineCells.Values.SelectMany(c => c));
            sc.Catalog = ReplacementCatalog.Build(layout, model, scan, lineCells, lineEval, setup.ScatterSymbols);
            sc.Mystery = MysteryModel.Build(layout, model, sc.Catalog);
            report.Mystery = sc.Mystery.Description;
            string? ruleError = null;
            try
            {
                sc.Floating = FloatingModel.Build(layout, model, sc.Catalog, lineEval, sc.Mystery, lineCells);
                if (sc.Floating.Active) report.Floating = sc.Floating.Description;
                var blocking = sc.Catalog.Entries.Where(e => e.ActsOnLines && !sc.Mystery.Handled.Contains(e) && !sc.Floating.Handled.Contains(e)).ToList();
                if (blocking.Count > 0)
                    throw new NotSupportedException($"Replacement {blocking[0]} can change symbols on paylines" +
                                                    (blocking.Count > 1 ? $" (+{blocking.Count - 1} more)" : "") + "; not supported analytically yet.");
            }
            catch (NotSupportedException e) when (AllowReelTransform && setup.BetLines.Count > 0)
            {
                // The rule-based line models do not cover these replacements: let the engine measure each reel instead.
                ruleError = e.Message;
                sc.Floating = FloatingModel.None(model, lineEval, sc.Mystery);
            }

            // ---- Ways (pattern) pays: counted with the lines ----
            var ways = new WaysEvaluator(layout, model, betPerLine, lines);
            if (ways.Active)
            {
                lineTimer.Start();
                ways.CheckReplacements(sc.Catalog.Entries.Where(e => !sc.Mystery.Handled.Contains(e)), sc.Catalog);
                sc.Ways = ways; report.Ways = ways.Description;
                foreach (var outcome in sc.Mystery.Outcomes) sc.WaysWin += outcome.Probability * ways.Expected(sc.Mystery, outcome);
                sc.LineWin += sc.WaysWin;
                lineTimer.Stop();
                Log($"ways: {ways.Description}");
            }

            // ---- Lines ----
            lineTimer.Start();
            double Evaluate(List<List<(double p, AGTSymbol s)>> dists)
            {
                string signature = string.Join(";", dists.Select(d => string.Join(",", d.Select(x => x.s.Name.CString + ":" + x.p.ToString("R")))));
                if (!lineCache.TryGetValue(signature, out double e)) lineCache[signature] = e = lineEval.ExpectedLineWin(dists);
                return e;
            }
            string? linesCache = CachePath is null ? null : CachePath + $".lines{sc.Index}";
            if (ruleError is not null && linesCache is not null && File.Exists(linesCache))
            {
                sc.LineWin += double.Parse(File.ReadAllText(linesCache).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                sc.LinesCached = true;
                report.Floating = "reel-transform model (line win from the cache)";
                Log("lines: taken from the cache");
            }
            else if (ruleError is not null)
            {
                Log("lines: rule-based models do not apply (" + ruleError + "); measuring reels with the engine");
                var busy = new HashSet<string>(sc.Catalog.Entries.SelectMany(e => e.Inputs), StringComparer.Ordinal);
                var transform = new ReelTransformLines(host, setup, lineEval, busy) { Progress = Log };
                transform.Calibrate();
                sc.Transform = transform;
                double transformWin = transform.ExpectedLineWin(Evaluate);
                sc.LineWin += transformWin;
                if (linesCache is not null) File.WriteAllText(linesCache, transformWin.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                report.OracleActs += transform.Plays;
                report.Floating = "reel-transform model: " + transform.Description + $", {transform.Checks} reel-locality checks";
            }
            else if (!sc.Floating.Active)
            {
                foreach (int line in setup.BetLines)
                {
                    var cells = setup.LineCells[line];
                    foreach (var outcome in sc.Mystery.Outcomes)
                    {
                        var dists = cells.Select(c => LineEvaluator.CellDistribution(model.Reels[c.reel], c.reel, c.window, sc.Mystery, outcome)).ToList();
                        sc.LineWin += outcome.Probability * Evaluate(dists);
                    }
                }
            }
            else
            {
                // Condition on the driver reels: they decide the mystery outcome and which floating replacements fire.
                Log($"floating: {sc.Floating.Description}; asking the engine which replacements fire");
                sc.Floating.Progress = Log;
                sc.Floating.Calibrate(dat, betPerLine, lines, setup, OraclePathLimit, (seed, suppress) => NewHost(seed, suppress));
                Log($"floating: oracle done ({sc.Floating.OracleActs:N0} engine acts); computing lines");
                report.OracleActs += sc.Floating.OracleActs;
                var floatCache = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var (weight, stops, key) in sc.Floating.DriverConfigurations())
                {
                    var outcome = sc.Mystery.SourceReels.Count > 0 ? sc.Mystery.OutcomeFor(model, stops) : sc.Mystery.Outcomes[0];
                    foreach (var (pf, mask) in sc.Floating.Firing(key))
                        foreach (int line in setup.BetLines)
                        {
                            var cells = setup.LineCells[line];
                            var dists = cells.Select(c => LineEvaluator.CellDistribution(model.Reels[c.reel], c.reel, c.window, sc.Mystery, outcome)).ToList();
                            string fkey = mask + "#" + string.Join(";", cells) + "#" + string.Join(";", dists.Select(d => string.Join(",", d.Select(x => x.s.Name.CString + ":" + x.p.ToString("R")))));
                            if (!floatCache.TryGetValue(fkey, out double e))
                                floatCache[fkey] = e = sc.Floating.ExpectedLineWin(cells, dists, mask, Evaluate) - sc.Floating.Correction(cells, outcome, mask, Evaluate);
                            sc.LineWin += weight * pf * e;
                        }
                }
            }
            lineTimer.Stop();

            // ---- Everything else ----
            Log("lines done; non-line part");
            otherTimer.Start();
            if (SkipNonLine) sc.NonLineError = "skipped (--lines-only)";
            else
            {
                try { ComputeNonLine(sc, host, report, preferCount: true); }
                catch (NotSupportedException e) { sc.NonLineError = e.Message; }
            }
            Log("non-line done");
            otherTimer.Stop();
        }
        report.DistinctLineModels = lineCache.Count;
        report.TuplesVisited = lineEval.TuplesVisited;
        report.PayEvaluations = lineEval.PayEvaluations;
        report.LineTime = lineTimer.Elapsed;
        report.ScatterTime = otherTimer.Elapsed;
        Combine(report, scenarios);

        if (verify)
        {
            Log("verifying against the engine");
            if (!report.LinesOnly) CheckFeatureEntryDetection(report, scenarios);
            Verify(report, host, scenarios, lineEval);
            // A count model that disagrees with the engine is replaced by the general evaluator.
            if (report.ScatterMismatches > 0 && scenarios.Any(s => s.Count is not null) && !report.LinesOnly)
            {
                report.Notes.Add("scatter-count model disagreed with the engine; recomputed with the general evaluator");
                otherTimer.Start();
                foreach (var sc in scenarios)
                {
                    try { ComputeNonLine(sc, host, report, preferCount: false); sc.NonLineError = null; }
                    catch (NotSupportedException e) { sc.NonLineError = e.Message; }
                }
                otherTimer.Stop();
                report.ScatterTime = otherTimer.Elapsed;
                Combine(report, scenarios);
                report.VerifiedLines = report.LineMismatches = report.VerifiedScatter = report.ScatterMismatches = 0;
                report.MismatchExamples.Clear();
                CheckTriggerRate(report, scenarios);
                Verify(report, host, scenarios, lineEval);
            }
        }
        return report;
    }

    /// <summary>
    /// Each enabled ReelGroupSet enables one and only one of its groups per spin. Its weights must cover the
    /// whole draw range: a draw past the total weight enables nothing, so the previous spin's reels stay
    /// active and spins are no longer independent.
    /// </summary>
    private static void CheckReelGroupSets(AGTLayout layout, Report report)
    {
        var parts = new List<string>();
        foreach (var entry in layout.ReelGroupSets)
        {
            var set = entry.Value;
            if (!set.Enabled || set.ReelGroups.Count <= 1) continue;
            ulong sum = 0; foreach (var g in set.ReelGroups) sum += g.Value;
            if (sum != set.ProbabilityRange)
                throw new NotSupportedException($"ReelGroupSet '{entry.Key}' weights sum to {sum} but it draws from {set.ProbabilityRange}: " +
                    "a draw past the total keeps the previous spin's reel groups, so spins are not independent.");
            parts.Add($"{entry.Key}: {set.ReelGroups.Count} groups");
        }
        report.ReelGroupSets = parts.Count == 0 ? "none" : string.Join("; ", parts);
    }

    private static void Combine(Report report, List<Scenario> scenarios)
    {
        report.ExpectedLineWin = scenarios.Sum(s => s.Weight * s.LineWin);
        report.ExpectedWaysWin = scenarios.Sum(s => s.Weight * s.WaysWin);
        report.NonLineError = scenarios.Select(s => s.NonLineError).FirstOrDefault(e => e is not null);
        report.ExpectedNonLineWin = report.LinesOnly ? double.NaN : scenarios.Sum(s => s.Weight * s.NonLineWin);
        report.TriggerProbability = report.LinesOnly ? double.NaN : scenarios.Sum(s => s.Weight * s.Trigger);
        report.NonLineMethod = report.LinesOnly ? "not supported" : string.Join(", ", scenarios.Select(s => s.Count is not null ? "scatter-count" : s.Classes is not null ? "class-count" : "general").Distinct());
    }

    private void ComputeNonLine(Scenario sc, GameHost host, Report report, bool preferCount)
    {
        var setup = sc.Setup;
        sc.Count = null; sc.General = null; sc.Classes = null;
        // Only cells/symbols that can influence non-line credits or feature entry matter; replacements that
        // cannot write them are switched off in a dedicated engine instance (results unchanged, far fewer draws).
        var layout = host.Game.Layouts.Entry(host.BaseLayoutIndex).Value;
        sc.Relevance ??= NonLineRelevance.Build(host.Game, layout, host.LayoutName(host.BaseLayoutIndex), setup.Model, sc.Catalog, sc.Ways?.PatternConditions);
        report.NonLineRelevance = sc.Relevance.Description;
        var nonLineHost = sc.NonLineHost ??= LoadNonLineHost(layout, sc.Relevance);
        if (preferCount && setup.ScatterSymbols.Count > 0 && StartState is null)
        {
            try
            {
                Log("trying the scatter-count model");
                var count = new ScatterEvaluator(setup.Model, setup.ScatterSymbols, nonLineHost, OraclePathLimit, setup.PrepareValues, setup.PrepareRanges);
                count.Calibrate();
                sc.Count = count;
                sc.NonLineWin = count.ExpectedNonLineWin();
                sc.Trigger = count.TriggerProbability();
                report.T = count.T; report.CountDistribution = count.CountDistribution(); report.Prizes = count.Prize;
                report.OracleActs += count.OracleActs; report.OracleConfigurations += count.OracleConfigurations;
                foreach (var n in count.Notes) if (!report.Notes.Contains(n)) report.Notes.Add(n);
                return;
            }
            catch (NotSupportedException e) { if (!report.Notes.Contains("scatter-count model not used: " + e.Message)) report.Notes.Add("scatter-count model not used: " + e.Message); sc.Count = null; Log("scatter-count model not used"); }
        }
        var model = setup.Model; var relevance = sc.Relevance;
        if (StartState is not null)
        {
            // Feature state: the general evaluator, recording the next state of every act. Within a reel the window
            // order is tried first as irrelevant (fewer groups), checked by playing other stops of each group.
            string? error = null;
            foreach (bool sorted in new[] { true, false })
            {
                try
                {
                    Func<int, uint, string> key = sorted
                        ? (r, p) => string.Join("|", relevance.Key(r, model.Reels[r].WindowSymbols[p]).Split('|').OrderBy(x => x, StringComparer.Ordinal))
                        : (r, p) => relevance.Key(r, model.Reels[r].WindowSymbols[p]);
                    var g = new SpecialSymbolEvaluator(model, key, nonLineHost, setup.PrepareValues, setup.PrepareRanges, OraclePathLimit, Math.Max(ConfigurationLimit, 100_000))
                    {
                        Progress = Log, Threads = Threads, HostFactory = () => LoadNonLineHost(layout, relevance),
                        CachePath = CachePath is null ? null : CachePath + (sorted ? ".sorted" : ".general")
                    };
                    // Extra metrics: credits per pay-condition family, then the next-state indicator.
                    var families = new List<string>(); var familyOf = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var pc in layout.PayConditions)
                    {
                        if (!(pc.Value.Scatter || pc.Value.Located || pc.Value.CombinedWin)) continue;
                        string f = System.Text.RegularExpressions.Regex.Replace(pc.Key, "_[0-9]+$", "");
                        int fi = families.IndexOf(f); if (fi < 0) { fi = families.Count; families.Add(f); }
                        familyOf[pc.Key] = fi;
                    }
                    int bi = nonLineHost.BaseLayoutIndex;
                    g.Extra = h =>
                    {
                        var states = Tracker?.Indicator(h) ?? Array.Empty<double>();
                        var v = new double[families.Count + states.Length];
                        var lay = h.Game.Layouts.Entry(bi).Value;
                        foreach (var list in new[] { lay.WinningLocatedPayconditions, lay.WinningScatteredPayconditions, lay.CombinedWinPayconditions })
                            foreach (var w in list) if (familyOf.TryGetValue(w.PayconditionName.CString, out int fi)) v[fi] += w.WonCredits;
                        Array.Copy(states, 0, v, families.Count, states.Length);
                        return v;
                    };
                    Log($"state-mode general evaluator ({(sorted ? "window order ignored" : "window order kept")}): {g.Configurations:N0} engine configurations");
                    g.Run(groupChecks: sorted ? 40 : 0);
                    sc.General = g;
                    sc.NonLineWin = g.ExpectedWin; sc.Trigger = g.TriggerProbability;
                    for (int i = 0; i < families.Count; i++)
                        if (g.ExpectedExtra[i] != 0) report.NonLineBreakdown[families[i]] = (report.NonLineBreakdown.TryGetValue(families[i], out var b) ? b : 0) + sc.Weight * g.ExpectedExtra[i];
                    for (int i = families.Count; i < g.ExpectedExtra.Length; i++)
                        if (g.ExpectedExtra[i] != 0) report.NextStates[i - families.Count] = (report.NextStates.TryGetValue(i - families.Count, out var v) ? v : 0) + sc.Weight * g.ExpectedExtra[i];
                    report.OracleActs += g.Acts; report.OracleConfigurations += g.Configurations;
                    return;
                }
                catch (NotSupportedException e) { error = (error is null ? "" : error + " / ") + e.Message; Log("  not used: " + e.Message); }
            }
            throw new NotSupportedException(error);
        }
        // Two evaluators: the general one (every relevant-symbol arrangement) and the class-count one (counts on
        // interchangeable reels). The cheaper one that applies is used.
        SpecialSymbolEvaluator? general = null; string? generalError = null;
        try
        {
            general = new SpecialSymbolEvaluator(model, (r, p) => relevance.Key(r, model.Reels[r].WindowSymbols[p]), nonLineHost,
                setup.PrepareValues, setup.PrepareRanges, OraclePathLimit, ConfigurationLimit);
        }
        catch (NotSupportedException e) { generalError = e.Message; }
        ClassCountEvaluator? classes = null; string? classError = null;
        try { classes = new ClassCountEvaluator(model, relevance, nonLineHost, setup.PrepareValues, setup.PrepareRanges, Math.Min(OraclePathLimit, ClassPathLimit), ClassConfigurationLimit); }
        catch (NotSupportedException e) { classError = e.Message; }
        if (classes is not null && (general is null || classes.Configurations < general.Configurations))
        {
            try
            {
                Log("class-count model: " + classes.Description + $"; {classes.Configurations:N0} engine configurations");
                classes.Progress = Log;
                classes.Threads = Threads; classes.CachePath = CachePath;
                classes.HostFactory = () => LoadNonLineHost(layout, relevance);
                classes.Run();
                sc.Classes = classes;
                sc.NonLineWin = classes.ExpectedWin;
                sc.Trigger = classes.TriggerProbability;
                report.OracleActs += classes.Acts; report.OracleConfigurations += classes.Configurations;
                foreach (var (f, v) in classes.Breakdown)
                    report.NonLineBreakdown[f] = (report.NonLineBreakdown.TryGetValue(f, out var old) ? old : 0) + sc.Weight * v;
                string note = $"class-count model: {classes.Description}; {classes.InvarianceChecks} arrangement checks passed; " +
                              $"in-spin picks: {classes.FactorSummary}";
                if (!report.Notes.Contains(note)) report.Notes.Add(note);
                report.T = Array.Empty<double>(); report.CountDistribution = Array.Empty<double>(); report.Prizes = new Dictionary<(int, int), double>();
                return;
            }
            catch (NotSupportedException e) { sc.Classes = null; classError = e.Message; }
        }
        if (general is null) throw new NotSupportedException(generalError + " Class-count model: " + classError);
        Log($"general evaluator: {general.Configurations:N0} engine configurations ({sc.Relevance!.Description})");
        general.Run();
        sc.General = general;
        sc.NonLineWin = general.ExpectedWin;
        sc.Trigger = general.TriggerProbability;
        report.OracleActs += general.Acts; report.OracleConfigurations += general.Configurations;
        report.T = Array.Empty<double>(); report.CountDistribution = Array.Empty<double>(); report.Prizes = new Dictionary<(int, int), double>();
    }

    /// <summary>Engine instance for the non-line evaluators, with irrelevant replacements switched off.</summary>
    private GameHost LoadNonLineHost(AGTLayout layout, NonLineRelevance relevance)
    {
        GameHost h;
        try { h = NewHost(51, suppress: true); }
        catch (NotSupportedException) { h = NewHost(51); }
        var refs = relevance.Irrelevant.Select(e => new EngineSurgery.ReplacementRef(e.Info.Trigger,
            Array.IndexOf(layout.Replacements[e.Info.Trigger], e.Replacement), e.Info.Kind == "mapped")).ToList();
        EngineSurgery.SwitchOff(h, refs);
        return h;
    }

    /// <summary>
    /// Symbols whose position can matter outside line wins, and reels whose whole window matters.
    /// </summary>
    private static (HashSet<string> special, HashSet<int> fullReels) SpecialSymbols(AGTLayout layout, Scenario sc)
    {
        var model = sc.Setup.Model;
        var special = new HashSet<string>(sc.Setup.ScatterSymbols, StringComparer.Ordinal);
        foreach (var pc in layout.PayConditions)
        {
            var c = pc.Value;
            if (!(c.Scatter || c.Located || c.IsPatternPayC)) continue;
            for (uint i = 0; i < c.Symbols.NumSymbols(); i++)
            {
                var s = c.Symbols[i];
                if (s.Qualifier.IsSubstitute) continue;
                special.Add(s.Name.CString);
                if (!s.AliasName.IsEmpty) special.Add(s.AliasName.CString);
            }
        }
        var fullReels = new HashSet<int>(sc.Mystery.SourceReels);
        var lineReels = new HashSet<int>(sc.Setup.LineCells.Values.SelectMany(c => c).Select(c => c.reel));
        for (int r = 0; r < model.Reels.Count; r++)
            if (model.Reels[r].Coordinates.Length == 1 && !lineReels.Contains(r)) fullReels.Add(r); // hidden selector-style reels
        foreach (var e in sc.Catalog.Entries)
        {
            foreach (var i in e.Inputs) special.Add(i);
            if (e.Source is (int sr, int)) fullReels.Add(sr);
            if (e.AnyInput) foreach (var t in e.Targets) if (sc.Catalog.CanAct(e, t)) fullReels.Add(t.reel);
        }
        return (special, fullReels);
    }

    private List<Scenario> DiscoverScenarios(GameHost host, DrawPartition partition)
    {
        var result = new List<Scenario>();
        var values = new List<uint>(); var ranges = new List<uint>();
        bool first = true;
        while (true)
        {
            var setup = BaseSetup.Read(host, first ? new List<uint>() : values, first ? new List<uint>() : ranges);
            first = false;
            double weight = 1;
            for (int d = 0; d < setup.PrepareRanges.Count; d++)
                weight *= (double)(partition.NextCellStart(setup.PrepareRanges[d], setup.PrepareValues[d]) - setup.PrepareValues[d]) / setup.PrepareRanges[d];
            result.Add(new Scenario { Setup = setup, Weight = weight, Index = result.Count });
            if (result.Count > ScenarioLimit) throw new NotSupportedException($"More than {ScenarioLimit} spin-preparation scenarios (reel group choices).");
            int depth = setup.PrepareRanges.Count - 1;
            while (depth >= 0 && partition.NextCellStart(setup.PrepareRanges[depth], setup.PrepareValues[depth]) >= setup.PrepareRanges[depth]) depth--;
            if (depth < 0) break;
            values = setup.PrepareValues.Take(depth).ToList(); ranges = setup.PrepareRanges.Take(depth).ToList();
            values.Add(partition.NextCellStart(setup.PrepareRanges[depth], setup.PrepareValues[depth])); ranges.Add(setup.PrepareRanges[depth]);
        }
        if (StartState is not null) result = ExpandGlobalDraws(host, partition, result);
        return result;
    }

    /// <summary>
    /// Feature states: in-spin draws whose ranges do not depend on the reel stops (e.g. which columns become wild
    /// this spin) are made part of the scenario. Their outcomes are grouped by the screen they give on a fixed
    /// stop vector; a group is only used when random spins give identical results for its members.
    /// Without this every configuration would multiply them out.
    /// </summary>
    private List<Scenario> ExpandGlobalDraws(GameHost host, DrawPartition partition, List<Scenario> scenarios)
    {
        var result = new List<Scenario>();
        var rng = new System.Random(17);
        string Play(List<uint> values, List<uint> ranges, uint[] stops, bool randomTail, int seed, out List<uint> drawn)
        {
            host.EnsureBase(true);
            host.Agent.Reseed(seed);
            host.Agent.BeginScripted(values, ranges, values.Count, randomTail);
            host.BeginAccounting();
            host.Session.Prepare(betPerLine, lines);
            int played = (int)host.Game.CurrentLayoutIndex;
            host.Session.Complete(stops);
            var res = host.EndAccounting(played);
            drawn = new List<uint>(host.Agent.Ranges);
            var layout = host.Game.Layouts.Entry(host.BaseLayoutIndex).Value;
            return res.Credited[host.BaseLayoutIndex] + " " + host.TransitionSignature() + " " +
                   string.Join("|", scenarios[0].Setup.Model.Reels.SelectMany(r => r.Coordinates).Select(c => layout.SymbolAtCoordinate(c).Name.CString));
        }
        foreach (var sc in scenarios)
        {
            var model = sc.Setup.Model;
            int fixedCount = sc.Setup.PrepareValues.Count;
            List<uint>? common = null;
            for (int t = 0; t < 40; t++)
            {
                var stops = model.Reels.Select(r => r.Stops[rng.Next(r.Stops.Count)].stop).ToArray();
                Play(sc.Setup.PrepareValues, sc.Setup.PrepareRanges, stops, false, 0, out var drawn);
                var tail = drawn.Skip(fixedCount).ToList();
                if (common is null) common = tail;
                else { int n = 0; while (n < common.Count && n < tail.Count && common[n] == tail[n]) n++; common = common.Take(n).ToList(); }
            }
            int k = common!.Count;
            if (k == 0) { result.Add(sc); continue; }
            var baseline = model.Reels.Select(r => r.Stops[0].stop).ToArray();
            var leaves = new List<(List<uint> v, List<uint> r, double p)>();
            void Walk(List<uint> v, List<uint> r, double p)
            {
                if (v.Count == fixedCount + k) { leaves.Add((v, r, p)); if (leaves.Count > 20_000) throw new NotSupportedException("More than 20,000 outcomes of the stop-independent in-spin draws."); return; }
                Play(v, r, baseline, false, 0, out var drawn);
                uint range = drawn[v.Count];
                var cells = partition.Cells(range);
                for (int c = 0; c < cells.Length; c++)
                {
                    uint end = c + 1 < cells.Length ? cells[c + 1] : range;
                    Walk(new List<uint>(v) { cells[c] }, new List<uint>(r) { range }, p * (end - cells[c]) / range);
                }
            }
            Walk(new List<uint>(sc.Setup.PrepareValues), new List<uint>(sc.Setup.PrepareRanges), 1.0);
            var groups = new Dictionary<string, List<(List<uint> v, List<uint> r, double p)>>(StringComparer.Ordinal);
            foreach (var leaf in leaves)
            {
                string key = Play(leaf.v, leaf.r, baseline, false, 0, out _);
                if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<(List<uint>, List<uint>, double)>();
                list.Add(leaf);
            }
            var checkStops = Enumerable.Range(0, 6).Select(_ => model.Reels.Select(r => r.Stops[rng.Next(r.Stops.Count)].stop).ToArray()).ToList();
            foreach (var members in groups.Values)
            {
                // Members of a group must play alike: compare the first with each other one on random spins.
                var rep = members[0];
                var expected = checkStops.Select((st, i) => Play(rep.v, rep.r, st, true, 1000 + i, out _)).ToList();
                bool alike = members.Skip(1).Take(3).All(m => checkStops.Select((st, i) => Play(m.v, m.r, st, true, 1000 + i, out _)).SequenceEqual(expected));
                var parts = alike ? new[] { (rep.v, rep.r, members.Sum(m => m.p)) } : members.Select(m => (m.v, m.r, m.p)).ToArray();
                foreach (var (v, r, p) in parts)
                    result.Add(new Scenario { Setup = sc.Setup.WithFixedDraws(v, r), Weight = sc.Weight * p, Index = result.Count });
            }
            Log($"state scenarios: {k} stop-independent in-spin draw(s), {leaves.Count} outcome(s) in {groups.Count} group(s)");
        }
        if (result.Count > ScenarioLimit * 4) throw new NotSupportedException($"{result.Count} scenarios after the stop-independent in-spin draws; too many.");
        return result;
    }

    /// <summary>
    /// Feature entry is detected from trigger pay conditions on engines whose feature strategies are switched
    /// off. Check that against the real engine on random spins with identical draws.
    /// </summary>
    private void CheckFeatureEntryDetection(Report report, List<Scenario> scenarios)
    {
        var setup = scenarios[0].Setup;
        GameHost suppressed;
        if (StartState is not null) return; // feature states: transitions are measured directly
        try { suppressed = NewHost(61, suppress: true); }
        catch (NotSupportedException) { return; } // not used: triggers are read from the real engine
        var real = NewHost(61);
        var rng = new System.Random(VerifySeed + 1);
        int n = FeatureEntryChecks, disagree = 0, entries = 0;
        for (int i = 0; i < n; i++)
        {
            int seed = rng.Next();
            var stops = setup.Ranges.Select(r => (uint)rng.Next((int)r)).ToArray();
            bool[] results = new bool[2]; int k = 0;
            foreach (var h in new[] { real, suppressed })
            {
                h.EnsureBase(); h.Agent.Reseed(seed);
                h.Agent.BeginScripted(setup.PrepareValues, setup.PrepareRanges, setup.PrepareValues.Count, randomTail: true);
                var req = h.Session.Prepare(betPerLine, lines);
                if (req.Count != stops.Length) return; // scenario-dependent strips: skip this check
                h.Session.Complete(stops);
                results[k++] = h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : h.IsAwayFromBase;
            }
            if (results[0]) entries++;
            if (results[0] != results[1]) disagree++;
        }
        report.Notes.Add($"feature-entry detection checked on {n:N0} random spins: {entries} entries, {disagree} disagreements");
        observedEntries = entries; observedSpins = n;
        CheckTriggerRate(report, scenarios);
        if (disagree > 0)
        {
            foreach (var sc in scenarios) sc.NonLineError ??= $"feature-entry detection disagreed with the real engine on {disagree} of {n} spins";
            Combine(report, scenarios);
        }
    }

    private int observedEntries, observedSpins;

    /// <summary>
    /// The feature-entry check's random spins also give an independent estimate of the trigger rate (for the
    /// first preparation scenario); a model far outside it counts as a non-line mismatch.
    /// </summary>
    private void CheckTriggerRate(Report report, List<Scenario> scenarios)
    {
        var first = scenarios[0];
        if (observedSpins == 0 || first.NonLineError is not null || double.IsNaN(first.Trigger)) return;
        int n = observedSpins;
        double observed = (double)observedEntries / n, p = Math.Max(first.Trigger, 1.0 / n);
        double se = Math.Sqrt(p * (1 - p) / n);
        if (Math.Abs(observed - first.Trigger) > 4.5 * se + 1.0 / n)
        {
            report.ScatterMismatches++;
            report.MismatchExamples.Add($"trigger rate: model {first.Trigger:G6}, engine {observedEntries} in {n:N0} random spins ({observed:G4})");
        }
    }

    private GameHost LoadSampler(GameHost fallback)
    {
        try { return NewHost(41, suppress: true, lenient: true); }
        catch (NotSupportedException) { return fallback; }
    }

    private bool TrySuppress()
    {
        if (StartState is not null) return false;
        try { GameHost.Load(dat, betPerLine, lines, 3, suppressFeatureEntry: true); return true; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>
    /// Random scenario and reel stops: compare the model's prediction for those stops with the full engine
    /// (all in-spin draws enumerated exactly). Lines: one random bet line per sample.
    /// </summary>
    private void Verify(Report report, GameHost host, List<Scenario> scenarios, LineEvaluator lineEval)
    {
        var sw = Stopwatch.StartNew();
        var rng = new System.Random(VerifySeed);
        GameHost? sampler = null; bool samplerTriggerUnknown = false;
        // Exact per-check enumeration up to 2,000 in-spin branches; beyond that the check samples the draws.
        var enumerator = new ActEnumerator(host, new DrawPartition(host.Game)) { PathLimitPerAct = Math.Min(OraclePathLimit, 2_000) };
        int baseIndex = host.BaseLayoutIndex;
        bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));
        var scatterLike = new HashSet<string>(scenarios[0].Setup.ScatterSymbols, StringComparer.Ordinal);

        for (int i = 0; i < VerifySamples; i++)
        {
            // Pick a scenario by weight.
            double u = rng.NextDouble(), acc = 0; var sc = scenarios[scenarios.Count - 1];
            foreach (var s in scenarios) { acc += s.Weight; if (u < acc) { sc = s; break; } }
            var setup = sc.Setup; var model = setup.Model;
            enumerator.SetScenario(setup.PrepareValues, setup.PrepareRanges);

            var stops = setup.Ranges.Select(r => (uint)rng.Next((int)r)).ToArray();
            // Bias half the samples towards scatter-heavy stops so trigger/prize cases are exercised.
            if (i % 2 == 1 && scatterLike.Count > 0)
                for (int r = 0; r < stops.Length; r++)
                {
                    var with = model.Reels[r].Stops.Select(x => (int)x.stop).Where(p => model.Reels[r].Windows[p].Any(scatterLike.Contains)).ToList();
                    if (with.Count > 0 && rng.NextDouble() < 0.6) stops[r] = (uint)with[rng.Next(with.Count)];
                }
            int line = setup.BetLines.Count > 0 ? setup.BetLines[rng.Next(setup.BetLines.Count)] : -1;
            // Every third sample: put replacement inputs (e.g. mystery symbols) on the chosen line where possible,
            // so the replacement rules are exercised rather than only plain symbols.
            if (i % 3 == 2 && line >= 0)
            {
                var inputs = new HashSet<string>(sc.Catalog.Entries.Where(e => sc.Mystery.Handled.Contains(e) || sc.Floating.Handled.Contains(e)).SelectMany(e => e.Inputs), StringComparer.Ordinal);
                if (inputs.Count > 0)
                    foreach (var (reel, window) in setup.LineCells[line])
                    {
                        var with = model.Reels[reel].Stops.Select(x => (int)x.stop).Where(p => inputs.Contains(model.Reels[reel].Windows[p][window])).ToList();
                        if (with.Count > 0 && rng.NextDouble() < 0.7) stops[reel] = (uint)with[rng.Next(with.Count)];
                    }
            }

            // Engine: exact over all in-spin draws when feasible, otherwise sampled.
            double engineLine = 0, engineNonLine = 0, engineTrigger = 0, engineWays = 0;
            double seLine = 0, seNonLine = 0, seTrigger = 0, seWays = 0; bool sampled = false;
            try
            {
                enumerator.Enumerate(stops, (h, res, w) =>
                {
                    if (line >= 0) engineLine += w * h.LineWin(line);
                    engineWays += w * h.AllPatternWins();
                    engineNonLine += w * ((double)res.Credited[baseIndex] - h.AllLineWins());
                    bool t = h.EntryTriggered(res);
                    if (t) engineTrigger += w;
                });
            }
            catch (NotSupportedException)
            {
                // Too many in-spin branches (e.g. several floating placements): sample them instead.
                sampled = true; report.SampledChecks++;
                // Sample on an engine with feature entry switched off, so triggering spins need no restore.
                sampler ??= LoadSampler(host);
                int n = SampledDraws; double l1 = 0, l2 = 0, o1 = 0, o2 = 0, t1 = 0, a1 = 0, a2 = 0;
                for (int k = 0; k < n; k++)
                {
                    sampler.EnsureBase();
                    sampler.Agent.BeginScripted(setup.PrepareValues, setup.PrepareRanges, setup.PrepareValues.Count, randomTail: true);
                    sampler.BeginAccounting();
                    sampler.Session.Prepare(betPerLine, lines);
                    int played = (int)sampler.Game.CurrentLayoutIndex;
                    sampler.Session.Complete(stops);
                    var res = sampler.EndAccounting(played);
                    double lw = line >= 0 ? sampler.LineWin(line) : 0, ow = (double)res.Credited[baseIndex] - sampler.AllLineWins();
                    bool t = sampler.EntryTriggered(res);
                    double aw = sampler.AllPatternWins(); a1 += aw; a2 += aw * aw;
                    l1 += lw; l2 += lw * lw; o1 += ow; o2 += ow * ow; if (t) t1++;
                }
                if (!sampler.FeatureEntryDetectionComplete) samplerTriggerUnknown = true;
                engineLine = l1 / n; engineNonLine = o1 / n; engineTrigger = t1 / n; engineWays = a1 / n;
                seWays = Math.Sqrt(Math.Max(a2 / n - engineWays * engineWays, 0) / n);
                seLine = Math.Sqrt(Math.Max(l2 / n - engineLine * engineLine, 0) / n);
                seNonLine = Math.Sqrt(Math.Max(o2 / n - engineNonLine * engineNonLine, 0) / n);
                seTrigger = Math.Sqrt(Math.Max(engineTrigger * (1 - engineTrigger), 0) / n);
            }
            if (Verbose && (i < 5 || i % 10 == 0)) Log($"check {i + 1}/{VerifySamples}: {(sampled ? "sampled" : "exact")}");
            bool Agree(double engine, double model, double se) => sampled ? Math.Abs(engine - model) <= 4 * se + 1e-9 : Same(engine, model);

            if (line >= 0 && !sc.LinesCached)
            {
                var outcome = sc.Mystery.OutcomeFor(model, stops);
                var cells = setup.LineCells[line];
                var cellDists = cells.Select(c => sc.Mystery.Effective(c.reel, c.window, model.Reels[c.reel].WindowSymbols[stops[c.reel]][c.window], outcome)).ToList();
                double modelLine = 0;
                if (sc.Transform is not null)
                    modelLine = sc.Transform.PredictLine(line, stops, d => lineEval.ExpectedLineWin(d));
                else if (sc.Floating.Active)
                    foreach (var (pf, mask) in sc.Floating.FiringFor(stops))
                        modelLine += pf * sc.Floating.ExpectedLineWin(cells, cellDists, mask, d => lineEval.ExpectedLineWin(d), sc.Floating.ScreenEligible(stops, mask, outcome));
                else modelLine = lineEval.ExpectedLineWin(cellDists);
                var tuple = cellDists.Select(d => d.Count == 1 ? d[0].s : new AGTSymbol()).ToArray();
                report.VerifiedLines++;
                if (!Agree(engineLine, modelLine, seLine))
                {
                    report.LineMismatches++;
                    if (report.MismatchExamples.Count < 5)
                        report.MismatchExamples.Add($"line {line}: [{string.Join(" ", tuple.Select(s => s.Name.IsEmpty ? "?" : s.Name.CString))}] engine {engineLine}{(sampled ? $" +/- {seLine:G3}" : "")} model {modelLine}");
                }
            }

            if (sc.Ways is not null)
            {
                double modelWays = sc.Ways.Predict(stops, sc.Mystery, sc.Mystery.OutcomeFor(model, stops));
                report.VerifiedLines++;
                if (!Agree(engineWays, modelWays, seWays))
                {
                    report.LineMismatches++;
                    if (report.MismatchExamples.Count < 5)
                        report.MismatchExamples.Add($"ways stops [{string.Join(",", stops)}]: engine {engineWays}{(sampled ? $" +/- {seWays:G3}" : "")} model {modelWays}");
                }
            }

            if (sc.NonLineError is null && !(sampled && samplerTriggerUnknown))
            {
                var (modelWin, modelTrigger) = sc.Predict(stops);
                report.VerifiedScatter++;
                if (!Agree(engineNonLine, modelWin, seNonLine) || !Agree(engineTrigger, modelTrigger, seTrigger))
                {
                    report.ScatterMismatches++;
                    if (report.MismatchExamples.Count < 5)
                        report.MismatchExamples.Add($"non-line stops [{string.Join(",", stops)}]: engine win {engineNonLine} trig {engineTrigger}, model win {modelWin} trig {modelTrigger}");
                }
            }
        }
        report.VerifyTime = sw.Elapsed;
    }
}
