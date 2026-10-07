#if NETFRAMEWORK
namespace System.Collections.Generic
{
    /// <summary>
    /// .NET Framework lacks KeyValuePair.Deconstruct (used by foreach (var (key, value) in dictionary)).
    /// Declared in System.Collections.Generic so it is in scope wherever dictionaries are.
    /// </summary>
    internal static class KeyValuePairDeconstruct
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }
}
#endif
