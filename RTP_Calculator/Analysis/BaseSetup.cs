using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Analysis;

/// <summary>
/// Everything the base-game decomposition needs to know about the game, read from the engine:
/// the reel-stop requests and strips, the bet lines and their cells, and the scatter symbols.
/// Also defines how reel stops are grouped for each part of the decomposition.
/// </summary>
public sealed class BaseSetup
{
    public ReelModel Model { get; private set; } = new ReelModel();
    public List<int> BetLines { get; } = new List<int>();
    public Dictionary<int, List<(int reel, int window)>> LineCells { get; } = new Dictionary<int, List<(int reel, int window)>>();
    public HashSet<string> ScatterSymbols { get; private set; } = new HashSet<string>(StringComparer.Ordinal);
    public uint[] Ranges { get; private set; } = Array.Empty<uint>();

    /// <summary>Draws made while preparing the spin for this setup (fixed for a scenario).</summary>
    public List<uint> PrepareValues { get; } = new List<uint>();
    public List<uint> PrepareRanges { get; } = new List<uint>();

    public static BaseSetup Read(GameHost host) => Read(host, null, null);

    /// <summary>The same setup with more draws fixed (e.g. in-spin draws that do not depend on the stops).</summary>
    public BaseSetup WithFixedDraws(IEnumerable<uint> values, IEnumerable<uint> ranges)
    {
        var copy = new BaseSetup { Model = Model, ScatterSymbols = ScatterSymbols, Ranges = Ranges };
        copy.BetLines.AddRange(BetLines);
        foreach (var kv in LineCells) copy.LineCells[kv.Key] = kv.Value;
        copy.PrepareValues.AddRange(values); copy.PrepareRanges.AddRange(ranges);
        return copy;
    }

    /// <summary>
    /// Read the base game as prepared by the engine. With <paramref name="prepareValues"/> the draws made
    /// during Prepare (e.g. ReelGroupSet choices) are scripted, so a specific reel-strip scenario is read.
    /// </summary>
    public static BaseSetup Read(GameHost host, List<uint>? prepareValues, List<uint>? prepareRanges)
    {
        var setup = new BaseSetup();
        host.EnsureBase(force: true);
        if (prepareValues is null) host.Agent.BeginRandom();
        else host.Agent.BeginScripted(prepareValues, prepareRanges!, prepareValues.Count);
        var requests = host.Session.Prepare(host.BetPerLine, host.Lines);
        if (prepareValues is not null) { setup.PrepareValues.AddRange(host.Agent.Values); setup.PrepareRanges.AddRange(host.Agent.Ranges); }
        var layout = host.Game.CurrentLayout;
        setup.Model = ReelModel.Read(host, requests);
        for (int i = 0; i < layout.PayLines.Count; i++)
            if (layout.PayLines.Entry(i).Value.Bet) setup.BetLines.Add(i);
        foreach (int l in setup.BetLines) setup.LineCells[l] = Cells(setup.Model, layout.PayLines.Entry(l).Value);
        setup.ScatterSymbols = ReadScatterSymbols(layout);
        setup.Ranges = setup.Model.Reels.Select(r => r.Range).ToArray();
        // Finish the prepared act so the session is consistent, then return to the snapshot.
        host.Session.Complete(requests.Select(r => host.Agent.Next(r.Range)).ToArray());
        host.Restore();
        return setup;
    }

    private int ScatterCount(string[] window) { int n = 0; foreach (var s in window) if (ScatterSymbols.Contains(s)) n++; return n; }

    /// <summary>Stop groups for the line part: stops that put the same symbol(s) on <paramref name="line"/>.</summary>
    public List<List<StopGroup>> LineGroups(int line)
    {
        var cells = LineCells[line];
        return Model.Reels.Select((reel, r) =>
        {
            var windows = cells.Where(c => c.reel == r).Select(c => c.window).ToArray();
            return StopGrouping.Group(reel, LineKey(windows), ScatterCount);
        }).ToList();
    }

    public Func<string[], string> LineKey(int line, int reel) =>
        LineKey(LineCells[line].Where(c => c.reel == reel).Select(c => c.window).ToArray());

    private static Func<string[], string> LineKey(int[] windows) =>
        windows.Length > 0 ? (w => string.Join("|", windows.Select(i => w[i]))) : (w => string.Join("|", w));

    /// <summary>Stop groups for the non-line part: stops with the same scatter-symbol positions.</summary>
    public List<List<StopGroup>> NonLineGroups() =>
        Model.Reels.Select(reel => StopGrouping.Group(reel, NonLineKey(reel), w => 0)).ToList();

    public Func<string[], string> NonLineKey(ReelInfo reel) =>
        reel.Hidden ? (w => string.Join("|", w)) : (w => string.Join("|", w.Select(s => ScatterSymbols.Contains(s) ? s : ".")));

    private static List<(int reel, int window)> Cells(ReelModel model, AGTPayLine line)
    {
        var cells = new List<(int reel, int window)>();
        for (uint i = 0; i < line.Graph.NumCoordinates; i++)
        {
            var c = line.Graph.Coordinate(i);
            if (!model.TryLocate(c, out int reel, out int window))
                throw new NotSupportedException($"Payline cell ({c.X},{c.Y}) is not covered by any reel window.");
            cells.Add((reel, window));
        }
        return cells;
    }

    private static HashSet<string> ReadScatterSymbols(AGTLayout layout)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pc in layout.PayConditions)
        {
            if (!pc.Value.Scatter) continue;
            var set = pc.Value.Symbols;
            for (uint i = 0; i < set.NumSymbols(); i++)
            {
                var s = set[i];
                if (!s.Name.IsEmpty) names.Add(s.Name.CString);
                if (!s.AliasName.IsEmpty) names.Add(s.AliasName.CString);
            }
        }
        return names;
    }

    /// <summary>
    /// All bet lines have the same win distribution when every line takes exactly one cell from each
    /// of the same reels and those reels are unweighted (with a uniform stop, every row of a reel shows
    /// each symbol with the same probability as its frequency on the strip).
    /// </summary>
    public bool CheckLineSymmetry(out string note)
    {
        HashSet<int>? reelSet = null;
        foreach (var kv in LineCells)
        {
            var reels = kv.Value.Select(c => c.reel).ToList();
            if (reels.Distinct().Count() != reels.Count) { note = $"payline {kv.Key} uses a reel more than once"; return false; }
            var set = new HashSet<int>(reels);
            if (reelSet is null) reelSet = set;
            else if (!reelSet.SetEquals(set)) { note = $"payline {kv.Key} uses a different set of reels"; return false; }
        }
        if (reelSet is not null)
            foreach (int r in reelSet)
                if (Model.Reels[r].Weighted) { note = $"reel {Model.Reels[r].Name} is weighted"; return false; }
        note = "every bet line takes one cell from each of the same unweighted reels";
        return true;
    }
}
