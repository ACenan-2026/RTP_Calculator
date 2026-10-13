using System.Globalization;
using AgtEngine.Core.ReelGame;
using RTP_Calculator.Analysis.Analytic;
using RTP_Calculator.Engine;

namespace RTP_Calculator.Analysis.Feature;

/// <summary>
/// Expected value of a feature (free games and similar), as a Markov chain over engine states.
///
///  * A state is what <see cref="GameHost.StateSignature"/> captures between acts: layout, free spins left, strategy
///    bookkeeping, every reel's symbol set (features keep counters such as a wild count or ring count on hidden
///    reels) and changes a win has queued for the next act.
///  * One spin from a state is a "kernel", computed exactly by <see cref="AnalyticBaseGame"/> started in that state:
///    expected win, and the distribution of the state it leads to. The kernel is computed with the free-spin count
///    set high and keyed by the state without the count: a spin is assumed to play the same whatever the count, and
///    the count's change (one spin used, retriggers) is part of the transition.
///  * Sub-features entered from the feature (e.g. Hold &amp; Spin from free games) are switched off in the kernel: their
///    entry probability is reported, and the game continues from the state it returns to.
///  * V(state, spins left) = win(state) + Σ P(next) · V(next, spins left + change), and V = 0 when no spins are left.
/// </summary>
public sealed class FeatureChain
{
    private readonly byte[] dat;
    private readonly uint betPerLine, lines;

    public string? CachePath { get; set; }
    public int Threads { get; set; } = 1;
    public int VerifySamples { get; set; } = 10;
    public Action<string>? Log { get; set; }
    public int MaxSpins { get; set; } = 500;

    public sealed class Kernel
    {
        public string Key = "";
        public double LineWin, NonLineWin, ExitProbability;
        public Dictionary<string, double> Breakdown = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>(next state key, change in free spins, probability, legacy state string of an example)</summary>
        public List<(string key, int delta, double p, string state)> Next = new List<(string, int, double, string)>();
        public string Verify = "";
        public double Win => LineWin + NonLineWin;
    }

    public Dictionary<string, Kernel> Kernels { get; } = new Dictionary<string, Kernel>(StringComparer.Ordinal);

    public FeatureChain(byte[] dat, uint betPerLine, uint lines) { this.dat = dat; this.betPerLine = betPerLine; this.lines = lines; }

    /// <summary>
    /// Find how the feature on <paramref name="layout"/> starts: play random base games until it is entered, and
    /// check over <paramref name="samples"/> entries that it always starts in the same state.
    /// </summary>
    public List<(AGTReelGame state, string signature, int count)> FindEntries(string layout, int samples = 2000, int seed = 7)
    {
        var host = GameHost.Load(dat, betPerLine, lines, seed);
        var g = host.Game;
        var found = new List<(AGTReelGame state, string signature, int count)>();
        int total = 0;
        for (long i = 0; i < 50_000_000 && total < samples; i++)
        {
            host.PlayRandomAct();
            if (g.NextStrategyIndex != GameHost.NoStrategy && g.Strategies.Entry((int)g.NextStrategyIndex).Value.Layout.CString == layout && g.CurrentLayoutName != layout)
            {
                // The strategy the base game was on is bookkeeping only here: the next act starts the feature's strategy.
                string sig = System.Text.RegularExpressions.Regex.Replace(host.StateSignature(), @"\|s\d+>", "|s?>");
                int at = found.FindIndex(f => f.signature == sig);
                if (at < 0) found.Add((host.Capture(), sig, 1));
                else found[at] = (found[at].state, sig, found[at].count + 1);
                total++;
                while (host.IsAwayFromBase) host.PlayRandomAct();
            }
        }
        if (found.Count == 0) throw new InvalidOperationException($"Feature '{layout}' was never entered from the base game.");
        foreach (var f in found) Log?.Invoke($"feature '{layout}' entered {f.count} of {total} times from: {Short(f.signature)}");
        return found;
    }

    /// <summary>Kernel key: the state signature without the free-spin count.</summary>
    public static (string key, int fs) Split(string signature)
    {
        int bar = signature.IndexOf("|fs", StringComparison.Ordinal);
        int end = signature.IndexOf('|', bar + 3);
        string count = signature.Substring(bar + 3, end - bar - 3);
        int fs = count.StartsWith("d") ? int.Parse(count.Substring(1), CultureInfo.InvariantCulture) : int.Parse(count, CultureInfo.InvariantCulture);
        return (signature.Substring(0, bar) + "|fs*" + signature.Substring(end), fs);
    }

    private GameHost StateHost(string legacy)
    {
        var h = GameHost.Load(dat, betPerLine, lines, 77);
        uint b = betPerLine, l = lines;
        int rc = h.Game.RestoreLegacyState(legacy, ref b, ref l);
        if (rc < -1) throw new InvalidOperationException($"Could not restore a saved feature state (code {rc})."); // -1 is the engine's success code
        return h;
    }

