using AgtEngine.Core.Geometry;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Analysis;

/// <summary>
/// Static description of the reel-stop requests of the base game, read from the engine's own
/// layout objects: for every request (reel), what each stop value shows in every window cell.
/// </summary>
public sealed class ReelModel
{
    public List<ReelInfo> Reels { get; } = new List<ReelInfo>();

    public static ReelModel Read(GameHost host, IReadOnlyList<ManagedGameSession.DrawRequest> requests)
    {
        var model = new ReelModel();
        var layout = host.Game.CurrentLayout;
        for (int i = 0; i < requests.Count; i++)
        {
            if (requests[i].ReelIndex is not uint reelIndex)
                throw new NotSupportedException("The base game requests a non-reel draw (choice); not supported by this proof of concept.");
            var entry = layout.Reels.Entry((int)reelIndex);
            var reel = entry.Value;
            uint range = requests[i].Range;
            int windows = (int)reel.Graph.NumCoordinates;
            uint stripLength = reel.Symbols.NumSymbols();
            var info = new ReelInfo
            {
                RequestIndex = i,
                ReelIndex = (int)reelIndex,
                Name = entry.Key,
                Range = range,
                Hidden = reel.Hidden,
                Weighted = reel.Symbols.ExtendedSymbolsPresent,
                Coordinates = Enumerable.Range(0, windows).Select(w => reel.Graph.Coordinate((uint)w)).ToArray(),
            };
            // What a stop shows depends only on the strip position it maps to. An unweighted reel maps stop p to
            // position p; a weighted reel maps a run of stop values to each position (weights can total 10^8, so
            // the model keeps one entry per position, never one per stop value).
            int positions = info.Weighted ? (int)stripLength : (int)range;
            if (info.Weighted && reel.Symbols.NumSymbolsBasedOnExtension != range)
                throw new NotSupportedException($"Weighted reel '{entry.Key}': draw range {range} differs from its total weight {reel.Symbols.NumSymbolsBasedOnExtension}.");
            var windowNames = new string[positions][];
            var windowSymbols = new AgtEngine.Core.Graphic.AGTSymbol[positions][];
            for (int position = 0; position < positions; position++)
            {
                var cells = new string[windows];
                var symbols = new AgtEngine.Core.Graphic.AGTSymbol[windows];
                for (int w = 0; w < windows; w++)
                {
                    // Copies, so later engine activity cannot change the model.
                    symbols[w] = new AgtEngine.Core.Graphic.AGTSymbol(reel.Symbols[unchecked((uint)position + (uint)w) % stripLength]);
                    cells[w] = symbols[w].Name.CString;
                }
                windowNames[position] = cells;
                windowSymbols[position] = symbols;
            }
            var stops = new List<(uint stop, long count)>(positions);
            uint[] firstStop;
            if (!info.Weighted)
            {
                for (uint p = 0; p < range; p++)
                {
                    if (reel.Symbols.ActualSymbolIndexInExtendedSet(p) != p)
                        throw new NotSupportedException($"Reel '{entry.Key}': stop {p} does not map to strip position {p}.");
                    stops.Add((p, 1));
                }
                firstStop = Array.Empty<uint>();
            }
            else
            {
                // Cumulative weights, as AGTSymbolSet.ActualSymbolIndexInExtendedSet maps a draw to a position.
                firstStop = new uint[positions];
                ulong sum = 0;
                for (int position = 0; position < positions; position++)
                {
                    uint weight = reel.Symbols[(uint)position].Statistics.Frequency;
                    firstStop[position] = (uint)sum;
                    if (weight > 0) stops.Add(((uint)sum, weight));
                    sum += weight;
                }
                // Check the mapping against the engine at both ends of every position's run of stop values.
                for (int position = 0; position < positions; position++)
                {
                    uint weight = reel.Symbols[(uint)position].Statistics.Frequency;
                    if (weight == 0) continue;
                    if (reel.Symbols.ActualSymbolIndexInExtendedSet(firstStop[position]) != position ||
                        reel.Symbols.ActualSymbolIndexInExtendedSet(firstStop[position] + weight - 1) != position)
                        throw new NotSupportedException($"Weighted reel '{entry.Key}': stop-to-position mapping differs from the engine's.");
                }
            }
            info.SetStops(stops, windowNames, windowSymbols, firstStop);
            model.Reels.Add(info);
        }
        return model;
    }

