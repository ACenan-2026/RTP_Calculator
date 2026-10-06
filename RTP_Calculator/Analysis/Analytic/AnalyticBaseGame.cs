using System.Diagnostics;
using AgtEngine.Core.Graphic;
using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis.Analytic;

/// <summary>
/// Base-game RTP from the reel design rather than from reel stops.
///
/// Recognised constructs, each with its own evaluator:
///   * coordinate-based mystery replacement  -> <see cref="MysteryModel"/>
///   * line pays (Win statements)            -> <see cref="LineEvaluator"/>
///   * scatter count trigger / scatter pays / per-cell prizes -> <see cref="ScatterEvaluator"/>
/// Any other replacement must be shown not to touch line symbols, otherwise the run stops with
/// "not supported" (the slower engine-driven exact-base command still works for such games).
///
/// The engine stays the judge of game rules: it decides line pays (its matcher), and supplies the
/// scatter oracle values. Verification replays random reel stops through the full engine and
/// compares them with the model's predictions for those stops.
/// </summary>
public sealed class AnalyticBaseGame
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;

    public AnalyticBaseGame(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    public int VerifySamples { get; set; } = 300;

    public sealed class Report
    {
        public ulong TotalBet { get; set; }
        public string Mystery { get; set; } = "";
        public int LinePayConditions { get; set; }
        public int BetLines { get; set; }
        public int DistinctLineModels { get; set; }
        public long TuplesVisited { get; set; }
        public long PayEvaluations { get; set; }
        public double ExpectedLineWin { get; set; }
        public double ExpectedNonLineWin { get; set; }
        public double TriggerProbability { get; set; }
        public double[] T { get; set; } = Array.Empty<double>();
        public double[] CountDistribution { get; set; } = Array.Empty<double>();
        public Dictionary<(int reel, int window), double> Prizes { get; set; } = new Dictionary<(int, int), double>();
        public string[] ReelNames { get; set; } = Array.Empty<string>();
        public long OracleActs { get; set; }
        public int OracleConfigurations { get; set; }
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

    public Report Run(bool verify)
    {
        var report = new Report();
        var host = GameHost.Load(dat, betPerLine, lines, 21, suppressFeatureEntry: TrySuppress());
        var setup = BaseSetup.Read(host);
        var model = setup.Model;
        var layout = host.Game.CurrentLayout;
        report.TotalBet = host.TotalBet;
        report.ReelNames = model.Reels.Select(r => r.Name).ToArray();
        report.BetLines = setup.BetLines.Count;

        // ---- Constructs ---------------------------------------------------------------------------
        var mystery = MysteryModel.Read(layout, model);
        report.Mystery = mystery.Description;
        var lineEval = new LineEvaluator(layout, betPerLine, lines);
        report.LinePayConditions = lineEval.LinePayConditions;
        CheckOtherReplacements(layout, mystery, lineEval, setup.ScatterSymbols);
        if (mystery.Outcomes.Any(o => o.Symbol is not null && setup.ScatterSymbols.Contains(o.Symbol.Name.CString)))
            throw new NotSupportedException("The mystery symbol can become a scatter; the scatter evaluator does not handle that yet.");

        // ---- Lines --------------------------------------------------------------------------------
        var sw = Stopwatch.StartNew();
        var cache = new Dictionary<string, double>(StringComparer.Ordinal);
        double expectedLines = 0;
        foreach (int line in setup.BetLines)
        {
            var cells = setup.LineCells[line];
            foreach (var outcome in mystery.Outcomes)
            {
                var dists = cells.Select(c => LineEvaluator.CellDistribution(model.Reels[c.reel], c.reel, c.window, mystery, outcome)).ToList();
                // Lines with identical cell distributions have identical expectations: compute once.
                string signature = string.Join(";", dists.Select(d => string.Join(",", d.Select(x => x.s.Name.CString + ":" + x.p.ToString("R")))));
                if (!cache.TryGetValue(signature, out double e)) cache[signature] = e = lineEval.ExpectedLineWin(dists);
                expectedLines += outcome.Probability * e;
            }
        }
        report.LineTime = sw.Elapsed;
        report.DistinctLineModels = cache.Count;
        report.TuplesVisited = lineEval.TuplesVisited;
        report.PayEvaluations = lineEval.PayEvaluations;
        report.ExpectedLineWin = expectedLines;

        // ---- Scatter, trigger and prizes ----------------------------------------------------------
        sw.Restart();
        var scatter = new ScatterEvaluator(model, setup.ScatterSymbols, host);
        scatter.Calibrate();
        report.T = scatter.T;
        report.CountDistribution = scatter.CountDistribution();
        report.Prizes = scatter.Prize;
        report.TriggerProbability = scatter.TriggerProbability();
        report.ExpectedNonLineWin = scatter.ExpectedNonLineWin();
        report.OracleActs = scatter.OracleActs;
        report.OracleConfigurations = scatter.OracleConfigurations;
        report.ScatterTime = sw.Elapsed;
        report.Notes.AddRange(scatter.Notes);

        if (verify) Verify(report, host, setup, mystery, lineEval, scatter);
        return report;
    }

    private bool TrySuppress()
    {
        try { GameHost.Load(dat, betPerLine, lines, 3, suppressFeatureEntry: true); return true; }
        catch (NotSupportedException) { return false; }
    }

    /// <summary>Every non-mystery replacement must not involve symbols that can take part in a line win.</summary>
    private static void CheckOtherReplacements(AgtEngine.Core.ReelGame.AGTLayout layout, MysteryModel mystery, LineEvaluator lineEval, HashSet<string> scatter)
    {
        foreach (var r in mystery.OtherReplacements)
        {
            foreach (var name in new[] { r.Original.CString, r.Replacement.CString })
            {
                if (name.Length == 0 || !layout.SymbolSets.Contains(name)) continue;
                var set = layout.SymbolSets[name];
                for (uint i = 0; i < set.NumSymbols(); i++)
                {
                    AGTSymbol s = set[i];
                    if (lineEval.TouchesLines(s))
                        throw new NotSupportedException($"Replacement set '{name}' contains '{s.Name.CString}', which can take part in line wins; not supported analytically.");
                }
            }
        }
    }

    /// <summary>
    /// Random reel stops: compare the model's prediction for those stops with the full engine
    /// (all in-act draws enumerated exactly). Lines: one random bet line per sample.
    /// </summary>
    private void Verify(Report report, GameHost host, BaseSetup setup, MysteryModel mystery, LineEvaluator lineEval, ScatterEvaluator scatter)
    {
        var sw = Stopwatch.StartNew();
        var rng = new System.Random(4242);
        var model = setup.Model;
        var enumerator = new ActEnumerator(host, new DrawPartition(host.Game));
        int baseIndex = host.BaseLayoutIndex;
        bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));

        for (int i = 0; i < VerifySamples; i++)
        {
            var stops = setup.Ranges.Select(r => (uint)rng.Next((int)r)).ToArray();
            // Bias half the samples towards scatter-heavy stops so trigger/prize cases are exercised.
            if (i % 2 == 1)
                for (int r = 0; r < stops.Length; r++)
                {
                    var withScatter = Enumerable.Range(0, (int)model.Reels[r].Range).Where(p => scatter.MaskOf(model.Reels[r].Windows[p]) != 0).ToList();
                    if (withScatter.Count > 0 && rng.NextDouble() < 0.6) stops[r] = (uint)withScatter[rng.Next(withScatter.Count)];
                }
            int line = setup.BetLines[rng.Next(setup.BetLines.Count)];

            double engineLine = 0, engineNonLine = 0, engineTrigger = 0;
            enumerator.Enumerate(stops, (h, res, w) =>
            {
                engineLine += w * h.LineWin(line);
                engineNonLine += w * ((double)res.Credited[baseIndex] - h.AllLineWins());
                bool t = h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : res.LeftBase;
                if (t) engineTrigger += w;
            });

            // Model: line pay for the symbols on this line, with the mystery outcome these stops select.
            MysteryModel.Outcome outcome = mystery.Outcomes[0];
            if (mystery.SelectorReel >= 0)
            {
                string chosen = model.Reels[mystery.SelectorReel].Windows[stops[mystery.SelectorReel]][mystery.SelectorWindow];
                outcome = mystery.Outcomes.First(o => o.Symbol!.Name.CString == chosen);
            }
            var tuple = setup.LineCells[line].Select(c => mystery.Effective(c.reel, model.Reels[c.reel].WindowSymbols[stops[c.reel]][c.window], outcome)).ToArray();
            double modelLine = lineEval.Pay(tuple);
            report.VerifiedLines++;
            if (!Same(engineLine, modelLine))
            {
                report.LineMismatches++;
                if (report.MismatchExamples.Count < 5)
                    report.MismatchExamples.Add($"line {line}: [{string.Join(" ", tuple.Select(s => s.Name.CString))}] engine {engineLine} model {modelLine}");
            }

            var (modelWin, modelTrigger) = scatter.Predict(stops);
            report.VerifiedScatter++;
            if (!Same(engineNonLine, modelWin) || !Same(engineTrigger, modelTrigger))
            {
                report.ScatterMismatches++;
                if (report.MismatchExamples.Count < 5)
                    report.MismatchExamples.Add($"scatter stops [{string.Join(",", stops)}]: engine win {engineNonLine} trig {engineTrigger}, model win {modelWin} trig {modelTrigger}");
            }
        }
        report.VerifyTime = sw.Elapsed;
    }
}
