using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis;

/// <summary>
/// Exact base-game RTP using the real engine as the pay evaluator.
///
/// Brute force over every reel stop is not practical (Laughing Dragon: 35*36*53*62*64*300 = 7.9e10
/// stop combinations, before the in-act replacement draws), so the base game is split into two
/// parts, each of which only depends on a small "sufficient statistic" of the reel stops:
///
///  1. LINE WINS. Expected total line win = sum over bet lines of the expected win on that line.
///     A line's win depends only on the symbols in its own cells (plus in-act draws), so each reel's
///     stops are grouped by the symbol(s) they put on that line. With unweighted reels and one cell
///     per reel per line, every bet line has the same distribution, so one line is computed and
///     multiplied by the number of bet lines (checked; otherwise each line is computed separately).
///
///  2. NON-LINE WINS AND FEATURE TRIGGER (scatters, located / combined wins, trigger prizes).
///     These depend only on where scatter symbols land, so stops are grouped by the positions of
///     scatter-condition symbols in each reel window.
///
/// Hidden reels (e.g. the mystery-symbol selector) are grouped by their full window. In both parts
/// every in-act random draw is enumerated exactly by <see cref="ActEnumerator"/>.
/// The grouping assumptions are checked empirically by the self-test command.
/// </summary>
public sealed class BaseGameExact
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;

    public BaseGameExact(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    public int Threads { get; set; } = Environment.ProcessorCount;
    public bool AlwaysRestore { get; set; }
    public bool ForcePerLine { get; set; }
    public bool SkipLines { get; set; }
    public bool SkipNonLine { get; set; }
    public int ShardIndex { get; set; }
    public int ShardCount { get; set; } = 1;
    public int SubShardIndex { get; set; }
    public int SubShardCount { get; set; } = 1;

    public sealed class Report
    {
        public string GameName { get; set; } = "";
        public string BaseLayout { get; set; } = "";
        public ulong TotalBet { get; set; }
        public int BetLines { get; set; }
        public string[] ScatterSymbols { get; set; } = Array.Empty<string>();
        public string StopCycle { get; set; } = "";
        public bool LinesSymmetric { get; set; }
        public string LineSymmetryNote { get; set; } = "";
        public int LinesComputed { get; set; }
        public double ExpectedLineWinPerGame { get; set; } = double.NaN;
        public double ExpectedNonLineWinPerGame { get; set; } = double.NaN;
        public double TriggerProbability { get; set; } = double.NaN;
        public double LineMass { get; set; } = double.NaN;
        /// <summary>Sum over the computed lines of the expected line win (before the symmetry multiplier).</summary>
        public double LineRawTotal { get; set; } = double.NaN;
        /// <summary>Multiplier applied to <see cref="LineRawTotal"/>: number of bet lines when symmetry is used, else 1.</summary>
        public int LineMultiplier { get; set; } = 1;
        public double NonLineMass { get; set; } = double.NaN;
        public string TriggerMethod { get; set; } = "";
        public List<GroupedProductEnumerator.Result> Runs { get; } = new List<GroupedProductEnumerator.Result>();
        public double LineRtp => ExpectedLineWinPerGame / TotalBet;
        public double NonLineRtp => ExpectedNonLineWinPerGame / TotalBet;
        public double BaseRtp => LineRtp + NonLineRtp;
    }

    public Report Run()
    {
        var host = GameHost.Load(dat, betPerLine, lines, 7);
        var setup = BaseSetup.Read(host);
        var report = new Report
        {
            GameName = host.Game.UserGameName.CString,
            BaseLayout = host.LayoutName(host.BaseLayoutIndex),
            TotalBet = host.TotalBet,
            BetLines = setup.BetLines.Count,
            ScatterSymbols = setup.ScatterSymbols.ToArray(),
            StopCycle = string.Join(" x ", setup.Ranges) + " = " + setup.Ranges.Aggregate(1.0, (a, r) => a * r).ToString("N0")
        };

        // Prefer engines with feature entry suppressed: a triggering base act then leaves no state behind,
        // so no snapshot restore (about 10 ms each) is needed after it. Entry is detected from the
        // trigger pay condition; self-test check G verifies this against the real transition.
        bool suppress = true;
        try { GameHost.Load(dat, betPerLine, lines, 3, suppressFeatureEntry: true); }
        catch (NotSupportedException e) { suppress = false; Console.WriteLine("  note: " + e.Message + " Falling back to restore mode."); }
        report.TriggerMethod = suppress ? "trigger pay condition (feature entry suppressed)" : "engine state transition (restore after each trigger)";

        // ---- Part 1: line wins ------------------------------------------------------------------
        if (!SkipLines && setup.BetLines.Count > 0)
        {
            report.LinesSymmetric = setup.CheckLineSymmetry(out string note);
            report.LineSymmetryNote = note;
            bool oneLine = report.LinesSymmetric && !ForcePerLine;
            var linesToCompute = oneLine ? new List<int> { setup.BetLines[0] } : setup.BetLines;
            report.LinesComputed = linesToCompute.Count;
            double expected = 0, mass = 0;
            foreach (int line in linesToCompute)
            {
                var groups = setup.LineGroups(line);
                Console.WriteLine($"  Line part, payline index {line}: stop groups per reel {string.Join(" x ", groups.Select(g => g.Count))}" +
                                  $" = {groups.Aggregate(1L, (a, g) => a * g.Count):N0} engine configurations");
                var run = new GroupedProductEnumerator(dat, betPerLine, lines) { Threads = Threads, AlwaysRestore = AlwaysRestore, Label = $"line {line}", ShardIndex = ShardIndex, ShardCount = ShardCount, SubShardIndex = SubShardIndex, SubShardCount = SubShardCount, SuppressFeatureEntry = suppress }
                    .Run(groups, setup.Ranges, 1, (h, res, w, acc) => acc[0] = w * h.LineWin(line));
                expected += run.Totals[0];
                mass += run.Mass;
                report.Runs.Add(run);
            }
            report.LineRawTotal = expected;
            report.LineMultiplier = oneLine ? setup.BetLines.Count : 1;
            report.ExpectedLineWinPerGame = expected * report.LineMultiplier;
            report.LineMass = mass / linesToCompute.Count;
        }

        // ---- Part 2: non-line wins and trigger ---------------------------------------------------
        if (!SkipNonLine)
        {
            var groups = setup.NonLineGroups();
            Console.WriteLine($"  Non-line part: stop groups per reel {string.Join(" x ", groups.Select(g => g.Count))}" +
                              $" = {groups.Aggregate(1L, (a, g) => a * g.Count):N0} engine configurations");
            int baseIndex = host.BaseLayoutIndex;
            var run = new GroupedProductEnumerator(dat, betPerLine, lines)
            {
                Threads = Threads, AlwaysRestore = AlwaysRestore, Label = "non-line", ShardIndex = ShardIndex, ShardCount = ShardCount, SubShardIndex = SubShardIndex, SubShardCount = SubShardCount,
                SuppressFeatureEntry = suppress
            }
                .Run(groups, setup.Ranges, 2, (h, res, w, acc) =>
                {
                    acc[0] = w * ((double)res.Credited[baseIndex] - h.AllLineWins());
                    bool triggered = h.FeatureEntrySuppressed ? h.FeatureEntryTriggered() : res.LeftBase;
                    acc[1] = triggered ? w : 0;
                });
            report.ExpectedNonLineWinPerGame = run.Totals[0];
            report.TriggerProbability = run.Totals[1];
            report.NonLineMass = run.Mass;
            report.Runs.Add(run);
        }
        return report;
    }
}
