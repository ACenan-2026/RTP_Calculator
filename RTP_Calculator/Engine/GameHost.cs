using AgtEngine.Core.Game;
using AgtEngine.Core.Misc;
using AgtEngine.Core.Random;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Engine;

/// <summary>
/// Owns one independent instance of the real win engine (AgtEngine.Core), driven the same way the
/// emulator drives it: Prepare (host asks for the reel-stop requests) then Complete (engine plays
/// the act, consuming any further randomness through <see cref="ScriptedRandomAgent"/>).
///
/// Each host can snapshot the game in its base "ready" state and restore it, so the enumerator can
/// replay the same act from the same state many times. Hosts are not thread-safe; use one per thread.
/// </summary>
public sealed class GameHost
{
    public const uint NoStrategy = 60000;

    private AGTReelGame? snapshot;
    private uint snapshotLayoutIndex;
    private readonly ulong[] creditedBefore;

    public ScriptedRandomAgent Agent { get; }
    public AGTRand Random { get; }
    public AGTReelGame Game { get; }
    public ManagedGameSession Session { get; }
    public uint BetPerLine { get; }
    public uint Lines { get; }
    public ulong TotalBet => (ulong)BetPerLine * Lines * (ulong)PostWinDivider;
    public int PostWinDivider { get; }

    /// <summary>Restores performed so far (diagnostics).</summary>
    public long Restores { get; private set; }

    private GameHost(byte[] dat, uint betPerLine, uint lines, int seed)
    {
        Agent = new ScriptedRandomAgent(seed);
        Random = new AGTRand(Agent);
        // Same construction sequence as ManagedPtEngine.LoadConfiguration: load a template, then copy-construct.
        var template = new AGTGameLoader(Random, new VariableListContext()).CreateGameInstance(dat);
        Game = new AGTReelGame(template);
        BetPerLine = betPerLine;
        Lines = lines;
        PostWinDivider = Game.Variables.VariableExists("POST_WIN_DIVIDER") && Game.Variables.Variable("POST_WIN_DIVIDER").AsBoolean()
            ? Game.Variables.Variable("POST_WIN_DIVIDER").AsInt32() : 1;
        Game.TotalBet = unchecked((uint)TotalBet);
        Session = new ManagedGameSession(Game, Random);
        creditedBefore = new ulong[Game.Layouts.Count];
        snapshotLayoutIndex = Game.CurrentLayoutIndex;
    }

    /// <summary>
    /// Load an independent engine instance and snapshot it in the base game.
    /// With <paramref name="suppressFeatureEntry"/> the strategies that leave the base layout are disabled
    /// after warm-up (see <see cref="FeatureEntryTriggers"/>), so base acts never change state and never
    /// need restoring. Feature entry is then detected from the trigger pay condition instead.
    /// </summary>
    public static GameHost Load(byte[] dat, uint betPerLine, uint lines, int seed = 1, bool suppressFeatureEntry = false)
    {
        var host = new GameHost(dat, betPerLine, lines, seed);
        host.WarmUpAndSnapshot(suppressFeatureEntry);
        return host;
    }

    /// <summary>
    /// Names of the pay conditions whose win moves the game out of the base layout. In the engine a
    /// strategy is keyed by its trigger name, and a pay-condition trigger is true when it is awarded.
    /// Only populated when feature entry is suppressed.
    /// </summary>
    public HashSet<string> FeatureEntryTriggers { get; } = new HashSet<string>(StringComparer.Ordinal);
    public bool FeatureEntrySuppressed { get; private set; }

    /// <summary>In suppressed mode: did a feature-entry trigger pay condition win in the act just played?</summary>
    public bool FeatureEntryTriggered()
    {
        var layout = Game.CurrentLayout;
        foreach (var list in new[] { layout.WinningStandardPayconditions, layout.WinningLocatedPayconditions,
                                     layout.WinningScatteredPayconditions, layout.CombinedWinPayconditions, layout.WinningPatternPayconditions })
            foreach (var w in list)
                if (FeatureEntryTriggers.Contains(w.PayconditionName.CString)) return true;
        return false;
    }

    public int LayoutCount => Game.Layouts.Count;
    public string LayoutName(int index) => Game.Layouts.Entry(index).Key;
    public int BaseLayoutIndex => (int)snapshotLayoutIndex;

    /// <summary>True when the game is anywhere other than "base layout, ready for a fresh paid game".</summary>
    public bool IsAwayFromBase =>
        Session.FeaturePending
        || Game.NextStrategyIndex != NoStrategy
        || Game.CurrentLayoutIndex != snapshotLayoutIndex
        || Game.StackedStrategyCount > 0
        || Game.QueuedStrategies.Count > 0;

