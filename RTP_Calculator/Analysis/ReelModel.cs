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
                Windows = new string[range][],
                WindowSymbols = new AgtEngine.Core.Graphic.AGTSymbol[range][]
            };
            for (uint p = 0; p < range; p++)
            {
                uint position = reel.Symbols.ActualSymbolIndexInExtendedSet(p);
                var cells = new string[windows];
                var symbols = new AgtEngine.Core.Graphic.AGTSymbol[windows];
                for (int w = 0; w < windows; w++)
                {
                    // Copies, so later engine activity cannot change the model.
                    symbols[w] = new AgtEngine.Core.Graphic.AGTSymbol(reel.Symbols[unchecked(position + (uint)w) % stripLength]);
                    cells[w] = symbols[w].Name.CString;
                }
                info.Windows[p] = cells;
                info.WindowSymbols[p] = symbols;
            }
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
    /// <summary>Windows[stop][windowIndex] = symbol name shown before any replacement.</summary>
    public string[][] Windows { get; set; } = Array.Empty<string[]>();
    /// <summary>WindowSymbols[stop][windowIndex] = the engine's symbol object (with qualifiers such as wild).</summary>
    public AgtEngine.Core.Graphic.AGTSymbol[][] WindowSymbols { get; set; } = Array.Empty<AgtEngine.Core.Graphic.AGTSymbol[]>();
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
        for (uint p = 0; p < reel.Range; p++)
        {
            var window = reel.Windows[p];
            string k = key(window);
            int s = score(window);
            if (!groups.TryGetValue(k, out var g))
            {
                groups[k] = new StopGroup(k, p, 1); bestScore[k] = s; order.Add(k);
            }
            else
            {
                g.Count++;
                if (s < bestScore[k]) { bestScore[k] = s; g.Representative = p; }
            }
        }
        return order.Select(k => groups[k]).ToList();
    }
}
