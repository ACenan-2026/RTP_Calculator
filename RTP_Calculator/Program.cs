using System.Diagnostics;
using System.Globalization;
using RTP_Calculator.Analysis;
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
  exact-base   Exact base-game RTP: line wins, non-line wins (scatter / trigger prizes) and
               feature trigger probability. Uses the real engine as the pay evaluator.
  simulate     Emulator-style Monte Carlo, reported per layout, with standard error.
  profile      Random-draw profile per layout: how big a brute-force enumeration of one act is.
  selftest     Empirical checks of the assumptions used by exact-base.
  all          selftest, profile, exact-base, then simulate.

Options:
  --lines N          Lines bet (default 50)
  --bet N            Credits bet per line (default 1)
  --threads N        Worker threads (default: all cores)
  --spins N          Paid games for simulate (default 2,000,000) / profile (default 20,000)
  --samples N        Samples per self-test check (default 200)
  --always-restore   Restore the engine snapshot before every enumerated path (slower, fewer assumptions)
  --per-line         Compute every bet line separately instead of using line symmetry
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
                byte[] dat = LoadDat(args[1]);
                Console.WriteLine($"Game file : {Path.GetFileName(args[1])}");
                Console.WriteLine($"Bet       : {options.Bet} x {options.Lines} lines, threads {options.Threads}");
                Console.WriteLine();
                switch (command)
                {
                    case "exact-base": RunExact(dat, options); break;
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

        private static byte[] LoadDat(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Game file not found.", path);
            if (!path.EndsWith(".agm", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
            string output = Path.Combine(Path.GetTempPath(), "RTP_Calculator_" + Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N") + ".dat");
            Console.WriteLine($"Compiling {Path.GetFileName(path)} with the AGT compiler...");
            new AgtCompiler.Core.Compilation.AgmCompiler().CompileToDat(path, output, "RTP_Calculator");
            try { return File.ReadAllBytes(output); }
            finally { try { File.Delete(output); } catch (IOException) { } }
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

        private static void RunSimulate(byte[] dat, Options o)
        {
            long spins = o.Spins ?? 2_000_000;
            Console.WriteLine($"=== Simulation ({spins:N0} paid games) ===");
            var r = new MonteCarlo(dat, o.Bet, o.Lines) { Threads = o.Threads }.Run(spins);
            for (int i = 0; i < r.LayoutNames.Length; i++)
                Console.WriteLine($"  {r.LayoutNames[i],-20} RTP {r.LayoutCredits[i] / r.Cost:F6}" + (i == 0 ? $"  +/- {r.BaseStdError:F6} (1 s.e.)" : ""));
            Console.WriteLine($"  {"Total",-20} RTP {r.Rtp:F6}  +/- {r.RtpStdError:F6} (1 s.e.)");
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
