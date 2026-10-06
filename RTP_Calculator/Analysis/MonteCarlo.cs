using System.Diagnostics;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Analysis;

/// <summary>
/// Emulator-style simulation (same engine, random draws), reported per layout like the compiler's
/// statistics view, with a standard error so exact results can be checked against it.
/// </summary>
public sealed class MonteCarlo
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;

    public MonteCarlo(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    public int Threads { get; set; } = Environment.ProcessorCount;
    public int Seed { get; set; } = 12345;

    public sealed class Report
    {
        public long PaidGames { get; set; }
        public long Acts { get; set; }
        public long Triggers { get; set; }
        public ulong TotalBet { get; set; }
        public string[] LayoutNames { get; set; } = Array.Empty<string>();
        public double[] LayoutCredits { get; set; } = Array.Empty<double>();
        public double SumGameWin { get; set; }
        public double SumGameWinSquared { get; set; }
        public double SumBaseWin { get; set; }
        public double SumBaseWinSquared { get; set; }
        public TimeSpan Elapsed { get; set; }
        public double Cost => (double)PaidGames * TotalBet;
        public double Rtp => SumGameWin / Cost;
        public double RtpStdError
        {
            get
            {
                double n = PaidGames, mean = SumGameWin / n;
                double variance = SumGameWinSquared / n - mean * mean;
                return Math.Sqrt(Math.Max(variance, 0) / n) / TotalBet;
            }
        }
        public double BaseStdError
        {
            get
            {
                double n = PaidGames, mean = SumBaseWin / n;
                double variance = SumBaseWinSquared / n - mean * mean;
                return Math.Sqrt(Math.Max(variance, 0) / n) / TotalBet;
            }
        }
    }

    public Report Run(long paidGames)
    {
        var report = new Report();
        var gate = new object();
        long perThread = paidGames / Threads, remainder = paidGames % Threads;
        var sw = Stopwatch.StartNew();
        Exception? failure = null;
        var threads = Enumerable.Range(0, Threads).Select(t => new Thread(() =>
        {
            try
            {
                var host = GameHost.Load(dat, betPerLine, lines, Seed + 7919 * t);
                int n = host.LayoutCount, baseIndex = host.BaseLayoutIndex;
                var credits = new double[n];
                long games = perThread + (t < remainder ? 1 : 0), acts = 0, triggers = 0;
                double s1 = 0, s2 = 0, b1 = 0, b2 = 0;
                for (long g = 0; g < games; g++)
                {
                    double gameWin = 0;
                    var first = host.PlayRandomAct(); acts++;
                    for (int i = 0; i < n; i++) { credits[i] += first.Credited[i]; gameWin += first.Credited[i]; }
                    double baseWin = first.Credited[baseIndex];
                    if (host.IsAwayFromBase) triggers++;
                    int guard = 0;
                    while (host.IsAwayFromBase)
                    {
                        var r = host.PlayRandomAct(); acts++;
                        for (int i = 0; i < n; i++) { credits[i] += r.Credited[i]; gameWin += r.Credited[i]; }
                        if (++guard > 1_000_000) throw new InvalidOperationException("Feature did not terminate.");
                    }
                    s1 += gameWin; s2 += gameWin * gameWin; b1 += baseWin; b2 += baseWin * baseWin;
                }
                lock (gate)
                {
                    if (report.LayoutCredits.Length == 0)
                    {
                        report.LayoutCredits = new double[n];
                        report.LayoutNames = Enumerable.Range(0, n).Select(host.LayoutName).ToArray();
                        report.TotalBet = host.TotalBet;
                    }
                    for (int i = 0; i < n; i++) report.LayoutCredits[i] += credits[i];
                    report.PaidGames += games; report.Acts += acts; report.Triggers += triggers;
                    report.SumGameWin += s1; report.SumGameWinSquared += s2; report.SumBaseWin += b1; report.SumBaseWinSquared += b2;
                }
            }
            catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
        }) { IsBackground = true }).ToList();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();
        if (failure is not null) throw new InvalidOperationException("Simulation failed: " + failure.Message, failure);
        report.Elapsed = sw.Elapsed;
        return report;
    }
}