    public sealed class Result
    {
        public double ExpectedWin, ExpectedSpins, ExpectedExits, TotalBet;
        public double LineWin, NonLineWin;
        public Dictionary<string, double> Breakdown = new Dictionary<string, double>(StringComparer.Ordinal);
        public int Kernels, Steps;
        /// <summary>Expected number of spins played in each state.</summary>
        public Dictionary<string, double> Visits = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The entry state still has the layout switch pending (its next act moves into the feature). Playing from there
    /// means a full engine restore after every act, so it is replaced by the equivalent state inside the feature:
    /// one quiet act is played (first outcome of every draw, a stop vector whose windows start no replacement), the
    /// free-spin count is set back, and a few random spins from both states are compared (win and next state).
    /// </summary>
    private AGTReelGame InsideEntry(AGTReelGame entry, GameHost probe)
    {
        if (probe.BaseLayoutIndex == (int)probe.Game.CurrentLayoutIndex) return entry;
        int fs = probe.Game.FreeSpinsLeft;
        var h = GameHost.LoadAt(dat, betPerLine, lines, entry, 9);
        var setup = BaseSetup.Read(h, new List<uint>(), new List<uint>());
        // a quiet stop: the first stop of each reel with nothing but the commonest symbols is not known here, so
        // try stop vectors until the act leaves no queued change behind.
        var rng = new System.Random(3);
        for (int attempt = 0; attempt < 200; attempt++)
        {
            var stops = setup.Model.Reels.Select(r => attempt == 0 ? r.Stops[0].stop : r.Stops[rng.Next(r.Stops.Count)].stop).ToArray();
            h.Restore();
            h.Agent.BeginScripted(new List<uint>(), new List<uint>(), 0);
            h.BeginAccounting();
            h.Session.Prepare(betPerLine, lines);
            int played = (int)h.Game.CurrentLayoutIndex;
            h.Session.Complete(stops);
            h.EndAccounting(played);
            string sig = h.StateSignature();
            if (sig.Contains("|pending") || (int)h.Game.CurrentLayoutIndex != h.BaseLayoutIndex || h.Game.NextStrategyIndex != GameHost.NoStrategy) continue;
            var inside = h.Capture();
            typeof(AGTReelGame).GetProperty(nameof(AGTReelGame.FreeSpinsLeft))!.SetValue(inside, fs);
            CheckSameSpin(entry, inside);
            Log?.Invoke($"entry: first spin is played from the feature state {Short(Split(GameHost.LoadAt(dat, betPerLine, lines, inside, 3).StateSignature()).key)}");
            return inside;
        }
        return entry; // no quiet act found: analyse the entry state directly (slower)
    }

    /// <summary>
    /// Kernels undo most acts by putting back reel symbol sets and queued changes (<see cref="GameHost.LightRevert"/>).
    /// Check on random spins, played one after another, that this gives what full restores give.
    /// </summary>
    private void CheckLightRevert(AGTReelGame state, int spins = 60)
    {
        var light = GameHost.LoadAt(dat, betPerLine, lines, state, 13, 1000, suppressExits: true);
        var full = GameHost.LoadAt(dat, betPerLine, lines, state, 13, 1000, suppressExits: true);
        full.LightRevert = false;
        var setup = BaseSetup.Read(full, new List<uint>(), new List<uint>());
        var rng = new System.Random(31);
        for (int t = 0; t < spins; t++)
        {
            var stops = setup.Model.Reels.Select(r => r.Stops[rng.Next(r.Stops.Count)].stop).ToArray();
            int seed = rng.Next();
            string Play(GameHost h)
            {
                h.EnsureBase();
                h.Agent.Reseed(seed); h.Agent.BeginScripted(new List<uint>(), new List<uint>(), 0, randomTail: true);
                h.BeginAccounting();
                h.Session.Prepare(betPerLine, lines);
                int played = (int)h.Game.CurrentLayoutIndex;
                h.Session.Complete(stops);
                var res = h.EndAccounting(played);
                return res.Credited[h.BaseLayoutIndex] + " " + h.TransitionSignature();
            }
            string a = Play(light), b = Play(full);
            if (a != b) throw new InvalidOperationException($"Undoing acts by putting reel sets back differs from a full restore: {Short(a)} vs {Short(b)}.");
        }
    }

