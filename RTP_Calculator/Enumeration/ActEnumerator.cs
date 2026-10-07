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
    private readonly List<uint> scenarioValues = new List<uint>();
    private readonly List<uint> scenarioRanges = new List<uint>();

    /// <summary>
    /// Fix the first draws of every act (the draws made while preparing the spin, e.g. a ReelGroupSet
    /// choosing which reel strips are active). They are replayed as given, never branched, and excluded
    /// from the path weight: results are conditional on that scenario.
    /// </summary>
    public void SetScenario(IReadOnlyList<uint> values, IReadOnlyList<uint> ranges)
    {
        scenarioValues.Clear(); scenarioValues.AddRange(values);
        scenarioRanges.Clear(); scenarioRanges.AddRange(ranges);
        expectedRequests = null;
    }

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
        prefixValues.AddRange(scenarioValues); prefixRanges.AddRange(scenarioRanges);
        int fixedDraws = scenarioValues.Count;
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
            for (int d = fixedDraws; d < ranges.Count; d++)
                weight *= (double)(partition.NextCellStart(ranges[d], values[d]) - values[d]) / ranges[d];

            measure(host, result, weight);
            mass += weight;
            Paths++; pathsThisAct++;
            if (pathsThisAct > PathLimitPerAct)
                throw new NotSupportedException($"More than {PathLimitPerAct:N0} internal random branches in one spin; too many to enumerate exactly.");

            // Advance the odometer: deepest draw that still has another cell.
            int depth = ranges.Count - 1;
            while (depth >= fixedDraws)
            {
                uint next = partition.NextCellStart(ranges[depth], values[depth]);
                if (next < ranges[depth]) break;
                depth--;
            }
            if (depth < fixedDraws) break;
            prefixValues.Clear(); prefixRanges.Clear();
            for (int d = 0; d < depth; d++) { prefixValues.Add(values[d]); prefixRanges.Add(ranges[d]); }
            prefixValues.Add(partition.NextCellStart(ranges[depth], values[depth]));
            prefixRanges.Add(ranges[depth]);
        }
        if (pathsThisAct > MaxPathsPerAct) MaxPathsPerAct = pathsThisAct;
        return mass;
    }

    // ------------------------------------------------------------------------------------------------
    // Factorised expectation
    // ------------------------------------------------------------------------------------------------

    /// <summary>Checks made by <see cref="Expect"/> (symmetry, companion draws) and how many failed.</summary>
    public long FactorChecks { get; private set; }
    public long FactorFailures { get; private set; }
    /// <summary>Runs evaluated as multisets.</summary>
    public long MultisetRuns { get; private set; }
    /// <summary>Random checks per run.</summary>
    public int ChecksPerRun { get; set; } = 2;
    /// <summary>Largest number of multisets enumerated for one run before multiplying out instead.</summary>
    public long MultisetLimit { get; set; } = 20_000;
    /// <summary>
    /// Optional description of the game state after an act (e.g. every symbol on screen). Outcomes of a
    /// pick that leave the same state, the same result and the same later draws are merged.
    /// </summary>
    public Func<GameHost, string>? StateSignature { get; set; }
    private readonly System.Random checkRandom = new System.Random(17);
    private long pathsThisExpect;
    private readonly HashSet<uint> nonRunRanges = new HashSet<uint>();
    private string? lastSignature;

    /// <summary>Diagnostics: per range, runs and multisets played.</summary>
    public Dictionary<string, (long runs, long multisets)> RunStats { get; } = new Dictionary<string, (long, long)>();
    private sealed class FactorCheckFailed : Exception { public FactorCheckFailed(string why) : base(why) { } }
    /// <summary>Why the last run was multiplied out (diagnostics).</summary>
    public string? LastFactorFailure { get; private set; }

    /// <summary>
    /// Expected value of <paramref name="metrics"/> over every in-spin draw of the act, like
    /// <see cref="Enumerate"/>, but repeated picks are not multiplied out.
    ///
    /// A "run" is a sequence of units, each one draw from the same range R followed by the same number of
    /// companion draws (e.g. a floating replacement: pick a symbol, then pick which remaining cell gets it;
    /// or one coin value pick per coin, with no companions). Within a run:
    ///  * companion draws are taken at their first outcome, after checking that changing one does not
    ///    change the result (they choose between interchangeable cells);
    ///  * outcomes of R that leave the same screen, result and later draws are merged
    ///    (see <see cref="StateSignature"/>; e.g. two weight cells that give the same coin value);
    ///  * the units are exchangeable (the result depends on which outcomes were drawn, not on their order):
    ///    every multiset of outcomes is played once with its multinomial probability.
    /// The assumptions are checked on random cases in every run (a changed companion gives the same result;
    /// a random sequence of raw outcomes gives the same result as its merged, sorted form). A run that fails,
    /// or whose structure changes when a pick changes, is multiplied out in full.
    /// </summary>
    public double[] Expect(uint[] requestValues, Func<GameHost, ActResult, double[]> metrics)
    {
        pathsThisExpect = 0; nonRunRanges.Clear();
        var pv = new List<uint>(scenarioValues); var pr = new List<uint>(scenarioRanges);
        var result = Node(requestValues, pv, pr, metrics, null);
        if (pathsThisExpect > MaxPathsPerAct) MaxPathsPerAct = pathsThisExpect;
        return result;
    }

    private (List<uint> ranges, double[] m) Play(uint[] requestValues, List<uint> pv, List<uint> pr, Func<GameHost, ActResult, double[]> metrics)
    {
        host.EnsureBase(AlwaysRestore);
        host.Agent.BeginScripted(pv, pr, pv.Count);
        host.BeginAccounting();
        var requests = host.Session.Prepare(host.BetPerLine, host.Lines);
        CheckRequests(requests, requestValues);
        int played = (int)host.Game.CurrentLayoutIndex;
        host.Session.Complete(requestValues);
        var result = host.EndAccounting(played);
        Paths++; pathsThisExpect++;
        if (pathsThisExpect > PathLimitPerAct)
            throw new NotSupportedException($"More than {PathLimitPerAct:N0} internal random branches in one spin; too many to enumerate exactly.");
        lastSignature = StateSignature?.Invoke(host);
        return (new List<uint>(host.Agent.Ranges), metrics(host, result));
    }

    /// <summary>Conditional expectation given the first pv.Count draws (later draws enumerated).</summary>
    private double[] Node(uint[] req, List<uint> pv, List<uint> pr, Func<GameHost, ActResult, double[]> metrics, (List<uint> ranges, double[] m)? known)
    {
        var (ranges, m) = known ?? Play(req, pv, pr, metrics);
        int p = pv.Count;
        // Skip draws with a single outcome.
        while (p < ranges.Count && partition.Cells(ranges[p]).Length == 1)
        {
            pv = new List<uint>(pv) { 0 }; pr = new List<uint>(pr) { ranges[p] }; p++;
        }
        if (p >= ranges.Count) return m;
        uint range = ranges[p];

        // A run of units starting here?
        if (!nonRunRanges.Contains(range))
        {
            int next = p + 1;
            while (next < ranges.Count && ranges[next] != range) next++;
            int stride = next - p, units = 1;
            if (next < ranges.Count)
                while (p + units * stride < ranges.Count && ranges[p + units * stride] == range) units++;
            if (units >= 2)
            {
                int length = Math.Min(units * stride, ranges.Count - p);
                try { return Run(req, pv, pr, metrics, (ranges, m), p, range, units, stride, length); }
                catch (ScriptDivergenceException e) { FactorFailures++; nonRunRanges.Add(range); LastFactorFailure = $"range {range} x{units}/{stride}: {e.Message}"; }
                catch (FactorCheckFailed e) { FactorFailures++; nonRunRanges.Add(range); LastFactorFailure = $"range {range} x{units}/{stride}: {e.Message}"; }
                // Multiplying the run out must stay within the path limit.
                double size = 1;
                for (int d = p; d < p + length; d++) size *= partition.Cells(ranges[d]).Length;
                if (size > PathLimitPerAct)
                    throw new NotSupportedException($"{units} repeated picks could not be factorised ({LastFactorFailure}) and multiply out to {size:G3} branches.");
            }
        }

        var cells = partition.Cells(range);
        var acc = new double[m.Length];
        // Outcome 0 in full; other outcomes that leave the same later draws reuse its later-draw contribution
        // (checked on random outcomes).
        var first = Node(req, new List<uint>(pv) { cells[0] }, new List<uint>(pr) { range }, metrics, (ranges, m));
        var shift = new double[m.Length];
        for (int x = 0; x < shift.Length; x++) shift[x] = first[x] - m[x];
        bool hasTail = ranges.Count > p + 1;
        var shiftedCells = new List<(int k, double[] e)>();
        for (int k = 0; k < cells.Length; k++)
        {
            uint end = k + 1 < cells.Length ? cells[k + 1] : range;
            double w = (double)(end - cells[k]) / range;
            double[] sub;
            if (k == 0) sub = first;
            else
            {
                var cv = new List<uint>(pv) { cells[k] }; var cr = new List<uint>(pr) { range };
                var played = Play(req, cv, cr, metrics);
                if (played.ranges.Count == ranges.Count && played.ranges.Skip(p + 1).SequenceEqual(ranges.Skip(p + 1)))
                {
                    sub = new double[m.Length];
                    for (int x = 0; x < sub.Length; x++) sub[x] = played.m[x] + shift[x];
                    if (hasTail) shiftedCells.Add((k, sub));
                }
                else sub = Node(req, cv, cr, metrics, played);
            }
            for (int x = 0; x < acc.Length; x++) acc[x] += w * sub[x];
        }
        for (int c = 0; c < ChecksPerRun && shiftedCells.Count > 0; c++)
        {
            var (k, predicted) = shiftedCells[checkRandom.Next(shiftedCells.Count)];
            var full = Node(req, new List<uint>(pv) { cells[k] }, new List<uint>(pr) { range }, metrics, null);
            FactorChecks++;
            if (!Equal(full, predicted))
            {
                // Later draws interact with this one: evaluate every outcome in full.
                FactorFailures++; LastFactorFailure = $"range {range}: later draws interact with this draw";
                Array.Clear(acc, 0, acc.Length);
                for (int j = 0; j < cells.Length; j++)
                {
                    uint end = j + 1 < cells.Length ? cells[j + 1] : range;
                    var sub = j == 0 ? first : Node(req, new List<uint>(pv) { cells[j] }, new List<uint>(pr) { range }, metrics, null);
                    for (int x = 0; x < acc.Length; x++) acc[x] += (double)(end - cells[j]) / range * sub[x];
                }
                break;
            }
        }
        return acc;
    }

    private static bool Equal(double[] a, double[] b)
    {
        for (int x = 0; x < a.Length; x++)
            if (Math.Abs(a[x] - b[x]) > 1e-9 * Math.Max(1, Math.Max(Math.Abs(a[x]), Math.Abs(b[x])))) return false;
        return true;
    }

    private double[] Run(uint[] req, List<uint> pv, List<uint> pr, Func<GameHost, ActResult, double[]> metrics,
        (List<uint> ranges, double[] m) zeroTrace, int p, uint range, int units, int stride, int length)
    {
        var cells = partition.Cells(range);
        int n = cells.Length;
        var r = new List<uint>(pr);
        for (int d = 0; d < length; d++) r.Add(zeroTrace.ranges[p + d]);
        List<uint> Values(int[] unitCells, (int draw, uint value)? companion = null)
        {
            var v = new List<uint>(pv);
            for (int d = 0; d < length; d++) v.Add(0);
            for (int i = 0; i < units; i++) v[p + i * stride] = cells[unitCells[i]];
            if (companion is { } c) v[p + c.draw] = c.value;
            return v;
        }
        double W(int k) => (double)((k + 1 < n ? cells[k + 1] : range) - cells[k]) / range;
        double[] Eval(int[] unitCells) => Node(req, Values(unitCells), r, metrics, unitCells.All(k => k == 0) ? zeroTrace : null);

        var zero = new int[units];
        var reference = Eval(zero);

        // Companion draws (e.g. which interchangeable cell gets the picked symbol) must not matter.
        for (int d = 0; d < length; d++)
        {
            if (d % stride == 0) continue;
            var cc = partition.Cells(r[pv.Count + d]);
            if (cc.Length < 2) continue;
            var changed = Node(req, Values(zero, (d, cc[1 + checkRandom.Next(cc.Length - 1)])), r, metrics, null);
            FactorChecks++;
            if (!Equal(changed, reference)) throw new FactorCheckFailed("companion draw " + d + " matters");
        }

        // Per unit: merge outcomes that leave the same state (screen, result, later draws), then group the
        // units whose outcomes act alike (same merged classes with the same weights and the same effect when
        // the other units are at their first outcome). Picks of different replacements can share a range.
        var unitClassOf = new int[units][];
        var unitClassCell = new List<int>[units];
        var profiles = new string[units];
        var classWeights = new List<double>[units];
        for (int i = 0; i < units; i++)
        {
            unitClassOf[i] = new int[n]; unitClassCell[i] = new List<int>(); classWeights[i] = new List<double>();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal); var effects = new List<string>();
            for (int k = 0; k < n; k++)
            {
                var u = new int[units]; u[i] = k;
                var (tr, mk) = Play(req, Values(u), r, metrics);
                string effect = string.Join(",", mk.Select(x => x.ToString("R"))) + "#" + string.Join(",", tr);
                string sig = StateSignature is null ? k.ToString() : lastSignature + "#" + effect;
                if (!seen.TryGetValue(sig, out int c)) { seen[sig] = c = classWeights[i].Count; classWeights[i].Add(0); unitClassCell[i].Add(k); effects.Add(effect); }
                unitClassOf[i][k] = c; classWeights[i][c] += W(k);
            }
            profiles[i] = string.Join("|", classWeights[i].Select((w, c) => w.ToString("R") + ":" + effects[c]));
        }
        var groups = Enumerable.Range(0, units).GroupBy(i => profiles[i]).Select(g => g.ToArray()).ToList();

        // Exchangeable within a group: one play per combination of multisets, weighted by multinomial probabilities.
        var logFact = new double[units + 1];
        for (int i = 1; i <= units; i++) logFact[i] = logFact[i - 1] + Math.Log(i);
        var groupSets = new List<List<(int[] seq, double weight)>>();
        double combinations = 1;
        foreach (var g in groups)
        {
            int size = g.Length, nc = classWeights[g[0]].Count;
            double count = 1;
            for (int i = 1; i <= size; i++) count = count * (nc - 1 + i) / i;
            combinations *= count;
            if (combinations > MultisetLimit)
                throw new NotSupportedException($"{units} repeated picks ({string.Join(" + ", groups.Select(x => $"{x.Length} x {classWeights[x[0]].Count} outcomes"))}) in one spin; too many combinations to enumerate exactly.");
            var list = new List<(int[], double)>();
            var seq = new int[size];
            while (true)
            {
                double logW = logFact[size];
                int start = 0;
                for (int i = 1; i <= size; i++)
                    if (i == size || seq[i] != seq[start]) { logW -= logFact[i - start]; start = i; }
                double weight = Math.Exp(logW);
                foreach (var c in seq) weight *= classWeights[g[0]][c];
                list.Add(((int[])seq.Clone(), weight));
                int d = size - 1;
                while (d >= 0 && seq[d] == nc - 1) d--;
                if (d < 0) break;
                seq[d]++;
                for (int i = d + 1; i < size; i++) seq[i] = seq[d];
            }
            groupSets.Add(list);
        }
        string KeyOfClasses(int[] unitClasses) => string.Join(";", groups.Select(g => string.Join(",", g.Select(i => unitClasses[i]).OrderBy(x => x))));

        var result = new double[reference.Length];
        var cache = new Dictionary<string, double[]>(StringComparer.Ordinal);
        // Later draws: when a combination leaves the same later draws as the reference, their contribution is
        // taken from the reference (the later picks are assumed not to interact with this run; checked below).
        int tailStart = p + length;
        var refTail = zeroTrace.ranges.Skip(tailStart).ToList();
        var shift = new double[reference.Length];
        for (int x = 0; x < shift.Length; x++) shift[x] = reference[x] - zeroTrace.m[x];
        var shifted = new List<int[]>();
        double[] Evaluate(int[] cellsOf, bool recordShift)
        {
            var played = Play(req, Values(cellsOf), r, metrics);
            if (played.ranges.Count == zeroTrace.ranges.Count && played.ranges.Skip(tailStart).SequenceEqual(refTail))
            {
                var e = new double[reference.Length];
                for (int x = 0; x < e.Length; x++) e[x] = played.m[x] + shift[x];
                if (recordShift && refTail.Count > 0) shifted.Add(cellsOf);
                return e;
            }
            return Node(req, Values(cellsOf), r, metrics, played);
        }
        var pick = new int[groups.Count];
        while (true)
        {
            double weight = 1;
            var unitClasses = new int[units];
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var (seq, w) = groupSets[gi][pick[gi]];
                weight *= w;
                for (int j = 0; j < seq.Length; j++) unitClasses[groups[gi][j]] = seq[j];
            }
            var cellsOf = Enumerable.Range(0, units).Select(i => unitClassCell[i][unitClasses[i]]).ToArray();
            var e = cellsOf.All(k => k == 0) ? reference : Evaluate(cellsOf, true);
            cache[KeyOfClasses(unitClasses)] = e;
            for (int x = 0; x < result.Length; x++) result[x] += weight * e[x];
            int d = groups.Count - 1;
            while (d >= 0 && ++pick[d] == groupSets[d].Count) { pick[d] = 0; d--; }
            if (d < 0) break;
        }
        for (int c = 0; c < ChecksPerRun && shifted.Count > 0; c++)
        {
            var cellsOf = shifted[checkRandom.Next(shifted.Count)];
            var full = Eval(cellsOf);
            FactorChecks++;
            var key = KeyOfClasses(Enumerable.Range(0, units).Select(i => unitClassOf[i][cellsOf[i]]).ToArray());
            if (!Equal(full, cache[key])) throw new FactorCheckFailed($"later picks interact with this run: {string.Join(",", full)} vs {string.Join(",", cache[key])}");
        }
        // A random sequence of raw outcomes must give the same result as its merged, sorted form.
        for (int c = 0; c < ChecksPerRun; c++)
        {
            var u = new int[units];
            for (int i = 0; i < units; i++) u[i] = checkRandom.Next(n);
            var key = KeyOfClasses(Enumerable.Range(0, units).Select(i => unitClassOf[i][u[i]]).ToArray());
            var e = Evaluate(u, false);
            FactorChecks++;
            if (!Equal(e, cache[key])) throw new FactorCheckFailed($"order matters: [{string.Join(",", u)}] {string.Join(",", e)} vs {string.Join(",", cache[key])}");
        }
        int classes = groups.Sum(g => classWeights[g[0]].Count);
        double multisets = combinations;
        MultisetRuns++;
        string statKey = $"{range} x{units} ({classes} classes)";
        RunStats.TryGetValue(statKey, out var st); RunStats[statKey] = (st.runs + 1, st.multisets + (long)multisets);
        return result;
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
            if (!same) throw new NotSupportedException(
                "The reel-stop requests changed between acts (reel strips are switching). This proof of concept " +
                "assumes the base game's reel strips are fixed.");
        }
        if (values.Length != requests.Count) throw new ArgumentException("One value per request is required.");
    }
}