    /// <summary>Find which reel and window index displays a layout coordinate.</summary>
    public bool TryLocate(AGTCoordinate coordinate, out int reelListIndex, out int window)
    {
        for (int r = 0; r < Reels.Count; r++)
        {
            var coords = Reels[r].Coordinates;
            for (int w = 0; w < coords.Length; w++)
                if (coords[w].X == coordinate.X && coords[w].Y == coordinate.Y) { reelListIndex = r; window = w; return true; }
        }
        reelListIndex = -1; window = -1; return false;
    }
}

public sealed class ReelInfo
{
    public int RequestIndex { get; set; }
    public int ReelIndex { get; set; }
    public string Name { get; set; } = "";
    public uint Range { get; set; }
    public bool Hidden { get; set; }
    public bool Weighted { get; set; }
    public AGTCoordinate[] Coordinates { get; set; } = Array.Empty<AGTCoordinate>();
    /// <summary>Windows[stop][windowIndex] = symbol name shown before any replacement (any stop value in [0, Range)).</summary>
    public StopTable<string[]> Windows { get; private set; } = null!;
    /// <summary>WindowSymbols[stop][windowIndex] = the engine's symbol object (with qualifiers such as wild).</summary>
    public StopTable<AgtEngine.Core.Graphic.AGTSymbol[]> WindowSymbols { get; private set; } = null!;
    /// <summary>
    /// Distinct stops with the number of stop values they stand for: every stop with count 1 on an unweighted
    /// reel, one stop per strip position (count = its weight) on a weighted reel. Loops over a reel's outcomes
    /// use this, weighting each entry by count / Range.
    /// </summary>
    public IReadOnlyList<(uint stop, long count)> Stops { get; private set; } = Array.Empty<(uint, long)>();

    internal void SetStops(List<(uint stop, long count)> stops, string[][] names, AgtEngine.Core.Graphic.AGTSymbol[][] symbols, uint[] firstStop)
    {
        Stops = stops;
        Func<uint, int> position = Weighted
            ? stop => { int k = Array.BinarySearch(firstStop, stop); if (k < 0) k = ~k - 1; while (k + 1 < firstStop.Length && firstStop[k + 1] == stop) k++; return k; }
            : stop => (int)stop;
        Windows = new StopTable<string[]>(names, position);
        WindowSymbols = new StopTable<AgtEngine.Core.Graphic.AGTSymbol[]>(symbols, position);
    }
}

/// <summary>Per-stop lookup backed by one entry per strip position; enumerating it gives each position once.</summary>
public sealed class StopTable<T> : IEnumerable<T>
{
    private readonly T[] byPosition;
    private readonly Func<uint, int> position;
    public StopTable(T[] byPosition, Func<uint, int> position) { this.byPosition = byPosition; this.position = position; }
    public T this[uint stop] => byPosition[position(stop)];
    public T this[int stop] => byPosition[position((uint)stop)];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)byPosition).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => byPosition.GetEnumerator();
}

/// <summary>A set of reel stops that are equivalent for the quantity being computed.</summary>
public sealed class StopGroup
{
    public StopGroup(string key, uint representative, long count) { Key = key; Representative = representative; Count = count; }
    public string Key { get; }
    public uint Representative { get; set; }
    public long Count { get; set; }
}

public static class StopGrouping
{
    /// <summary>
    /// Group the stops of <paramref name="reel"/> by <paramref name="key"/>. Within a group the
    /// representative is the stop with the lowest <paramref name="score"/> (ties: lowest stop).
    /// </summary>
    public static List<StopGroup> Group(ReelInfo reel, Func<string[], string> key, Func<string[], int> score)
    {
        var groups = new Dictionary<string, StopGroup>(StringComparer.Ordinal);
        var bestScore = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (p, count) in reel.Stops)
        {
            var window = reel.Windows[p];
            string k = key(window);
            int s = score(window);
            if (!groups.TryGetValue(k, out var g))
            {
                groups[k] = new StopGroup(k, p, count); bestScore[k] = s; order.Add(k);
            }
            else
            {
                g.Count += count;
                if (s < bestScore[k]) { bestScore[k] = s; g.Representative = p; }
            }
        }
        return order.Select(k => groups[k]).ToList();
    }
}
