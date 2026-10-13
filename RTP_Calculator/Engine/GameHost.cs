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
    public ManagedGameSession Session { get; private set; }
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
    public static GameHost Load(byte[] dat, uint betPerLine, uint lines, int seed = 1, bool suppressFeatureEntry = false, bool lenient = false)
    {
        var host = new GameHost(dat, betPerLine, lines, seed) { lenient = lenient };
        host.WarmUpAndSnapshot(suppressFeatureEntry);
        return host;
    }

    /// <summary>
    /// Load an engine instance and snapshot it in a given game state (e.g. a feature state captured from another
    /// instance with <see cref="Capture"/>). Acts are then played from that state; the host counts as "away" as
    /// soon as the state signature differs from the snapshot's (a free spin is used up, a feature ends, ...).
    /// </summary>
    public static GameHost LoadAt(byte[] dat, uint betPerLine, uint lines, AGTReelGame state, int seed = 1, int? freeSpins = null, bool suppressExits = false)
    {
        var host = new GameHost(dat, betPerLine, lines, seed);
        host.Game.Assign(state);
        if (freeSpins is int fs)
        {
            // Free spins left set high: acts from this state then never run out of spins, so consecutive acts
            // stay in the same state (apart from the counter, which the state comparison ignores) and need no restore.
            typeof(AGTReelGame).GetProperty(nameof(AGTReelGame.FreeSpinsLeft))!.SetValue(host.Game, fs);
            host.ignoreFreeSpins = true; host.inflatedFreeSpins = fs;
        }
        // Copying a state from another engine instance copies its layouts and strategies, and with them that
        // instance's random-number source: rebind them to this host's agent, or their draws would bypass it.
        host.RebindRandom();
        host.snapshotLayoutIndex = host.Game.CurrentLayoutIndex;
        host.snapshot = FullCopy(host.Game);
        host.Game.Assign(host.snapshot);
        host.stateMode = true;
        host.snapshotSignature = host.StateSignature(false);
        // The layout the next act is played on (a pending strategy can switch layout during Prepare).
        host.Agent.BeginRandom();
        var requests = host.Session.Prepare(betPerLine, lines);
        host.playLayoutIndex = host.Game.CurrentLayoutIndex;
        host.Session.Complete(requests.Select(r => host.Agent.Next(r.Range)).ToArray());
        host.Restore();
        if (suppressExits) host.SuppressExits();
        return host;
    }

    /// <summary>Point every engine object that keeps its own random source (layouts, their credit weights, strategies) at this host's.</summary>
    private void RebindRandom()
    {
        int rebound = 0;
        void Rebind(object? o)
        {
            if (o is null) return;
            for (var t = o.GetType(); t is not null && t != typeof(object); t = t.BaseType)
                foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(AGTRand) && !ReferenceEquals(f.GetValue(o), Random)) { f.SetValue(o, Random); rebound++; }
        }
        foreach (var layout in Game.Layouts) { Rebind(layout.Value); Rebind(layout.Value.Credits); }
        foreach (var strategy in Game.Strategies) Rebind(strategy.Value);
        Rebind(Game);
    }

    private static readonly System.Reflection.FieldInfo[] PendingFields = new[] { "ReelSymbolsetChangeInfo", "SymbolsetChangeInfo" }
        .Select(n => typeof(AGTLayout).GetField(n, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
        .Where(f => f is not null).Select(f => f!).ToArray();

    private static readonly System.Reflection.FieldInfo? RandomReelGroupsField =
        typeof(AGTLayout).GetField("AvailableRandomReelGroups", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    private bool stateMode;
    private bool ignoreFreeSpins;
    private int inflatedFreeSpins;

    /// <summary>
    /// State mode: switch off the strategies that leave the played layout on one of its pay conditions (e.g. Hold &amp; Spin
    /// entered from free games). The act then stays in the layout, so the state it leads to is the one the game returns
    /// to after that sub-feature; the sub-feature's entry is detected from its trigger (<see cref="FeatureEntryTriggered"/>).
    /// </summary>
    private void SuppressExits()
    {
        var layout = Game.Layouts.Entry((int)playLayoutIndex).Value;
        string name = LayoutName((int)playLayoutIndex);
        var keys = new List<string>();
        foreach (var entry in Game.Strategies)
            if (entry.Value.Enabled && entry.Value.Layout.CString != name && layout.PayConditions.Contains(entry.Key)) keys.Add(entry.Key);
        if (keys.Count == 0) return;
        Mutate(g => { foreach (var k in keys) g.Strategies[k].Enabled = false; });
        foreach (var k in keys) FeatureEntryTriggers.Add(k);
        FeatureEntrySuppressed = true;
    }
    private uint playLayoutIndex;
    /// <summary>True for hosts started in a given state (<see cref="LoadAt"/>).</summary>
    public bool StateMode => stateMode;
    private string snapshotSignature = "";

    /// <summary>Copy of the live game state (to start another host there).</summary>
    public AGTReelGame Capture() => FullCopy(Game);

    /// <summary>
    /// The copy constructor resets some runtime fields (free spins left among them); Assign copies everything,
    /// so a mid-feature state survives the copy.
    /// </summary>
    private static AGTReelGame FullCopy(AGTReelGame source)
    {
        var copy = new AGTReelGame(source);
        copy.Assign(source);
        return copy;
    }

    /// <summary>
    /// What identifies a game state between acts: layout, free spins left, the strategy bookkeeping, and every
    /// reel's current symbol set and enabled flag in the current layout (features keep their state in hidden
    /// reels whose symbol set is switched by wins), plus enabled reel-group sets.
    /// </summary>
    public string StateSignature() => StateSignature(true);

    /// <summary>Signature; without the free-spin count when <paramref name="withFreeSpins"/> is false and this host ignores it.</summary>
    public string StateSignature(bool withFreeSpins) =>
        Signature(Game, Session.FeaturePending, ignoreFreeSpins && !withFreeSpins ? "*" : null);

    /// <summary>
    /// Signature of a game state (see <see cref="StateSignature()"/>). <paramref name="featurePending"/> is the session's
    /// view (free spins or picks left); <paramref name="freeSpins"/>, when given, replaces the free-spin count.
    /// </summary>
    public static string Signature(AGTReelGame g, bool featurePending, string? freeSpins = null)
    {
        var layout = g.CurrentLayout;
        var sb = new System.Text.StringBuilder();
        sb.Append(g.CurrentLayoutName).Append("|fs").Append(freeSpins ?? g.FreeSpinsLeft.ToString()).Append("|s").Append(g.CurrentStrategyIndex).Append('>').Append(g.NextStrategyIndex)
          .Append("|k").Append(g.StackedStrategyCount).Append("|q").Append(g.QueuedStrategies.Count).Append("|p").Append(featurePending ? 1 : 0);
        for (int i = 0; i < layout.Reels.Count; i++)
        {
            var reel = layout.Reels.Entry(i).Value;
            sb.Append('|').Append(reel.CurrentSymbolSetName.CString);
            if (!reel.Enabled) sb.Append('-');
        }
        for (int i = 0; i < layout.ReelGroupSets.Count; i++) sb.Append(layout.ReelGroupSets.Entry(i).Value.Enabled ? "|G" : "|g");
        // Changes a win has queued for the next act (applied when it starts): reel symbol-set switches and the like.
        // Only for the current layout and the layout a pending strategy moves to: changes queued in other layouts
        // (e.g. a sub-feature's starting board while that sub-feature is switched off) do not affect play here.
        int nextLayout = g.NextStrategyIndex < g.Strategies.Count ? g.Layouts.KeyIndex(g.Strategies.Entry((int)g.NextStrategyIndex).Value.Layout.CString) : -1;
        for (int l = 0; l < g.Layouts.Count; l++)
        {
            if (l != (int)g.CurrentLayoutIndex && l != nextLayout) continue;
            var lay = g.Layouts.Entry(l).Value;
            foreach (var field in PendingFields)
                if (field.GetValue(lay) is System.Collections.IDictionary map && map.Count > 0)
                {
                    sb.Append("|pending").Append(l).Append(field.Name);
                    foreach (System.Collections.DictionaryEntry e in map) sb.Append(':').Append(e.Key).Append('=').Append(e.Value);
                }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Apply the changes a win has queued for the next act now, as the engine would at the start of that act
    /// (AGTLayout.ResetWinningPayconditionLists). Only when that step makes no random draws (no random reel groups)
    /// and no reel-group set is enabled (it is evaluated just before). Returns false when not possible.
    /// </summary>
    public static bool ApplyPending(AGTReelGame g)
    {
        bool any = false;
        for (int l = 0; l < g.Layouts.Count; l++)
        {
            var lay = g.Layouts.Entry(l).Value;
            bool pending = PendingFields.Any(f => f.GetValue(lay) is System.Collections.IDictionary m && m.Count > 0);
            if (!pending) continue;
            if (RandomReelGroupsField?.GetValue(lay) is System.Collections.ICollection groups && groups.Count > 0) return false;
            for (int i = 0; i < lay.ReelGroupSets.Count; i++) if (lay.ReelGroupSets.Entry(i).Value.Enabled) return false;
            any = true;
        }
        if (!any) return false;
        for (int l = 0; l < g.Layouts.Count; l++)
        {
            var lay = g.Layouts.Entry(l).Value;
            if (PendingFields.Any(f => f.GetValue(lay) is System.Collections.IDictionary m && m.Count > 0)) lay.ResetWinningPayconditionLists();
        }
        return true;
    }

    /// <summary>
    /// Names of the pay conditions whose win moves the game out of the base layout. In the engine a
    /// strategy is keyed by its trigger name, and a pay-condition trigger is true when it is awarded.
    /// Only populated when feature entry is suppressed.
    /// </summary>
    public HashSet<string> FeatureEntryTriggers { get; } = new HashSet<string>(StringComparer.Ordinal);
    public bool FeatureEntrySuppressed { get; private set; }
    private bool lenient;
    /// <summary>
    /// False when (lenient mode) some feature strategies were switched off without a base pay condition to detect
    /// them by: FeatureEntryTriggered may then miss entries. Fine for measuring line wins only.
    /// </summary>
    public bool FeatureEntryDetectionComplete { get; private set; } = true;

    /// <summary>
    /// Did the act just played enter a feature: from the trigger pay condition when feature entry is switched off,
    /// otherwise from the game leaving the base state. In state mode without switched-off sub-features, false (every
    /// act changes the state; the feature chain reads the next state instead).
    /// </summary>
    public bool EntryTriggered(ActResult result) => FeatureEntrySuppressed ? FeatureEntryTriggered() : !stateMode && result.LeftBase;

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
    /// <summary>The layout analysed: the base layout, or in state mode the layout the act from the snapshot is played on.</summary>
    public int BaseLayoutIndex => (int)(stateMode ? playLayoutIndex : snapshotLayoutIndex);

    /// <summary>True when the game is anywhere other than "base layout, ready for a fresh paid game".</summary>
    public bool IsAwayFromBase => stateMode ? (ignoreFreeSpins && Game.FreeSpinsLeft < inflatedFreeSpins / 2) || StateSignature(false) != snapshotSignature :
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
            {
                // Keyed by a pay condition of another layout (e.g. a feature's own trigger): it cannot fire while the
                // base layout is played, unless the key is also a game-level trigger.
                bool gameLevel = Game.Triggers.Any(t => t.Key == entry.Key) || Game.KeyWordTriggers.Any(t => t.Key == entry.Key);
                if (!gameLevel) { strategy.Enabled = false; continue; }
                if (lenient) { strategy.Enabled = false; FeatureEntryDetectionComplete = false; continue; }
            }
            if (!payConditionNames.Contains(entry.Key))
                throw new NotSupportedException(
                    $"Strategy '{strategy.Name.CString}' (to layout '{strategy.Layout.CString}') is triggered by '{entry.Key}', which is not a " +
                    "base pay condition, so feature entry cannot be detected without letting the strategy run.");
            FeatureEntryTriggers.Add(entry.Key);
            strategy.Enabled = false;
        }
        FeatureEntrySuppressed = true;
    }

    /// <summary>
    /// Apply a change to both the live game and its snapshot, so it survives restores. Used only by
    /// analysis-only engine instances (e.g. an oracle that must not perform certain replacements);
    /// it never touches files or the engine source.
    /// </summary>
    public void Mutate(Action<AGTReelGame> change)
    {
        change(Game);
        if (snapshot is not null) change(snapshot);
    }

    /// <summary>Restore the snapshot and start a fresh session (after an act that was interrupted part-way).</summary>
    public void RestoreAfterError()
    {
        Session = new ManagedGameSession(Game, Random);
        Restore();
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
        if (!force && stateMode && ClearPendingIfOnlyDifference()) return;
        if (force || IsAwayFromBase) Restore();
    }

    /// <summary>
    /// State mode: when the act only queued changes for the next act (e.g. a wild counter increment) and is otherwise
    /// back in the snapshot state, drop the queued changes instead of a full restore (which copies every layout).
    /// </summary>
    private bool ClearPendingIfOnlyDifference()
    {
        if (ignoreFreeSpins && Game.FreeSpinsLeft < inflatedFreeSpins / 2) return false;
        string full = StateSignature(false);
        if (full == snapshotSignature) return true;
        if (!LightRevert) return false;
        // Same layout and strategy bookkeeping: put back the reels' symbol sets and enabled flags and the queued
        // changes from the snapshot instead of copying every layout (a full restore costs ~10 ms).
        var snap = snapshot!;
        if (Game.CurrentLayoutIndex != snap.CurrentLayoutIndex || Game.NextStrategyIndex != snap.NextStrategyIndex
            || Game.CurrentStrategyIndex != snap.CurrentStrategyIndex || Game.StackedStrategyCount != snap.StackedStrategyCount
            || Game.QueuedStrategies.Count != snap.QueuedStrategies.Count) return false;
        var layout = Game.CurrentLayout; var snapLayout = snap.CurrentLayout;
        for (int i = 0; i < layout.Reels.Count; i++)
        {
            var reel = layout.Reels.Entry(i); var snapReel = snapLayout.Reels.Entry(i).Value;
            if (reel.Value.CurrentSymbolSetName.CString != snapReel.CurrentSymbolSetName.CString) layout.SetReelSymbolSet(reel.Key, snapReel.CurrentSymbolSetName.CString);
            if (reel.Value.Enabled != snapReel.Enabled) reel.Value.SetEnabled(snapReel.Enabled);
        }
        for (int l = 0; l < Game.Layouts.Count; l++)
            foreach (var field in PendingFields)
                if (field.GetValue(Game.Layouts.Entry(l).Value) is System.Collections.IDictionary map && field.GetValue(snap.Layouts.Entry(l).Value) is System.Collections.IDictionary snapMap)
                {
                    map.Clear();
                    foreach (System.Collections.DictionaryEntry e in snapMap) map[e.Key] = e.Value;
                }
        LightReverts++;
        return StateSignature(false) == snapshotSignature;
    }

    /// <summary>
    /// State mode: undo an act that only switched reel symbol sets or queued changes by putting those back (see
    /// <see cref="ClearPendingIfOnlyDifference"/>), instead of a full restore. Checked by the feature chain against full restores.
    /// </summary>
    public bool LightRevert { get; set; } = true;
    public long LightReverts { get; private set; }

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
    private int actStartFreeSpins;

    /// <summary>
    /// The state the act just played led to, with the free-spin count as the change during the act ("fs" field
    /// "d-1" = one spin used, "d+7" = retrigger of 8, ...), so it does not depend on the count the act started from.
    /// </summary>
    public string TransitionSignature()
    {
        string full = StateSignature(true);
        int bar = full.IndexOf("|fs", StringComparison.Ordinal);
        int end = full.IndexOf('|', bar + 3);
        int delta = Game.FreeSpinsLeft - actStartFreeSpins;
        return full.Substring(0, bar) + "|fsd" + (delta >= 0 ? "+" : "") + delta + full.Substring(end);
    }

    public void BeginAccounting()
    {
        actStartFreeSpins = Game.FreeSpinsLeft;
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

    /// <summary>
    /// Sum of all line-type wins in the act just played: payline wins plus ways (pattern) wins. These are
    /// computed by the line and ways evaluators; everything else is "non-line".
    /// </summary>
    public ulong AllLineWins()
    {
        ulong sum = 0;
        foreach (var w in Game.CurrentLayout.WinningStandardPayconditions) sum += w.WonCredits;
        return sum + AllPatternWins();
    }

    /// <summary>Sum of the ways (pattern) wins in the act just played.</summary>
    public ulong AllPatternWins()
    {
        ulong sum = 0;
        foreach (var w in Game.CurrentLayout.WinningPatternPayconditions)
            foreach (var c in w.CoincidingWins) sum += c.WonCredits;
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
