using System.Diagnostics;
using RTP_Calculator.Analysis;
using RTP_Calculator.Engine;
using RTP_Calculator.Util;

namespace RTP_Calculator.Enumeration;

/// <summary>
/// Enumerates the Cartesian product of per-reel stop groups. For each combination the reels are set
/// to the groups' representative stops and the act is played through <see cref="ActEnumerator"/>,
/// which enumerates every internal draw. Each outcome is weighted by
///   prod_reels (group count / reel range)  x  internal path weight.
///
/// Work is split across threads, each with its own independent engine instance.
/// </summary>
public sealed class GroupedProductEnumerator
{
    public delegate void Measure(GameHost host, ActResult result, double weight, double[] accumulator);

    private readonly byte[] dat;
    private readonly uint betPerLine, lines;

    public GroupedProductEnumerator(byte[] dat, uint betPerLine, uint lines)
    {
        this.dat = dat; this.betPerLine = betPerLine; this.lines = lines;
    }

    public int Threads { get; set; } = Environment.ProcessorCount;
    public bool AlwaysRestore { get; set; }
    /// <summary>Load the worker engines with feature entry suppressed (see <see cref="GameHost.Load"/>).</summary>
    public bool SuppressFeatureEntry { get; set; }
    public string Label { get; set; } = "enumeration";
    public TimeSpan ProgressInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Run only part of the work: shard <see cref="ShardIndex"/> of <see cref="ShardCount"/>. Partial totals add up.</summary>
    public int ShardIndex { get; set; }
    public int ShardCount { get; set; } = 1;

    /// <summary>
    /// Split a shard further: within each selected work item, only every SubShardCount-th combination
    /// (in enumeration order), starting at <see cref="SubShardIndex"/>. Spreads expensive items evenly.
    /// </summary>
    public int SubShardIndex { get; set; }
    public int SubShardCount { get; set; } = 1;

