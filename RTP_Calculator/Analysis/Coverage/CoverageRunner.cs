using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RTP_Calculator.Analysis.Analytic;
using RTP_Calculator.Engine;
using RTP_Calculator.Util;

namespace RTP_Calculator.Analysis.Coverage;

/// <summary>
/// Runs the construct scanner and the analytic base-game calculation over every game file in a folder,
/// and reports which games the analytic mode covers and what blocks the rest. Identical files are
/// processed once. Results are written to a CSV so coverage can be tracked as evaluators are added.
/// </summary>
public sealed class CoverageRunner
{
    public sealed class Row
    {
        public string File { get; set; } = "";
        public string Duplicates { get; set; } = "";
        public string Game { get; set; } = "";
        public uint Lines { get; set; }
        public string Constructs { get; set; } = "";
        public string Replacements { get; set; } = "";
        public string Status { get; set; } = "";
        public string Blocker { get; set; } = "";
        public double LineRtp { get; set; } = double.NaN;
        public double NonLineRtp { get; set; } = double.NaN;
        public double HitRate { get; set; } = double.NaN;
        public string Verify { get; set; } = "";
        public double Seconds { get; set; }
    }

    public int VerifySamples { get; set; } = 30;
    public bool ScanOnly { get; set; }
    public uint Bet { get; set; } = 1;
    public uint? Lines { get; set; }
    public string? Filter { get; set; }
    public (int from, int count)? Range { get; set; }
    /// <summary>When set, the CSV is rewritten after every game so partial runs keep their results.</summary>
    public string? CsvPath { get; set; }

