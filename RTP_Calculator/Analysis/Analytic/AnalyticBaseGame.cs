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
    public int ScenarioLimit { get; set; } = 64;
    /// <summary>Compute line wins only (the non-line part is reported as skipped).</summary>
    public bool SkipNonLine { get; set; }
    /// <summary>Print progress of each stage.</summary>
    public bool Verbose { get; set; }
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private void Log(string message) { if (Verbose) Console.WriteLine($"    [{clock.Elapsed:mm\\:ss}] {message}"); }
    /// <summary>Random in-spin draws per check when exact enumeration of a spin is too large.</summary>
    public int SampledDraws { get; set; } = 2000;

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
        public double ExpectedNonLineWin { get; set; }
        public double TriggerProbability { get; set; }
        public string NonLineMethod { get; set; } = "";
        public string Floating { get; set; } = "none";
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
        public FloatingModel Floating = null!;
        public string? NonLineError;
        public double LineWin, NonLineWin, Trigger;
        public (double win, double trigger) Predict(uint[] stops) => Count is not null ? Count.Predict(stops) : General!.Predict(stops);
    }

    public Report Run(bool verify)
    {
        var report = new Report();
        bool suppress = TrySuppress();
        var host = GameHost.Load(dat, betPerLine, lines, 21, suppressFeatureEntry: suppress);
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
            sc.Floating = FloatingModel.Build(layout, model, sc.Catalog, lineEval, sc.Mystery, lineCells);
            if (sc.Floating.Active) report.Floating = sc.Floating.Description;
            var blocking = sc.Catalog.Entries.Where(e => e.ActsOnLines && !sc.Mystery.Handled.Contains(e) && !sc.Floating.Handled.Contains(e)).ToList();
            if (blocking.Count > 0)
                throw new NotSupportedException($"Replacement {blocking[0]} can change symbols on paylines" +
                                                (blocking.Count > 1 ? $" (+{blocking.Count - 1} more)" : "") + "; not supported analytically yet.");

            // ---- Lines ----
            lineTimer.Start();
            double Evaluate(List<List<(double p, AGTSymbol s)>> dists)
            {
                string signature = string.Join(";", dists.Select(d => string.Join(",", d.Select(x => x.s.Name.CString + ":" + x.p.ToString("R")))));
                if (!lineCache.TryGetValue(signature, out double e)) lineCache[signature] = e = lineEval.ExpectedLineWin(dists);
                return e;
            }
            if (!sc.Floating.Active)
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
                sc.Floating.Calibrate(dat, betPerLine, lines, setup, OraclePathLimit);
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
        report.NonLineError = scenarios.Select(s => s.NonLineError).FirstOrDefault(e => e is not null);
        report.ExpectedNonLineWin = report.LinesOnly ? double.NaN : scenarios.Sum(s => s.Weight * s.NonLineWin);
        report.TriggerProbability = report.LinesOnly ? double.NaN : scenarios.Sum(s => s.Weight * s.Trigger);
        report.NonLineMethod = report.LinesOnly ? "not supported" : string.Join(", ", scenarios.Select(s => s.Count is not null ? "scatter-count" : "general").Distinct());
    }

    private void ComputeNonLine(Scenario sc, GameHost host, Report report, bool preferCount)
    {
        var setup = sc.Setup;
        sc.Count = null; sc.General = null;
        if (preferCount && setup.ScatterSymbols.Count > 0)
        {
            try
            {
                var count = new ScatterEvaluator(setup.Model, setup.ScatterSymbols, host, OraclePathLimit, setup.PrepareValues, setup.PrepareRanges);
                count.Calibrate();
                sc.Count = count;
                sc.NonLineWin = count.ExpectedNonLineWin();
                sc.Trigger = count.TriggerProbability();
                report.T = count.T; report.CountDistribution = count.CountDistribution(); report.Prizes = count.Prize;
                report.OracleActs += count.OracleActs; report.OracleConfigurations += count.OracleConfigurations;
                foreach (var n in count.Notes) if (!report.Notes.Contains(n)) report.Notes.Add(n);
                return;
            }
            catch (NotSupportedException e) { report.Notes.Add("scatter-count model not used: " + e.Message); sc.Count = null; }
        }
        var (special, fullReels) = SpecialSymbols(host.Game.Layouts.Entry(host.BaseLayoutIndex).Value, sc);
        var general = new SpecialSymbolEvaluator(setup.Model, special, fullReels, host, setup.PrepareValues, setup.PrepareRanges, OraclePathLimit, ConfigurationLimit);
        general.Run();
        sc.General = general;
        sc.NonLineWin = general.ExpectedWin;
        sc.Trigger = general.TriggerProbability;
        report.OracleActs += general.Acts; report.OracleConfigurations += general.Configurations;
        report.T = Array.Empty<double>(); report.CountDistribution = Array.Empty<double>(); report.Prizes = new Dictionary<(int, int), double>();
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
            result.Add(new Scenario { Setup = setup, Weight = weight });
            if (result.Count > ScenarioLimit) throw new NotSupportedException($"More than {ScenarioLimit} spin-preparation scenarios (reel group choices).");
            int depth = setup.PrepareRanges.Count - 1;
            while (depth >= 0 && partition.NextCellStart(setup.PrepareRanges[depth], setup.PrepareValues[depth]) >= setup.PrepareRanges[depth]) depth--;
            if (depth < 0) break;
            values = setup.PrepareValues.Take(depth).ToList(); ranges = setup.PrepareRanges.Take(depth).ToList();
            values.Add(partition.NextCellStart(setup.PrepareRanges[depth], setup.PrepareValues[depth])); ranges.Add(setup.PrepareRanges[depth]);
        }
        return result;
    }

    private GameHost LoadSampler(GameHost fallback)
    {
        try { return GameHost.Load(dat, betPerLine, lines, 41, suppressFeatureEntry: true, lenient: true); }
        catch (NotSupportedException) { return fallback; }
    }

    private bool TrySuppress()
    {
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
                    var with = Enumerable.Range(0, (int)model.Reels[r].Range).Where(p => model.Reels[r].Windows[p].Any(scatterLike.Contains)).ToList();
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
                        var with = Enumerable.Range(0, (int)model.Reels[reel].Range).Where(p => inputs.Contains(model.Reels[reel].Windows[p][window])).ToList();
                        if (with.Count > 0 && rng.NextDouble() < 0.7) stops[reel] = (uint)with[rng.Next(with.Count)];
                    }
            }

            // Engine: exact over all in-spin draws when feasible, otherwise sampled.
            double engineLine = 0, engineNonLine = 0, engineTrigger = 0;
            double seLine = 0, seNonLine = 0, seTrigger = 0; bool sampled = false;
            try
            {
                enumerator.Enumerate(stops, (h, res, w) =>
                {
                    if (line >= 0) engineLine += w * h.LineWin(line);
                    engineNonLine += w * ((double)res.Credited[baseIndex] - h.AllLineWins());
                    bool t = h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : res.LeftBase;
                    if (t) engineTrigger += w;
                });
            }
            catch (NotSupportedException)
            {
                // Too many in-spin branches (e.g. several floating placements): sample them instead.
                sampled = true; report.SampledChecks++;
                // Sample on an engine with feature entry switched off, so triggering spins need no restore.
                sampler ??= LoadSampler(host);
                int n = SampledDraws; double l1 = 0, l2 = 0, o1 = 0, o2 = 0, t1 = 0;
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
                    bool t = sampler.FeatureEntrySuppressed ? sampler.FeatureEntryTriggered() : res.LeftBase;
                    l1 += lw; l2 += lw * lw; o1 += ow; o2 += ow * ow; if (t) t1++;
                }
                if (!sampler.FeatureEntryDetectionComplete) samplerTriggerUnknown = true;
                engineLine = l1 / n; engineNonLine = o1 / n; engineTrigger = t1 / n;
                seLine = Math.Sqrt(Math.Max(l2 / n - engineLine * engineLine, 0) / n);
                seNonLine = Math.Sqrt(Math.Max(o2 / n - engineNonLine * engineNonLine, 0) / n);
                seTrigger = Math.Sqrt(Math.Max(engineTrigger * (1 - engineTrigger), 0) / n);
            }
            if (Verbose && (i < 5 || i % 10 == 0)) Log($"check {i + 1}/{VerifySamples}: {(sampled ? "sampled" : "exact")}");
            bool Agree(double engine, double model, double se) => sampled ? Math.Abs(engine - model) <= 4 * se + 1e-9 : Same(engine, model);

            if (line >= 0)
            {
                var outcome = sc.Mystery.OutcomeFor(model, stops);
                var cells = setup.LineCells[line];
                var cellDists = cells.Select(c => sc.Mystery.Effective(c.reel, c.window, model.Reels[c.reel].WindowSymbols[stops[c.reel]][c.window], outcome)).ToList();
                double modelLine = 0;
                if (sc.Floating.Active)
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
