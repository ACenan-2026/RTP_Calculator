using AgtEngine.Core.Random;

namespace RTP_Calculator.Engine;

/// <summary>
/// The single point through which the win engine consumes randomness
/// (<see cref="IRandomNumberAgent"/> -> AGTRand.GetRandomNumber).
///
/// Two modes:
///  * Random   - behaves like the emulator's RNG (used for simulation / profiling).
///  * Scripted - returns a caller supplied prefix of values, then 0 for every further
///               draw, while recording the range and value of every draw made. The exact
///               enumerator uses the recorded trace to walk every branch of an act
///               depth-first.
/// </summary>
public sealed class ScriptedRandomAgent : IRandomNumberAgent
{
    private readonly System.Random random;
    private uint[] prefixValues = new uint[64];
    private uint[] prefixRanges = new uint[64];
    private int prefixLength;
    private int position;

    public ScriptedRandomAgent(int seed) => random = new System.Random(seed);

    public bool Scripted { get; private set; }

    /// <summary>Ranges of the draws made since the last Begin call (scripted mode only).</summary>
    public List<uint> Ranges { get; } = new List<uint>(128);

    /// <summary>Values returned for those draws (scripted mode only).</summary>
    public List<uint> Values { get; } = new List<uint>(128);

    /// <summary>Number of draws made since the last Begin call.</summary>
    public int DrawCount => position;

    /// <summary>Optional observer used by the profiler: receives the range of every draw.</summary>
    public Action<uint>? Observer { get; set; }

    public void BeginRandom()
    {
        Scripted = false; position = 0; Ranges.Clear(); Values.Clear();
    }

    /// <summary>
    /// Replay <paramref name="values"/> (which were drawn from <paramref name="ranges"/>) for the first
    /// draws, then return 0 for every later draw.
    /// </summary>
    public void BeginScripted(List<uint> values, List<uint> ranges, int length)
    {
        Scripted = true; position = 0; Ranges.Clear(); Values.Clear();
        prefixLength = length;
        if (prefixValues.Length < length)
        {
            prefixValues = new uint[Math.Max(length, prefixValues.Length * 2)];
            prefixRanges = new uint[prefixValues.Length];
        }
        for (int i = 0; i < length; i++) { prefixValues[i] = values[i]; prefixRanges[i] = ranges[i]; }
    }

    /// <summary>Uniform value in [0, range) from the internal RNG.</summary>
    public uint Next(uint range)
    {
        if (range <= 1) return 0;
        if (range <= int.MaxValue) return (uint)random.Next((int)range);
        ulong wide = ((ulong)(uint)random.Next() << 31) ^ (uint)random.Next();
        return (uint)(wide % range);
    }

    public uint QueryRandomNumber(uint lowInclusive, uint highExclusive)
    {
        uint range = unchecked(highExclusive - lowInclusive);
        if (range == 0) return lowInclusive;
        Observer?.Invoke(range);
        uint value;
        if (!Scripted)
        {
            value = Next(range);
        }
        else
        {
            if (position < prefixLength)
            {
                if (prefixRanges[position] != range)
                    throw new ScriptDivergenceException(
                        $"Draw #{position}: expected range {prefixRanges[position]} but the engine asked for {range}. " +
                        "The act did not replay deterministically from the restored state.");
                value = prefixValues[position];
            }
            else value = 0;
            Ranges.Add(range);
            Values.Add(value);
        }
        position++;
        return unchecked(lowInclusive + value);
    }
}

public sealed class ScriptDivergenceException : Exception
{
    public ScriptDivergenceException(string message) : base(message) { }
}