    private void CheckSameSpin(AGTReelGame a, AGTReelGame b, int spins = 40)
    {
        var ha = GameHost.LoadAt(dat, betPerLine, lines, a, 11);
        var hb = GameHost.LoadAt(dat, betPerLine, lines, b, 11);
        var setup = BaseSetup.Read(ha, new List<uint>(), new List<uint>());
        var rng = new System.Random(21);
        for (int t = 0; t < spins; t++)
        {
            var stops = setup.Model.Reels.Select(r => r.Stops[rng.Next(r.Stops.Count)].stop).ToArray();
            int seed = rng.Next();
            string Play(GameHost h)
            {
                h.Restore();
                h.Agent.Reseed(seed); h.Agent.BeginScripted(new List<uint>(), new List<uint>(), 0, randomTail: true);
                h.BeginAccounting();
                h.Session.Prepare(betPerLine, lines);
                int played = (int)h.Game.CurrentLayoutIndex;
                h.Session.Complete(stops);
                var res = h.EndAccounting(played);
                return res.Credited[h.BaseLayoutIndex] + " " + System.Text.RegularExpressions.Regex.Replace(h.TransitionSignature(), @"\|s\d+>", "|s?>");
            }
            string ra = Play(ha), rb = Play(hb);
            if (ra != rb) throw new NotSupportedException($"Two states expected to play alike differ: {Short(ra)} vs {Short(rb)}.");
        }
    }

    public Result Run(AGTReelGame entry)
    {
        LoadCache();
        var probe = GameHost.LoadAt(dat, betPerLine, lines, entry, 3);
        entry = InsideEntry(entry, probe);
        probe = GameHost.LoadAt(dat, betPerLine, lines, entry, 3);
        var exits = GameHost.LoadAt(dat, betPerLine, lines, entry, 3, 1000, suppressExits: true).FeatureEntryTriggers;
        Log?.Invoke("sub-features switched off in the kernels (entries counted): " + (exits.Count == 0 ? "none" : string.Join(", ", exits)));
        var (entryKey, entryFs) = Split(probe.StateSignature());
        string entryLegacy = probe.Game.CaptureLegacyState(betPerLine, lines);
        var states = new Dictionary<string, string>(StringComparer.Ordinal) { [entryKey] = entryLegacy };
        var objects = new Dictionary<string, AGTReelGame>(StringComparer.Ordinal) { [entryKey] = entry };
        var queue = new Queue<string>(); queue.Enqueue(entryKey);
        var seen = new HashSet<string>(StringComparer.Ordinal) { entryKey };
        while (queue.Count > 0)
        {
            string key = queue.Dequeue();
            if (!Kernels.TryGetValue(key, out var kernel))
            {
                AGTReelGame state;
                if (objects.TryGetValue(key, out var o)) state = o;
                else
                {
                    var restored = StateHost(states[key]);
                    if (Split(restored.StateSignature()).key != key)
                        throw new InvalidOperationException($"A saved feature state did not restore to the same state ({Short(key)}); delete the cache and run again.");
                    state = restored.Capture();
                }
                kernel = Compute(key, state, out var nextObjects);
                foreach (var (k, obj) in nextObjects) if (!objects.ContainsKey(k)) objects[k] = obj;
                Kernels[key] = kernel;
                SaveCache();
            }
            foreach (var n in kernel.Next)
                if (seen.Add(n.key)) { states[n.key] = n.state; queue.Enqueue(n.key); }
        }

        // Expected visits to each (state, spins left), propagated forward from the entry: a spin from (key, fs)
        // leads to (next, fs + change); the feature ends when no spins are left. Totals are visits x kernel values.
        // Works for unbounded retriggers too: propagation stops when the probability still in play is negligible.
        var visits = new Dictionary<string, double>(StringComparer.Ordinal);
        var current = new Dictionary<(string key, int fs), double> { [(entryKey, entryFs)] = 1.0 };
        double remaining = 1; int steps = 0;
        while (remaining > 1e-15)
        {
            if (++steps > MaxSpins * 100) throw new NotSupportedException($"Feature still running with probability {remaining:G3} after {steps - 1} spins.");
            var next = new Dictionary<(string, int), double>();
            foreach (var ((key, fs), p) in current)
            {
                visits[key] = (visits.TryGetValue(key, out var v) ? v : 0) + p;
                foreach (var n in Kernels[key].Next)
                {
                    int nfs = fs + n.delta;
                    if (nfs <= 0) continue;
                    next[(n.key, nfs)] = (next.TryGetValue((n.key, nfs), out var x) ? x : 0) + p * n.p;
                }
            }
            current = next;
            remaining = current.Values.Sum();
        }
        var result = new Result { TotalBet = probe.TotalBet, Kernels = Kernels.Count, Steps = steps, Visits = visits };
        foreach (var (key, n) in visits)
        {
            var k = Kernels[key];
            result.ExpectedSpins += n; result.ExpectedWin += n * k.Win; result.LineWin += n * k.LineWin; result.NonLineWin += n * k.NonLineWin;
            result.ExpectedExits += n * k.ExitProbability;
            foreach (var (f, v) in k.Breakdown) result.Breakdown[f] = (result.Breakdown.TryGetValue(f, out var x) ? x : 0) + n * v;
        }
        return result;
    }

