using AgtEngine.Core.ReelGame;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Analysis.Feature;

/// <summary>
/// Gives every game state reached after an act a small integer id (by <see cref="GameHost.TransitionSignature"/>:
/// the state with the free-spin count as its change during the act) and
/// keeps a copy of the first engine state seen with each signature, so that state can be analysed later.
/// Used as an extra metric: an indicator vector over state ids, whose expectation is the next-state distribution.
/// </summary>
public sealed class StateTracker
{
    private readonly object gate = new object();
    private readonly Dictionary<string, int> ids = new Dictionary<string, int>(StringComparer.Ordinal);
    public List<string> Signatures { get; } = new List<string>();
    public List<AGTReelGame> States { get; } = new List<AGTReelGame>();
    public int Capacity { get; }

    public StateTracker(int capacity = 256) { Capacity = capacity; }

    private readonly Dictionary<string, int> rawIds = new Dictionary<string, int>(StringComparer.Ordinal);
    /// <summary>States whose queued changes were applied early: (as reached, as recorded) for checking they play alike.</summary>
    public List<(AGTReelGame raw, AGTReelGame canonical)> Canonicalised { get; } = new List<(AGTReelGame, AGTReelGame)>();
    /// <summary>
    /// Record states with queued changes already applied. Off by default: queued reel-set changes take effect after the
    /// next act's stops are set (Galaxy Gold's wild counter reads the old hidden symbol for one more spin), so applying
    /// them early changes the game.
    /// </summary>
    public bool ApplyQueuedChanges { get; set; }
    /// <summary>Engine save-state of each recorded state (for resuming); filled when <see cref="BetPerLine"/> is set.</summary>
    public List<string> Legacy { get; } = new List<string>();
    public uint BetPerLine { get; set; }
    public uint Lines { get; set; }
    /// <summary>Optional file the recorded states are appended to, so ids stay the same when a run resumes.</summary>
    public string? FilePath { get; set; }

    /// <summary>Load states recorded by an earlier run (same order, same ids).</summary>
    public void Load()
    {
        if (FilePath is null || !File.Exists(FilePath)) return;
        foreach (var line in File.ReadLines(FilePath))
        {
            var f = line.Split('\t');
            if (f.Length < 2) continue;
            ids[f[0]] = Signatures.Count; rawIds[f[0]] = Signatures.Count;
            Signatures.Add(f[0]); States.Add(null!); Legacy.Add(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f[1])));
        }
    }

    public int Id(GameHost host)
    {
        string raw = host.TransitionSignature();
        lock (gate)
        {
            if (rawIds.TryGetValue(raw, out int known)) return known;
            var state = host.Capture();
            string signature = raw;
            if (raw.Contains("|pending") && ApplyQueuedChanges)
            {
                // A state with changes queued for the next act plays like the state with them applied, which is what
                // the next act starts from anyway; recording it applied lets that state's acts mostly keep the state.
                var applied = host.Capture();
                if (GameHost.ApplyPending(applied))
                {
                    int bar = raw.IndexOf("|fs", StringComparison.Ordinal), end = raw.IndexOf('|', bar + 3);
                    string fsField = raw.Substring(bar + 3, end - bar - 3);
                    signature = GameHost.Signature(applied, applied.FreeSpinsLeft > 0 || applied.PicksAwarded - applied.PicksConsumed > 0, fsField);
                    Canonicalised.Add((state, applied));
                    state = applied;
                }
            }
            if (!ids.TryGetValue(signature, out int id))
            {
                if (Signatures.Count >= Capacity) throw new NotSupportedException($"More than {Capacity} distinct next states from one state.");
                id = Signatures.Count;
                ids[signature] = id; Signatures.Add(signature); States.Add(state);
                string legacy = BetPerLine > 0 ? host.Game.CaptureLegacyState(BetPerLine, Lines) : "";
                Legacy.Add(legacy);
                if (FilePath is not null) File.AppendAllText(FilePath, signature + "\t" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(legacy)) + Environment.NewLine);
            }
            rawIds[raw] = id;
            return id;
        }
    }

    public double[] Indicator(GameHost host)
    {
        var v = new double[Capacity];
        v[Id(host)] = 1;
        return v;
    }
}
