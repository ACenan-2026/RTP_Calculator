using AgtEngine.Core.Graphic;
using AgtEngine.Core.ReelGame;

namespace RTP_Calculator.Enumeration;

/// <summary>
/// Splits the value range of a random draw into "cells": runs of consecutive values that the
/// engine maps to the same outcome. A weighted pick such as
/// <c>set.ActualSymbolIndexInExtendedSet(random.GetRandomNumber(set.NumSymbolsBasedOnExtension))</c>
/// over a table with weights {60,25,10,4,1} has range 100 but only 5 distinct outcomes, so the
/// enumerator needs to try 5 values (each weighted by its cell width), not 100.
///
/// Cell boundaries are collected from every weighted symbol set and every ReelGroupSet in the game. Because several sets
/// (or other consumers) can share the same range, the cells for a range are the union of all
/// boundaries registered for it, which is a common refinement and therefore safe for every set
/// with that range. Small ranges are always enumerated value-by-value because they are typically
/// position or count picks that are not backed by a weighted set.
/// </summary>
public sealed class DrawPartition
{
    private readonly Dictionary<uint, SortedSet<uint>> boundaries = new Dictionary<uint, SortedSet<uint>>();
    private readonly HashSet<uint> fullRanges = new HashSet<uint>();
    private readonly Dictionary<uint, uint[]> cache = new Dictionary<uint, uint[]>();
    private readonly object gate = new object();
    // One-entry cache. A DrawPartition is used by a single worker thread, so no locking is needed here.
    private uint lastRange;
    private uint[]? lastCells;

    /// <summary>Ranges up to this size are always enumerated value-by-value.</summary>
    public uint AlwaysFullUpTo { get; }

    /// <summary>Unregistered ranges up to this size are enumerated value-by-value; larger ones are rejected.</summary>
    public uint MaxUnregisteredRange { get; }

    public List<string> Warnings { get; } = new List<string>();

    public DrawPartition(AGTReelGame game, uint alwaysFullUpTo = 64, uint maxUnregisteredRange = 100_000)
    {
        AlwaysFullUpTo = alwaysFullUpTo;
        MaxUnregisteredRange = maxUnregisteredRange;
        for (int l = 0; l < game.Layouts.Count; l++)
        {
            var layout = game.Layouts.Entry(l).Value;
            foreach (var set in layout.SymbolSets) Register(set.Value);
            foreach (var reel in layout.Reels) Register(reel.Value.Symbols);
            foreach (var set in layout.ReelGroupSets) Register(set.Value);
        }
    }

    /// <summary>
    /// A ReelGroupSet draws once from [0, ProbabilityRange) and enables the first group whose cumulative
    /// weight exceeds the draw (AGTLayout.EvaluateReelGroupSwitch), so its outcomes are cut at the
    /// cumulative weights. A draw past the total weight (if the range is larger) is a cell of its own.
    /// </summary>
    private void Register(AgtEngine.Core.ReelGame.AGTReelGroupSet set)
    {
        uint range = set.ProbabilityRange;
        if (range <= 1 || set.ReelGroups.Count <= 1) return;
        if (!boundaries.TryGetValue(range, out var cuts)) boundaries[range] = cuts = new SortedSet<uint> { 0 };
        uint sum = 0;
        foreach (var group in set.ReelGroups)
        {
            sum = unchecked(sum + group.Value);
            if (sum < range) cuts.Add(sum);
        }
    }

    private void Register(AGTSymbolSet set)
    {
        uint range = set.NumSymbolsBasedOnExtension;
        if (range <= 1) return;
        if (!set.ExtendedSymbolsPresent)
        {
            // Uniform pick over the set's entries: every value is its own outcome.
            fullRanges.Add(range);
            return;
        }
        if (!boundaries.TryGetValue(range, out var cuts)) boundaries[range] = cuts = new SortedSet<uint> { 0 };
        uint sum = 0;
        for (uint i = 0; i < set.NumSymbols(); i++)
        {
            sum = unchecked(sum + set[i].Statistics.Frequency);
            if (sum < range) cuts.Add(sum);
        }
    }

    /// <summary>Sorted start values of every cell for <paramref name="range"/>. The first is always 0.</summary>
    public uint[] Cells(uint range)
    {
        // Fast path without locking: the same range is looked up for every draw of every path.
        if (lastRange == range && lastCells is not null) return lastCells;
        lock (gate)
        {
            if (cache.TryGetValue(range, out var cached)) { lastCells = cached; lastRange = range; return cached; }
            uint[] cells;
            if (range <= AlwaysFullUpTo || fullRanges.Contains(range)) cells = Full(range);
            else if (boundaries.TryGetValue(range, out var cuts)) cells = cuts.ToArray();
            else if (range <= MaxUnregisteredRange)
            {
                Warnings.Add($"Range {range} is not backed by a weighted symbol set; enumerating all {range} values.");
                cells = Full(range);
            }
            else throw new NotSupportedException(
                $"The engine drew from range {range:N0}, which is not backed by any weighted symbol set. " +
                "Enumerating it value-by-value is not practical; this act needs a dedicated strategy.");
            cache[range] = cells;
            lastCells = cells; lastRange = range;
            return cells;
        }
    }

    private static uint[] Full(uint range)
    {
        var cells = new uint[range];
        for (uint i = 0; i < range; i++) cells[i] = i;
        return cells;
    }

    /// <summary>Start of the cell after the one starting at <paramref name="start"/>, or <paramref name="range"/> if it is the last.</summary>
    public uint NextCellStart(uint range, uint start)
    {
        var cells = Cells(range);
        int index = Array.BinarySearch(cells, start);
        if (index < 0) throw new InvalidOperationException($"Value {start} is not a cell start for range {range}.");
        return index + 1 < cells.Length ? cells[index + 1] : range;
    }
}