    /// <summary>
    /// Play random paid games until one finishes cleanly in the base game, then snapshot that state.
    /// The first Prepare also runs the bet initialiser, so the snapshot is a true "between paid games" state.
    /// </summary>
    private void WarmUpAndSnapshot(bool suppressFeatureEntry)
    {
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            PlayRandomAct();
            while (IsAwayFromBase) PlayRandomAct();
            // One more clean act from base so the snapshot follows a normal base act.
            PlayRandomAct();
            if (!IsAwayFromBase)
            {
                snapshotLayoutIndex = Game.CurrentLayoutIndex;
                if (suppressFeatureEntry) SuppressFeatureEntry();
                snapshot = new AGTReelGame(Game);
                // The copy constructor resets a few runtime fields; restore through Assign so the
                // snapshot and the live game are produced by the same code path.
                Game.Assign(snapshot);
                return;
            }
        }
        throw new InvalidOperationException("Could not reach a clean base-game state during warm-up.");
    }

    private void SuppressFeatureEntry()
    {
        string baseName = LayoutName((int)snapshotLayoutIndex);
        var baseLayout = Game.CurrentLayout;
        var payConditionNames = new HashSet<string>(baseLayout.PayConditions.Select(p => p.Key), StringComparer.Ordinal);
        foreach (var entry in Game.Strategies)
        {
            var strategy = entry.Value;
            if (!strategy.Enabled || strategy.Layout.CString == baseName) continue;
            if (!payConditionNames.Contains(entry.Key))
                throw new NotSupportedException(
                    $"Strategy '{strategy.Name.CString}' (to layout '{strategy.Layout.CString}') is triggered by '{entry.Key}', which is not a " +
                    "base pay condition, so feature entry cannot be detected without letting the strategy run.");
            FeatureEntryTriggers.Add(entry.Key);
            strategy.Enabled = false;
        }
        FeatureEntrySuppressed = true;
    }

    /// <summary>Return the live game to the snapshot state.</summary>
    public void Restore()
    {
        if (snapshot is null) throw new InvalidOperationException("No snapshot.");
        Game.Assign(snapshot);
        Restores++;
    }

    /// <summary>Restore only if the previous act left the base game.</summary>
    public void EnsureBase(bool force = false)
    {
        if (force || IsAwayFromBase) Restore();
    }

    /// <summary>Plays one act with emulator-style random values. Returns the per-layout credited win.</summary>
    public ActResult PlayRandomAct()
    {
        Agent.BeginRandom();
        BeginAccounting();
        var requests = Session.Prepare(BetPerLine, Lines);
        var values = new uint[requests.Count];
        for (int i = 0; i < values.Length; i++) values[i] = Agent.Next(requests[i].Range);
        int playedLayout = (int)Game.CurrentLayoutIndex;
        Session.Complete(values);
        return EndAccounting(playedLayout);
    }

    /// <summary>Snapshot of each layout's cumulative credited win, to diff after the act.</summary>
    public void BeginAccounting()
    {
        for (int i = 0; i < creditedBefore.Length; i++) creditedBefore[i] = Game.Layouts.Entry(i).Value.TotalCreditsWon;
    }

    public ActResult EndAccounting(int playedLayout)
    {
        var result = new ActResult(creditedBefore.Length) { PlayedLayout = playedLayout };
        for (int i = 0; i < creditedBefore.Length; i++)
            result.Credited[i] = unchecked(Game.Layouts.Entry(i).Value.TotalCreditsWon - creditedBefore[i]);
        result.LeftBase = IsAwayFromBase;
        return result;
    }

    /// <summary>Sum of line (standard pay condition) wins on the given payline in the act just played.</summary>
    public ulong LineWin(int paylineIndex)
    {
        ulong sum = 0;
        foreach (var w in Game.CurrentLayout.WinningStandardPayconditions)
            if (w.PaylineIndex == (uint)paylineIndex) sum += w.WonCredits;
        return sum;
    }

    /// <summary>Sum of all line wins in the act just played.</summary>
    public ulong AllLineWins()
    {
        ulong sum = 0;
        foreach (var w in Game.CurrentLayout.WinningStandardPayconditions) sum += w.WonCredits;
        return sum;
    }

    /// <summary>Sum of every win record the engine reports for the act (lines, located, scatter, combined, pattern).</summary>
    public ulong AllReportedWins()
    {
        var layout = Game.CurrentLayout;
        ulong sum = 0;
        foreach (var w in layout.WinningStandardPayconditions) sum += w.WonCredits;
        foreach (var w in layout.WinningLocatedPayconditions) sum += w.WonCredits;
        foreach (var w in layout.WinningScatteredPayconditions) sum += w.WonCredits;
        foreach (var w in layout.CombinedWinPayconditions) sum += w.WonCredits;
        // Pattern ("ways") wins pay per coinciding win. The list is empty for non-pattern layouts.
        foreach (var w in layout.WinningPatternPayconditions)
            foreach (var c in w.CoincidingWins) sum += c.WonCredits;
        return sum;
    }
}

public sealed class ActResult
{
    public ActResult(int layouts) { Credited = new ulong[layouts]; }
    public int PlayedLayout { get; set; }
    /// <summary>Credits added to each layout's TotalCreditsWon by this act (what the emulator reports per layout).</summary>
    public ulong[] Credited { get; }
    public bool LeftBase { get; set; }
    public ulong TotalCredited { get { ulong s = 0; foreach (var c in Credited) s += c; return s; } }
}