    private Kernel Compute(string key, AGTReelGame state, out List<(string key, AGTReelGame obj)> nextObjects)
    {
        Log?.Invoke($"kernel {Kernels.Count + 1}: {Short(key)}");
        string? kernelCache = CachePath is null ? null : CachePath + "." + Hash(key);
        var tracker = new StateTracker { BetPerLine = betPerLine, Lines = lines, FilePath = kernelCache is null ? null : kernelCache + ".states" };
        tracker.Load();
        var analysis = new AnalyticBaseGame(dat, betPerLine, lines)
        {
            StartState = state, StateFreeSpins = 1000, Tracker = tracker, VerifySamples = VerifySamples,
            Threads = Threads, Verbose = Log is not null, CachePath = kernelCache
        };
        CheckLightRevert(state);
        var report = analysis.Run(verify: VerifySamples > 0);
        if (report.LinesOnly) throw new NotSupportedException($"Feature state {Short(key)}: " + report.NonLineError);
        var kernel = new Kernel
        {
            Key = key, LineWin = report.ExpectedLineWin, NonLineWin = report.ExpectedNonLineWin, ExitProbability = report.TriggerProbability,
            Verify = $"{report.VerifiedLines - report.LineMismatches}/{report.VerifiedLines} lines, {report.VerifiedScatter - report.ScatterMismatches}/{report.VerifiedScatter} other"
        };
        if (report.LineMismatches > 0 || report.ScatterMismatches > 0)
            throw new InvalidOperationException($"Feature state {Short(key)} failed verification: {string.Join(" / ", report.MismatchExamples)}");
        foreach (var (f, v) in report.NonLineBreakdown) kernel.Breakdown[f] = v;
        nextObjects = new List<(string, AGTReelGame)>();
        foreach (var (id, p) in report.NextStates)
        {
            var (nextKey, delta) = Split(tracker.Signatures[id]);
            kernel.Next.Add((nextKey, delta, p, tracker.Legacy[id]));
            if (tracker.States[id] is AGTReelGame obj) nextObjects.Add((nextKey, obj));
        }
        // States recorded with their queued changes applied must play like the states actually reached.
        foreach (var (raw, canonical) in tracker.Canonicalised) CheckSameSpin(raw, canonical, 20);
        double mass = kernel.Next.Sum(n => n.p);
        if (Math.Abs(mass - 1) > 1e-9) throw new InvalidOperationException($"Transition probabilities from {Short(key)} add up to {mass}.");
        Log?.Invoke($"  win {kernel.Win:G8} credits (lines {kernel.LineWin:G8}), exit {kernel.ExitProbability:G6}, {kernel.Next.Count} next state(s), verify {kernel.Verify}");
        return kernel;
    }

    private static string Hash(string key)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key)).Take(8).Select(b => b.ToString("x2")));
    }

    private static string Short(string key) => key.Length > 160 ? key.Substring(0, 60) + " ... " + key.Substring(key.Length - 90) : key;

    // ---- cache: one kernel per block, so an interrupted run resumes ----
    private void SaveCache()
    {
        if (CachePath is null) return;
        using var w = new StreamWriter(CachePath);
        var inv = CultureInfo.InvariantCulture;
        foreach (var k in Kernels.Values)
        {
            w.WriteLine("K\t" + k.Key);
            w.WriteLine("W\t" + k.LineWin.ToString("R", inv) + "\t" + k.NonLineWin.ToString("R", inv) + "\t" + k.ExitProbability.ToString("R", inv) + "\t" + k.Verify);
            foreach (var (f, v) in k.Breakdown) w.WriteLine("B\t" + f + "\t" + v.ToString("R", inv));
            foreach (var n in k.Next) w.WriteLine("N\t" + n.key + "\t" + n.delta + "\t" + n.p.ToString("R", inv) + "\t" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(n.state)));
        }
    }

    private void LoadCache()
    {
        if (CachePath is null || !File.Exists(CachePath)) return;
        var inv = CultureInfo.InvariantCulture;
        Kernel? k = null;
        foreach (var line in File.ReadLines(CachePath))
        {
            var f = line.Split('\t');
            switch (f[0])
            {
                case "K": k = new Kernel { Key = f[1] }; Kernels[k.Key] = k; break;
                case "W": k!.LineWin = double.Parse(f[1], inv); k.NonLineWin = double.Parse(f[2], inv); k.ExitProbability = double.Parse(f[3], inv); k.Verify = f.Length > 4 ? f[4] : ""; break;
                case "B": k!.Breakdown[f[1]] = double.Parse(f[2], inv); break;
                case "N": k!.Next.Add((f[1], int.Parse(f[2], inv), double.Parse(f[3], inv), System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f[4])))); break;
            }
        }
    }
}
