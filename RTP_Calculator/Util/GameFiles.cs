namespace RTP_Calculator.Util;

/// <summary>Loading game definitions: a .dat is read as is; a .agm is compiled to a temporary .dat (never modified).</summary>
public static class GameFiles
{
    public static bool IsGameFile(string path) =>
        path.EndsWith(".agm", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase);

    public static byte[] Load(string path, bool quiet = false)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Game file not found.", path);
        if (!path.EndsWith(".agm", StringComparison.OrdinalIgnoreCase)) return File.ReadAllBytes(path);
        string output = Path.Combine(Path.GetTempPath(), "RTP_Calculator_" + Path.GetFileNameWithoutExtension(path) + "_" + Guid.NewGuid().ToString("N") + ".dat");
        if (!quiet) Console.WriteLine($"Compiling {Path.GetFileName(path)} with the AGT compiler...");
        new AgtCompiler.Core.Compilation.AgmCompiler().CompileToDat(path, output, "RTP_Calculator");
        try { return File.ReadAllBytes(output); }
        finally { try { File.Delete(output); } catch (IOException) { } }
    }

    /// <summary>Number of paylines defined in the layout the game starts in (0 for pattern / ways games).</summary>
    public static uint BaseLayoutPaylines(byte[] dat)
    {
        var random = new AgtEngine.Core.Random.AGTRand(new Engine.ScriptedRandomAgent(1));
        var game = new AgtEngine.Core.Game.AGTGameLoader(random, new AgtEngine.Core.Misc.VariableListContext()).CreateGameInstance(dat);
        return (uint)game.CurrentLayout.PayLines.Count;
    }
}