    public List<Row> Run(string folder)
    {
        var files = Directory.GetFiles(folder).Where(GameFiles.IsGameFile).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        // Group byte-identical files.
        var groups = new List<List<string>>();
        var byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using (var sha = SHA256.Create())
            foreach (var f in files)
            {
                string h = Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f)));
                if (!byHash.TryGetValue(h, out var g)) { byHash[h] = g = new List<string>(); groups.Add(g); }
                g.Add(f);
            }
        if (Filter is not null) groups = groups.Where(g => g.Any(f => Path.GetFileName(f).IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        Console.WriteLine($"  {files.Count} game files, {byHash.Count} distinct" + (Filter is null ? "" : $", {groups.Count} matching '{Filter}'"));
        if (Range is (int from, int count)) groups = groups.Skip(from).Take(count).ToList();

        var rows = new List<Row>();
        foreach (var group in groups)
        {
            var row = new Row
            {
                File = Path.GetFileName(group[0]),
                Duplicates = string.Join(" ", group.Skip(1).Select(Path.GetFileName))
            };
            var sw = Stopwatch.StartNew();
            try { Process(group[0], row); }
            catch (Exception e) { row.Status = "ERROR"; row.Blocker = Flatten(e); }
            row.Seconds = sw.Elapsed.TotalSeconds;
            rows.Add(row);
            if (CsvPath is not null) WriteCsv(CsvPath, rows);
            Console.WriteLine($"  {row.Status,-12} {row.File,-42} {Short(row.Blocker, 90)}{(row.Status is "ANALYTIC" or "LINES-ONLY" ? $" lines {row.LineRtp:F6} other {row.NonLineRtp:F6} hit 1/{row.HitRate:F2} {row.Verify}" : "")}  ({row.Seconds:F0}s)");
        }
        return rows;
    }

    private void Process(string path, Row row)
    {
        byte[] dat;
        try { dat = GameFiles.Load(path, quiet: true); }
        catch (Exception e) { row.Status = "COMPILE"; row.Blocker = Flatten(e); return; }
        uint lines = Lines ?? Math.Max(1, GameFiles.BaseLayoutPaylines(dat));
        row.Lines = lines;
        var host = GameHost.Load(dat, Bet, lines, 5);
        var scan = ConstructScanner.Scan(host);
        row.Game = scan.Game;
        row.Constructs = string.Join(" ", scan.Constructs.OrderBy(c => c, StringComparer.Ordinal));
        row.Replacements = string.Join(" | ", scan.Replacements.Select(r => r.ToString()));
        if (ScanOnly) { row.Status = "SCANNED"; return; }
        try
        {
            var analytic = new AnalyticBaseGame(dat, Bet, lines) { VerifySamples = VerifySamples, OraclePathLimit = 20_000 };
            var r = analytic.Run(verify: VerifySamples > 0);
            row.LineRtp = r.LineRtp; row.NonLineRtp = r.NonLineRtp; row.HitRate = 1 / r.TriggerProbability;
            if (r.VerifiedLines > 0)
            {
                row.Verify = $"verify {r.VerifiedLines - r.LineMismatches}/{r.VerifiedLines} lines, {r.VerifiedScatter - r.ScatterMismatches}/{r.VerifiedScatter} other [{r.NonLineMethod}, {r.Scenarios} scen.]";
                bool ok = r.LineMismatches == 0 && r.ScatterMismatches == 0;
                row.Status = !ok ? "VERIFY-FAIL" : r.LinesOnly ? "LINES-ONLY" : "ANALYTIC";
                if (ok && r.LinesOnly) row.Blocker = "non-line: " + r.NonLineError;
                if (!ok) row.Blocker = string.Join(" / ", r.MismatchExamples.Take(2));
            }
            else { row.Status = r.LinesOnly ? "LINES-ONLY" : "ANALYTIC"; if (r.LinesOnly) row.Blocker = "non-line: " + r.NonLineError; }
        }
        catch (NotSupportedException e) { row.Status = "UNSUPPORTED"; row.Blocker = e.Message; }
    }

    private static string Flatten(Exception e)
    {
        var sb = new StringBuilder(e.Message);
        for (var inner = e.InnerException; inner is not null; inner = inner.InnerException) sb.Append(" <- ").Append(inner.Message);
        return sb.ToString().Replace('\n', ' ').Replace('\r', ' ');
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 3) + "...";

    public static void WriteCsv(string path, IEnumerable<Row> rows)
    {
        string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
        string D(double d) => double.IsNaN(d) ? "" : d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.AppendLine("file,duplicates,game,lines,status,blocker,line_rtp,other_rtp,hit_rate,verify,constructs,replacements,seconds");
        foreach (var r in rows)
            sb.AppendLine(string.Join(",", Q(r.File), Q(r.Duplicates), Q(r.Game), r.Lines, Q(r.Status), Q(r.Blocker), D(r.LineRtp), D(r.NonLineRtp), D(r.HitRate), Q(r.Verify), Q(r.Constructs), Q(r.Replacements), r.Seconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)));
        File.WriteAllText(path, sb.ToString());
    }

    public static void PrintSummary(List<Row> rows)
    {
        Console.WriteLine();
        Console.WriteLine("  Status summary:");
        foreach (var g in rows.GroupBy(r => r.Status).OrderByDescending(g => g.Count())) Console.WriteLine($"    {g.Count(),4}  {g.Key}");
        var blockers = rows.Where(r => r.Status is "UNSUPPORTED" or "ERROR")
            .GroupBy(r => Normalise(r.Blocker)).OrderByDescending(g => g.Count()).Take(15).ToList();
        if (blockers.Count > 0)
        {
            Console.WriteLine("  Most common blockers:");
            foreach (var g in blockers) Console.WriteLine($"    {g.Count(),4}  {Short(g.Key, 110)}");
        }
        var constructs = rows.SelectMany(r => r.Constructs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)).GroupBy(c => c).OrderByDescending(g => g.Count());
        Console.WriteLine("  Constructs (games using each):");
        foreach (var g in constructs) Console.WriteLine($"    {g.Count(),4}  {g.Key}");
    }

    /// <summary>Strip names out of a blocker message so similar blockers group together.</summary>
    private static string Normalise(string blocker) =>
        System.Text.RegularExpressions.Regex.Replace(blocker, "'[^']*'", "'…'").Replace("  ", " ");
}
