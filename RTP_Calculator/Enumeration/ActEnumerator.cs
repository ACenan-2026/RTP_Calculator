using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Enumeration;

/// <summary>
/// Exhaustively enumerates every outcome of ONE act for fixed reel-stop request values.
///
/// The act is replayed from the same starting state once per branch. Every random draw the engine
/// makes inside the act (replacements, weighted picks, qualifiers, ...) is a branch point; the
/// enumerator walks them depth-first, like an odometer whose digits are the partition cells of
/// each draw, and weights every path by the product of (cell width / range).
///
/// The measure callback is invoked after each path with the host in its post-act state and adds
/// its metrics, already multiplied by the path weight, into an accumulator.
/// </summary>
public sealed class ActEnumerator
{
    private readonly GameHost host;
    private readonly DrawPartition partition;
    private readonly List<uint> prefixValues = new List<uint>(128);
    private readonly List<uint> prefixRanges = new List<uint>(128);
    private IReadOnlyList<ManagedGameSession.DrawRequest>? expectedRequests;

    public ActEnumerator(GameHost host, DrawPartition partition)
    {
        this.host = host;
        this.partition = partition;
    }

    /// <summary>Paths played so far.</summary>
    public long Paths { get; private set; }

    /// <summary>Largest number of internal branches seen for a single act.</summary>
    public long MaxPathsPerAct { get; private set; }

    /// <summary>
    /// Restore the snapshot before every path, not only after paths that left the base game.
    /// Slower, but makes no assumption that a non-triggering base act leaves no relevant state behind.
    /// </summary>
    public bool AlwaysRestore { get; set; }

    /// <summary>Safety cap on paths per act.</summary>
    public long PathLimitPerAct { get; set; } = 50_000_000;

    /// <summary>
    /// Enumerate all internal branches of the act defined by <paramref name="requestValues"/>.
    /// <paramref name="measure"/> receives (host, actResult, pathWeight).
    /// Returns the total probability mass covered (should be 1 up to rounding).
    /// </summary>
    public double Enumerate(uint[] requestValues, Action<GameHost, ActResult, double> measure)
    {
        prefixValues.Clear(); prefixRanges.Clear();
        double mass = 0;
        long pathsThisAct = 0;
        while (true)
        {
            host.EnsureBase(AlwaysRestore);
            host.Agent.BeginScripted(prefixValues, prefixRanges, prefixValues.Count);
            host.BeginAccounting();
            var requests = host.Session.Prepare(host.BetPerLine, host.Lines);
            CheckRequests(requests, requestValues);
            int played = (int)host.Game.CurrentLayoutIndex;
            host.Session.Complete(requestValues);
            var result = host.EndAccounting(played);

            var ranges = host.Agent.Ranges;
            var values = host.Agent.Values;
            double weight = 1.0;
            for (int d = 0; d < ranges.Count; d++)
                weight *= (double)(partition.NextCellStart(ranges[d], values[d]) - values[d]) / ranges[d];

            measure(host, result, weight);
            mass += weight;
            Paths++; pathsThisAct++;
            if (pathsThisAct > PathLimitPerAct)
                throw new InvalidOperationException($"More than {PathLimitPerAct:N0} internal branches for one act; aborting.");

            // Advance the odometer: deepest draw that still has another cell.
            int depth = ranges.Count - 1;
            while (depth >= 0)
            {
                uint next = partition.NextCellStart(ranges[depth], values[depth]);
                if (next < ranges[depth]) break;
                depth--;
            }
            if (depth < 0) break;
            prefixValues.Clear(); prefixRanges.Clear();
            for (int d = 0; d < depth; d++) { prefixValues.Add(values[d]); prefixRanges.Add(ranges[d]); }
            prefixValues.Add(partition.NextCellStart(ranges[depth], values[depth]));
            prefixRanges.Add(ranges[depth]);
        }
        if (pathsThisAct > MaxPathsPerAct) MaxPathsPerAct = pathsThisAct;
        return mass;
    }

    private void CheckRequests(IReadOnlyList<ManagedGameSession.DrawRequest> requests, uint[] values)
    {
        if (expectedRequests is null)
        {
            expectedRequests = requests.ToArray();
        }
        else
        {
            bool same = requests.Count == expectedRequests.Count;
            for (int i = 0; same && i < requests.Count; i++)
                same = requests[i].Range == expectedRequests[i].Range && requests[i].ReelIndex == expectedRequests[i].ReelIndex;
            if (!same) throw new InvalidOperationException(
                "The reel-stop requests changed between acts (reel strips are switching). This proof of concept " +
                "assumes the base game's reel strips are fixed.");
        }
        if (values.Length != requests.Count) throw new ArgumentException("One value per request is required.");
    }
}
