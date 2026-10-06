using RTP_Calculator.Engine;
using RTP_Calculator.Enumeration;

namespace RTP_Calculator.Analysis;

/// <summary>
/// Plays random games and records every random draw per act, per layout. Reports how large a
/// brute-force enumeration of a single act would be (log10 of the number of distinct paths after
/// collapsing weighted tables into cells). This is the feasibility map for the exact tool: it shows
/// which acts can be enumerated directly and which need a decomposition.
/// </summary>
public sealed class DrawProfiler
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;
    public DrawProfiler(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    public sealed class LayoutProfile
    {
        public string Layout { get; set; } = "";
        public long Acts { get; set; }
        public double SumDraws { get; set; }
        public int MaxDraws { get; set; }
        public double SumLog10Paths { get; set; }
        public double MaxLog10Paths { get; set; }
        public double SumLog10Requests { get; set; }
        public Dictionary<uint, long> RangeCounts { get; } = new Dictionary<uint, long>();
    }

    public List<LayoutProfile> Run(long paidGames, int seed = 99)
    {
        var host = GameHost.Load(dat, betPerLine, lines, seed);
        var partition = new DrawPartition(host.Game);
        var profiles = new Dictionary<string, LayoutProfile>(StringComparer.Ordinal);
        var ranges = new List<uint>();
        host.Agent.Observer = r => ranges.Add(r);
        for (long g = 0; g < paidGames; g++)
        {
            do
            {
                ranges.Clear();
                host.Agent.BeginRandom();
                host.BeginAccounting();
                var requests = host.Session.Prepare(host.BetPerLine, host.Lines);
                // Prepare is where the engine switches layout, so read the layout after it.
                string layout = host.Game.CurrentLayoutName;
                var values = requests.Select(r => host.Agent.Next(r.Range)).ToArray();
                // Host-side request draws do not go through the engine's RNG; count them separately.
                double logRequests = requests.Sum(r => r.Range > 0 ? Math.Log10(r.Range) : 0);
                host.Session.Complete(values);
                if (!profiles.TryGetValue(layout, out var p)) profiles[layout] = p = new LayoutProfile { Layout = layout };
                double logPaths = logRequests;
                foreach (var r in ranges)
                {
                    p.RangeCounts[r] = p.RangeCounts.TryGetValue(r, out long c) ? c + 1 : 1;
                    int cells;
                    try { cells = partition.Cells(r).Length; } catch (NotSupportedException) { cells = (int)Math.Min(r, int.MaxValue); }
                    logPaths += Math.Log10(cells);
                }
                int draws = ranges.Count + requests.Count;
                p.Acts++; p.SumDraws += draws; p.MaxDraws = Math.Max(p.MaxDraws, draws);
                p.SumLog10Paths += logPaths; p.MaxLog10Paths = Math.Max(p.MaxLog10Paths, logPaths);
                p.SumLog10Requests += logRequests;
            } while (host.IsAwayFromBase);
        }
        host.Agent.Observer = null;
        return profiles.Values.ToList();
    }
}
