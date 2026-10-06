using System.Globalization;

namespace RTP_Calculator.Util;

public sealed class Options
{
    /// <summary>Lines bet. 0 until set: then the number of paylines in the starting (base) layout is used.</summary>
    public uint Lines { get; private set; }
    public bool LinesSpecified => Lines > 0;
    public void UseDefaultLines(uint lines) { if (Lines == 0) Lines = lines; }
    public uint Bet { get; private set; } = 1;
    public int Threads { get; private set; } = Environment.ProcessorCount;
    public long? Spins { get; private set; }
    public int Samples { get; private set; } = 200;
    public bool AlwaysRestore { get; private set; }
    public bool PerLine { get; private set; }
    public double? RefLines { get; private set; }
    public double? RefNonLine { get; private set; }
    public double? RefHit { get; private set; }
    public int ShardIndex { get; private set; }
    public int ShardCount { get; private set; } = 1;
    public string Only { get; private set; } = "";
    public int SubShardIndex { get; private set; }
    public int SubShardCount { get; private set; } = 1;

    public static Options Parse(IEnumerable<string> args)
    {
        var o = new Options();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            string Next() => i + 1 < list.Count ? list[++i] : throw new ArgumentException($"Option {a} needs a value.");
            double D() => double.Parse(Next(), CultureInfo.InvariantCulture);
            switch (a.ToLowerInvariant())
            {
                case "--lines": o.Lines = uint.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--bet": o.Bet = uint.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--threads": o.Threads = Math.Max(1, int.Parse(Next(), CultureInfo.InvariantCulture)); break;
                case "--spins": o.Spins = long.Parse(Next().Replace("_", "").Replace(",", ""), CultureInfo.InvariantCulture); break;
                case "--samples": o.Samples = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--always-restore": o.AlwaysRestore = true; break;
                case "--per-line": o.PerLine = true; break;
                case "--ref-lines": o.RefLines = D(); break;
                case "--ref-nonline": o.RefNonLine = D(); break;
                case "--ref-hit": o.RefHit = D(); break;
                case "--shard":
                    var parts = Next().Split('/');
                    o.ShardIndex = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    o.ShardCount = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    if (o.ShardCount < 1 || o.ShardIndex < 0 || o.ShardIndex >= o.ShardCount) throw new ArgumentException("--shard expects k/n with 0 <= k < n.");
                    break;
                case "--subshard":
                    var sub = Next().Split('/');
                    o.SubShardIndex = int.Parse(sub[0], CultureInfo.InvariantCulture);
                    o.SubShardCount = int.Parse(sub[1], CultureInfo.InvariantCulture);
                    if (o.SubShardCount < 1 || o.SubShardIndex < 0 || o.SubShardIndex >= o.SubShardCount) throw new ArgumentException("--subshard expects j/m with 0 <= j < m.");
                    break;
                case "--only":
                    o.Only = Next().ToLowerInvariant();
                    if (o.Only != "lines" && o.Only != "nonline") throw new ArgumentException("--only expects 'lines' or 'nonline'.");
                    break;
                default: throw new ArgumentException($"Unknown option '{a}'.");
            }
        }
        return o;
    }
}