    public sealed class Result
    {
        public double[] Totals { get; set; } = Array.Empty<double>();
        public double Mass { get; set; }
        public long Combinations { get; set; }
        public long Paths { get; set; }
        public long MaxPathsPerAct { get; set; }
        public long Restores { get; set; }
        public TimeSpan Elapsed { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public Result Run(IReadOnlyList<List<StopGroup>> groups, IReadOnlyList<uint> ranges, int metrics, Measure measure)
    {
        int reels = groups.Count;
        // Split on leading reels until there are enough work items for good load balance.
        int splitDepth = 0; long items = 1;
        while (splitDepth < reels && items < Threads * 8L) { items *= groups[splitDepth].Count; splitDepth++; }
        long totalCombos = 1; foreach (var g in groups) totalCombos *= g.Count;

        var workItems = new List<int[]>();
        var idx = new int[splitDepth];
        if (splitDepth == 0) workItems.Add(Array.Empty<int>());
        else
        {
            while (true)
            {
                workItems.Add((int[])idx.Clone());
                int d = splitDepth - 1;
                while (d >= 0 && ++idx[d] == groups[d].Count) { idx[d] = 0; d--; }
                if (d < 0) break;
            }
        }

        if (ShardCount > 1)
        {
            // Keep every ShardCount-th work item, so several processes / machines can split the run.
            workItems = workItems.Where((_, i) => i % ShardCount == ShardIndex).ToList();
            long shardCombos = 0;
            foreach (var head in workItems)
            {
                long c = 1; for (int d = splitDepth; d < reels; d++) c *= groups[d].Count;
                shardCombos += c;
            }
            totalCombos = shardCombos;
        }

        long nextItem = -1, doneCombos = 0, pathsSoFar = 0;
        var results = new List<(KahanVector acc, double mass, long paths, long maxPaths, long restores, List<string> warnings)>();
        var gate = new object();
        var sw = Stopwatch.StartNew();
        Exception? failure = null;

        void Worker(int seed)
        {
            try
            {
                var host = GameHost.Load(dat, betPerLine, lines, seed, SuppressFeatureEntry);
                var partition = new DrawPartition(host.Game);
                var act = new ActEnumerator(host, partition) { AlwaysRestore = AlwaysRestore };
                var acc = new KahanVector(metrics);
                var scratch = new double[metrics];
                double mass = 0;
                var values = new uint[reels];
                var choice = new int[reels];
                long lastPaths = 0;
                while (true)
                {
                    long item = Interlocked.Increment(ref nextItem);
                    if (item >= workItems.Count || Volatile.Read(ref failure) is not null) break;
                    var head = workItems[(int)item];
                    for (int d = 0; d < splitDepth; d++) choice[d] = head[d];
                    for (int d = splitDepth; d < reels; d++) choice[d] = 0;
                    long combos = 0;
                    while (true)
                    {
                        bool mine = SubShardCount <= 1 || combos % SubShardCount == SubShardIndex;
                        double outer = 1.0;
                        for (int r = 0; mine && r < reels; r++)
                        {
                            var g = groups[r][choice[r]];
                            values[r] = g.Representative;
                            outer *= (double)g.Count / ranges[r];
                        }
                        if (mine)
                        {
                            act.Enumerate(values, (h, res, w) =>
                            {
                                Array.Clear(scratch, 0, scratch.Length);
                                measure(h, res, outer * w, scratch);
                                acc.Add(scratch);
                                mass += outer * w;
                            });
                        }
                        combos++;
                        int d = reels - 1;
                        while (d >= splitDepth && ++choice[d] == groups[d].Count) { choice[d] = 0; d--; }
                        if (d < splitDepth) break;
                    }
                    Interlocked.Add(ref doneCombos, combos);
                    Interlocked.Add(ref pathsSoFar, act.Paths - lastPaths); lastPaths = act.Paths;
                }
                lock (gate) results.Add((acc, mass, act.Paths, act.MaxPathsPerAct, host.Restores, partition.Warnings.ToList()));
            }
            catch (Exception e) { Interlocked.CompareExchange(ref failure, e, null); }
        }

        var threads = Enumerable.Range(0, Math.Max(1, Threads)).Select(i => new Thread(() => Worker(1000 + i)) { IsBackground = true }).ToList();
        foreach (var t in threads) t.Start();
        var lastReport = sw.Elapsed;
        while (threads.Any(t => t.IsAlive))
        {
            Thread.Sleep(200);
            if (sw.Elapsed - lastReport >= ProgressInterval)
            {
                lastReport = sw.Elapsed;
                long done = Interlocked.Read(ref doneCombos);
                double frac = (double)done / totalCombos;
                string eta = frac > 0 ? TimeSpan.FromSeconds(sw.Elapsed.TotalSeconds * (1 - frac) / frac).ToString(@"hh\:mm\:ss") : "?";
                Console.WriteLine($"    [{Label}] {frac:P1} of {totalCombos:N0} combinations, {Interlocked.Read(ref pathsSoFar):N0} paths, elapsed {sw.Elapsed:hh\\:mm\\:ss}, ETA {eta}");
            }
        }
        if (failure is not null) throw new InvalidOperationException($"{Label} failed: {failure.Message}", failure);

        var total = new KahanVector(metrics);
        var result = new Result { Combinations = totalCombos, Elapsed = sw.Elapsed };
        double massTotal = 0;
        foreach (var r in results)
        {
            total.Add(r.acc.Values);
            massTotal += r.mass;
            result.Paths += r.paths;
            result.MaxPathsPerAct = Math.Max(result.MaxPathsPerAct, r.maxPaths);
            result.Restores += r.restores;
            foreach (var w in r.warnings) if (!result.Warnings.Contains(w)) result.Warnings.Add(w);
        }
        result.Totals = total.Values;
        result.Mass = massTotal;
        return result;
    }
}
