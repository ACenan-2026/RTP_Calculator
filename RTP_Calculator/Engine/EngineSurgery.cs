using AgtEngine.Core.Graphic;

namespace RTP_Calculator.Engine;

/// <summary>
/// Changes made only to analysis engine instances (never to files or the engine source): switching
/// individual replacements off so they make no draws and change nothing.
/// </summary>
public static class EngineSurgery
{
    public const string NeverSetName = "__rtp_never__";

    /// <summary>A replacement, identified by its trigger name and position in that trigger's list.</summary>
    public readonly struct ReplacementRef
    {
        public ReplacementRef(string trigger, int index, bool mapped) { Trigger = trigger; Index = index; Mapped = mapped; }
        public string Trigger { get; }
        public int Index { get; }
        public bool Mapped { get; }
    }

    /// <summary>
    /// Switch replacements off in the base layout of <paramref name="host"/> (live game and snapshot): their
    /// original set is pointed at a set that matches nothing (mapped replacements: their mapping info).
    /// </summary>
    public static void SwitchOff(GameHost host, IEnumerable<ReplacementRef> replacements)
    {
        var list = replacements.ToList();
        if (list.Count == 0) return;
        int baseIndex = host.BaseLayoutIndex;
        host.Mutate(game =>
        {
            var layout = game.Layouts.Entry(baseIndex).Value;
            if (!layout.SymbolSets.Contains(NeverSetName))
            {
                var never = new AGTSymbolSet(1); never[0u].Name.Assign(NeverSetName);
                layout.SymbolSets.Add(NeverSetName, never);
            }
            foreach (var r in list)
            {
                var array = layout.Replacements[r.Trigger];
                if (r.Index < 0 || r.Index >= array.Length) throw new InvalidOperationException("Could not locate a replacement to switch off.");
                if (r.Mapped) array[r.Index].ReplacementMappingSymbolsetName.Assign(NeverSetName);
                else array[r.Index].Original.Assign(NeverSetName);
            }
        });
    }
}
