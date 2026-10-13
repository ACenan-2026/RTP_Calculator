using System.Diagnostics;
using System.Globalization;
using RTP_Calculator.Analysis;
using RTP_Calculator.Analysis.Exact;
using RTP_Calculator.Util;

namespace RTP_Calculator
{
    internal static class Program
    {
        private const string Usage = @"
RTP_Calculator - exact RTP derivation using the AGT win engine (proof of concept)

Usage:
  RTP_Calculator <command> <game.dat | game.agm> [options]

Commands:
  coverage     Run the construct scan and the analytic calculation over every .agm/.dat in a FOLDER
               (given in place of the game file). Options: --scan-only, --out file.csv,
               --filter text, --range from:count, --samples N (default 100), --lines N,
               --threads N, --cache folder (class-count measurements kept per game, so reruns resume).
  analytic     Base-game RTP from the reel design (seconds): line pays via the engine's matcher,
               scatter trigger / prizes via a few exact engine evaluations, then verification of
               the model against the full engine on random reel stops (--samples, default 200).
               Coin / prize games use the class-count model (--threads N engines in parallel;
               --cache file keeps its engine measurements so an interrupted run resumes).
  exact-base   Exact base-game RTP: line wins, non-line wins (scatter / trigger prizes) and
               feature trigger probability. Uses the real engine as the pay evaluator.
  feature      Expected value of a feature (--layout name) as a Markov chain over engine states: each state's
               spin computed exactly, sub-features entered from it reported separately (--cache file resumes).
  simulate     Emulator-style Monte Carlo, reported per layout, with standard error.
  profile      Random-draw profile per layout: how big a brute-force enumeration of one act is.
  selftest     Empirical checks of the assumptions used by exact-base.
  all          selftest, profile, exact-base, then simulate.

Options:
  --lines N          Lines bet (default: all paylines defined in the base layout)
  --bet N            Credits bet per line (default 1)
  --threads N        Worker threads (default: all cores)
  --spins N          Paid games for simulate (default 2,000,000) / profile (default 20,000)
  --samples N        Samples per self-test check (default 200)
  --always-restore   Restore the engine snapshot before every enumerated path (slower, fewer assumptions)
  --per-line         Compute every bet line separately instead of using line symmetry
  --lines-only       analytic: compute line wins only (skip the non-line part)
  --verify-seed N    analytic: seed for the verification screens (default 4242)
  --ref-lines X      Reference value for base line RTP (e.g. from the PAR sheet), for comparison
  --ref-nonline X    Reference value for base non-line RTP
  --ref-hit X        Reference feature hit rate (1 in X)
  --only lines|nonline  exact-base: compute only one part
  --shard k/n        exact-base: compute only shard k of n (0-based). Each shard prints PARTIAL lines;
                     add the raw values over all shards to get the full result (see README).
  --subshard j/m     Split each shard further: every m-th reel-stop combination, starting at j.

A .agm input is compiled to a temporary .dat with the AGT compiler; the .agm is never modified.
";

        private static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            if (args.Length < 2 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Usage); return args.Length == 0 ? 0 : 1; }
            try
            {
                var options = Options.Parse(args.Skip(2));
                string command = args[0].ToLowerInvariant();
                if (command == "coverage") return RunCoverage(args[1], options);
                byte[] dat = GameFiles.Load(args[1]);
                uint available = GameFiles.BaseLayoutPaylines(dat);
                if (!options.LinesSpecified) options.UseDefaultLines(Math.Max(1, available));
                else if (available > 0 && options.Lines > available)
                    throw new ArgumentException($"--lines {options.Lines} is more than the {available} paylines defined in the base layout.");
                Console.WriteLine($"Game file : {Path.GetFileName(args[1])}");
                Console.WriteLine($"Bet       : {options.Bet} x {options.Lines} lines, threads {options.Threads}");
                Console.WriteLine();
                switch (command)
                {
                    case "exact-base": RunExact(dat, options); break;
                    case "analytic": return RunAnalytic(dat, options) ? 0 : 3;
                    case "feature": RunFeature(dat, options); break;
                    case "simulate": RunSimulate(dat, options); break;
                    case "profile": RunProfile(dat, options); break;
                    case "selftest": return RunSelfTest(dat, options) ? 0 : 3;
                    case "all":
                        bool ok = RunSelfTest(dat, options);
                        RunProfile(dat, options);
                        RunExact(dat, options);
                        RunSimulate(dat, options);
                        return ok ? 0 : 3;
                    default: Console.Error.WriteLine($"Unknown command '{args[0]}'."); Console.WriteLine(Usage); return 1;
                }
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("ERROR: " + e.Message);
                if (e.InnerException is not null) Console.Error.WriteLine("  " + e.InnerException.Message);
                return 2;
            }
        }

        private static int RunCoverage(string folder, Options o)
        {
            if (!Directory.Exists(folder)) throw new DirectoryNotFoundException($"Folder not found: {folder}");
            Console.WriteLine($"=== Coverage: {folder} ===");
            var runner = new Analysis.Coverage.CoverageRunner
            {
                VerifySamples = o.SamplesSpecified ? o.Samples : 100,
                ScanOnly = o.ScanOnly, Bet = o.Bet, Lines = o.LinesSpecified ? o.Lines : (uint?)null,
                Filter = o.Filter, Range = o.Range, Threads = o.Threads, CacheDir = o.Cache
            };
            if (o.Cache is not null) Directory.CreateDirectory(o.Cache);
            string output = o.Out ?? Path.Combine(Directory.GetCurrentDirectory(), "coverage.csv");
            runner.CsvPath = output;
            var rows = runner.Run(folder);
            Analysis.Coverage.CoverageRunner.WriteCsv(output, rows);
            Analysis.Coverage.CoverageRunner.PrintSummary(rows);
            Console.WriteLine($"  Written: {output}");
            return 0;
        }

        private static void RunExact(byte[] dat, Options o)
        {
            Console.WriteLine("=== Exact base game ===");
            var sw = Stopwatch.StartNew();
            var exact = new BaseGameExact(dat, o.Bet, o.Lines)
            {
                Threads = o.Threads, AlwaysRestore = o.AlwaysRestore, ForcePerLine = o.PerLine,
                SkipLines = o.Only == "nonline", SkipNonLine = o.Only == "lines",
                ShardIndex = o.ShardIndex, ShardCount = o.ShardCount,
                SubShardIndex = o.SubShardIndex, SubShardCount = o.SubShardCount
            };
            var r = exact.Run();
            if (o.ShardCount > 1 || o.SubShardCount > 1)
            {
                // Machine-readable partial sums; probabilities and wins are additive across shards.
                if (!double.IsNaN(r.LineRawTotal))
                    Console.WriteLine($"PARTIAL lines shard={o.ShardIndex}/{o.ShardCount} sub={o.SubShardIndex}/{o.SubShardCount} raw={r.LineRawTotal:R} mass={r.LineMass:R} multiplier={r.LineMultiplier} totalbet={r.TotalBet}");
                if (!double.IsNaN(r.ExpectedNonLineWinPerGame))
                    Console.WriteLine($"PARTIAL nonline shard={o.ShardIndex}/{o.ShardCount} sub={o.SubShardIndex}/{o.SubShardCount} win={r.ExpectedNonLineWinPerGame:R} trigger={r.TriggerProbability:R} mass={r.NonLineMass:R} totalbet={r.TotalBet}");
                return;
            }
            Console.WriteLine();
            Console.WriteLine($"  Game                 : {r.GameName}   (base layout '{r.BaseLayout}', total bet {r.TotalBet})");
            Console.WriteLine($"  Reel-stop cycle      : {r.StopCycle}");
            Console.WriteLine($"  Scatter symbols      : {string.Join(", ", r.ScatterSymbols)}");
            Console.WriteLine($"  Line symmetry        : {(r.LinesSymmetric ? "yes" : "no")} - {r.LineSymmetryNote}; lines computed {r.LinesComputed} of {r.BetLines}");
            Console.WriteLine($"  Probability coverage : lines {r.LineMass:R}, non-line {r.NonLineMass:R}  (both must be 1)");
            foreach (var run in r.Runs)
                Console.WriteLine($"  Engine work          : {run.Combinations:N0} configurations, {run.Paths:N0} engine acts, max {run.MaxPathsPerAct:N0} branches/act, {run.Elapsed:hh\\:mm\\:ss}");
            foreach (var w in r.Runs.SelectMany(x => x.Warnings).Distinct()) Console.WriteLine("  note: " + w);
            Console.WriteLine();
            Console.WriteLine($"  Base line RTP        : {r.LineRtp:F12}{Compare(r.LineRtp, o.RefLines)}");
            Console.WriteLine($"  Base non-line RTP    : {r.NonLineRtp:F12}{Compare(r.NonLineRtp, o.RefNonLine)}   (scatter / trigger prizes credited in base)");
            Console.WriteLine($"  Base RTP (total)     : {r.BaseRtp:F12}");
            Console.WriteLine($"  Trigger detected by  : {r.TriggerMethod}");
            Console.WriteLine($"  Feature trigger      : p = {r.TriggerProbability:F12}  =  1 in {1 / r.TriggerProbability:F6}{Compare(1 / r.TriggerProbability, o.RefHit)}");
            Console.WriteLine($"  Elapsed              : {sw.Elapsed:hh\\:mm\\:ss}");
            Console.WriteLine();
        }

        private static string Compare(double value, double? reference) =>
            reference is double x ? $"   reference {x:F12}  diff {value - x:+0.0e+00;-0.0e+00;0}" : "";

        private static void RunFeature(byte[] dat, Options o)
        {
            if (o.Layout is null) throw new ArgumentException("feature: give the feature's layout with --layout name.");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            void Log(string m) => Console.WriteLine($"    [{clock.Elapsed:mm\\:ss}] {m}");
            var chain = new Analysis.Feature.FeatureChain(dat, o.Bet, o.Lines)
            {
                CachePath = o.Cache, Threads = o.Threads, VerifySamples = o.SamplesSpecified ? o.Samples : 10, Log = Log
            };
            Console.WriteLine($"=== Feature '{o.Layout}' as a Markov chain over engine states ===");
            var entries = chain.FindEntries(o.Layout);
            int sampled = entries.Sum(e => e.count);
            double mixWin = 0, mixSpins = 0, mixVar = 0; double bet = 0;
            var values = new List<(double f, double win)>();
            foreach (var (entry, signature, count) in entries)
            {
                var r = chain.Run(entry);
                bet = r.TotalBet;
                double f = (double)count / sampled;
                mixWin += f * r.ExpectedWin; mixSpins += f * r.ExpectedSpins; values.Add((f, r.ExpectedWin));
                Console.WriteLine();
                if (entries.Count > 1) Console.WriteLine($"  Entry state          : {signature}  ({count} of {sampled} sampled entries)");
                Console.WriteLine($"  States (kernels)     : {r.Kernels}");
                Console.WriteLine($"  Expected spins       : {r.ExpectedSpins:F10}");
                Console.WriteLine($"  Expected win         : {r.ExpectedWin:F6} credits = {r.ExpectedWin / r.TotalBet:F10} x total bet");
                Console.WriteLine($"      lines            : {r.LineWin / r.TotalBet:F10} x total bet");
                Console.WriteLine($"      non-line         : {r.NonLineWin / r.TotalBet:F10} x total bet");
                foreach (var (family, credits) in r.Breakdown.OrderByDescending(kv => kv.Value))
                    if (credits != 0) Console.WriteLine($"        {family,-26}: {credits / r.TotalBet:F10}");
                Console.WriteLine($"  Sub-feature entries  : {r.ExpectedExits:F10} per feature (their own value is not included)");
            }
            if (entries.Count > 1)
            {
                // The mix of entry states is sampled from random base games: its standard error from the multinomial counts.
                foreach (var (f, w) in values) mixVar += f * (w - mixWin) * (w - mixWin);
                double se = Math.Sqrt(mixVar / sampled);
                Console.WriteLine();
                Console.WriteLine($"  Mixed over entries   : {mixWin / bet:F6} x total bet per feature (+/- {se / bet:F6} from the sampled entry mix), {mixSpins:F4} spins");
                Console.WriteLine("  note: each entry state's value is exact; the mix of entry states is estimated from random base games.");
            }
            Console.WriteLine($"  Elapsed              : {clock.Elapsed.TotalSeconds:F1} s");
        }

        private static bool RunAnalytic(byte[] dat, Options o)
        {
            Console.WriteLine("=== Analytic base game (reel design) ===");
            var sw = Stopwatch.StartNew();
            var r = new Analysis.Analytic.AnalyticBaseGame(dat, o.Bet, o.Lines) { VerifySamples = o.Samples, SkipNonLine = o.LinesOnly, Verbose = true, VerifySeed = o.VerifySeed, Threads = o.Threads, CachePath = o.Cache }.Run(verify: o.Samples > 0);
            Console.WriteLine($"  Mystery construct    : {r.Mystery}");
            Console.WriteLine($"  Reel group sets      : {r.ReelGroupSets}");
            Console.WriteLine($"  Spin scenarios       : {r.Scenarios} (outcomes of the draws made while preparing the spin)");
            Console.WriteLine($"  Floating (lines)     : {r.Floating}");
            if (r.Ways != "none") Console.WriteLine($"  Ways                 : {r.Ways}");
            if (r.NonLineRelevance.Length > 0) Console.WriteLine($"  Non-line relevance   : {r.NonLineRelevance}");
            Console.WriteLine($"  Non-line method      : {r.NonLineMethod}{(r.LinesOnly ? " - " + r.NonLineError : "")}");
            Console.WriteLine($"  Lines                : {r.BetLines} bet lines, {r.LinePayConditions} line pay conditions, {r.DistinctLineModels} distinct line models");
            Console.WriteLine($"                         {r.TuplesVisited:N0} symbol tuples, {r.PayEvaluations:N0} engine pay evaluations, {r.LineTime.TotalSeconds:F2} s");
            Console.WriteLine($"  Scatter oracle       : {r.OracleConfigurations} configurations, {r.OracleActs:N0} engine acts, {r.ScatterTime.TotalSeconds:F2} s");
            Console.Write("  Scatter count dist.  :");
            for (int k = 0; k < r.CountDistribution.Length; k++) if (r.CountDistribution[k] > 0) Console.Write($" {k}:{r.CountDistribution[k]:G6}");
            Console.WriteLine();
            Console.WriteLine($"  Trigger T(k)         : {string.Join(" ", r.T.Select((t, k) => $"{k}:{t:0.##}"))}");
            foreach (var g in r.Prizes.GroupBy(p => p.Key.reel).OrderBy(g => g.Key))
                Console.WriteLine($"  Prize per scatter    : {r.ReelNames[g.Key]}: " + string.Join("  ", g.OrderBy(p => p.Key.window).Select(p => $"row {p.Key.window} = {p.Value:G8}")));
            foreach (var n in r.Notes) Console.WriteLine("  note: " + n);
            Console.WriteLine();
            Console.WriteLine($"  Base line RTP        : {r.LineRtp:F12}{Compare(r.LineRtp, o.RefLines)}");
            if (r.Ways != "none") Console.WriteLine($"      of which ways    : {r.ExpectedWaysWin / r.TotalBet:F12}");
            Console.WriteLine($"  Base non-line RTP    : {r.NonLineRtp:F12}{Compare(r.NonLineRtp, o.RefNonLine)}");
            foreach (var (family, credits) in r.NonLineBreakdown.OrderByDescending(kv => kv.Value))
                Console.WriteLine($"      {family,-28}: {credits / r.TotalBet:F12}");
            Console.WriteLine($"  Base RTP (total)     : {r.LineRtp + r.NonLineRtp:F12}");
            Console.WriteLine($"  Feature trigger      : p = {r.TriggerProbability:F12}  =  1 in {1 / r.TriggerProbability:F6}{Compare(1 / r.TriggerProbability, o.RefHit)}");
            bool ok = true;
            if (r.VerifiedLines > 0)
            {
                ok = r.LineMismatches == 0 && r.ScatterMismatches == 0;
                Console.WriteLine();
                if (r.SampledChecks > 0) Console.WriteLine($"  ({r.SampledChecks} checks sampled the engine's in-spin draws instead of enumerating them; tolerance 4 s.e.)");
                Console.WriteLine($"  Verification vs full engine ({r.VerifyTime.TotalSeconds:F1} s): " +
                                  $"lines {r.VerifiedLines - r.LineMismatches}/{r.VerifiedLines} agree, scatter/trigger {r.VerifiedScatter - r.ScatterMismatches}/{r.VerifiedScatter} agree " +
                                  (ok ? "- PASS" : "- FAIL"));
                foreach (var m in r.MismatchExamples) Console.WriteLine("    " + m);
            }
            Console.WriteLine($"  Elapsed              : {sw.Elapsed.TotalSeconds:F1} s");
            Console.WriteLine();
            return ok;
        }

        private static void RunSimulate(byte[] dat, Options o)
        {
            long spins = o.Spins ?? 2_000_000;
            Console.WriteLine($"=== Simulation ({spins:N0} paid games) ===");
            var r = new MonteCarlo(dat, o.Bet, o.Lines) { Threads = o.Threads, Seed = 12345 + o.VerifySeed - 4242 }.Run(spins);
            for (int i = 0; i < r.LayoutNames.Length; i++)
                Console.WriteLine($"  {r.LayoutNames[i],-20} RTP {r.LayoutCredits[i] / r.Cost:F6}" + (i == 0 ? $"  +/- {r.BaseStdError:F6} (1 s.e.)" : ""));
            Console.WriteLine($"  {"Total",-20} RTP {r.Rtp:F6}  +/- {r.RtpStdError:F6} (1 s.e.)");
            Console.WriteLine($"  Base, paid spin only RTP {r.SumBaseWin / r.Cost:F6}  +/- {r.BaseStdError:F6}   (base-layout credits from later acts: {r.BaseCreditsAfterPaidSpin / r.Cost:F6})");
            Console.WriteLine($"  Feature hit rate     1 in {(double)r.PaidGames / Math.Max(1, r.Triggers):F3}  ({r.Triggers:N0} triggers)");
            Console.WriteLine($"  {r.Acts:N0} acts in {r.Elapsed:hh\\:mm\\:ss} ({r.Acts / Math.Max(1e-9, r.Elapsed.TotalSeconds):N0} acts/s)");
            Console.WriteLine();
        }

        private static void RunProfile(byte[] dat, Options o)
        {
            long spins = o.Spins is long s && s < 200_000 ? s : 20_000;
            Console.WriteLine($"=== Draw profile ({spins:N0} paid games) ===");
            var profiles = new DrawProfiler(dat, o.Bet, o.Lines).Run(spins);
            Console.WriteLine($"  {"layout",-16}{"acts",10}{"draws/act",11}{"max",6}{"log10 paths/act (avg / max)",32}");
            foreach (var p in profiles)
                Console.WriteLine($"  {p.Layout,-16}{p.Acts,10:N0}{p.SumDraws / p.Acts,11:F1}{p.MaxDraws,6}{(p.SumLog10Paths / p.Acts).ToString("F1"),20} / {p.MaxLog10Paths:F1}");
            Console.WriteLine("  (paths = reel-stop combinations x weighted-table cells of every in-act draw; ~1e6 is enumerable directly)");
            foreach (var p in profiles)
            {
                var top = p.RangeCounts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{kv.Key}x{(double)kv.Value / p.Acts:0.#}");
                Console.WriteLine($"  {p.Layout}: in-act draw ranges (per act) {string.Join(", ", top)}");
            }
            Console.WriteLine();
        }

        private static bool RunSelfTest(byte[] dat, Options o)
        {
            Console.WriteLine("=== Self-test ===");
            bool ok = new SelfTest(dat, o.Bet, o.Lines) { Samples = o.Samples }.Run();
            Console.WriteLine(ok ? "  All checks passed." : "  One or more checks FAILED.");
            Console.WriteLine();
            return ok;
        }
    }
}
