using System;
using System.Collections.Generic;
using System.Text;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Acronym ("camel-hump") keys for identifiers: <c>UCharacterMovementComponent</c> → <c>UCMC</c> and,
    /// without the Unreal type prefix, <c>CMC</c>. A sorted key list gives O(log n + k) acronym lookup.
    /// Not thread-safe: callers synchronize (CacheService uses its ReaderWriterLockSlim).
    /// </summary>
    internal sealed class AcronymIndex
    {
        private readonly List<KeyValuePair<string, string>> _entries = new List<KeyValuePair<string, string>>();
        private static readonly Comparer<KeyValuePair<string, string>> ByKey =
            Comparer<KeyValuePair<string, string>>.Create((a, b) =>
            {
                int c = string.CompareOrdinal(a.Key, b.Key);
                return c != 0 ? c : string.CompareOrdinal(a.Value, b.Value);
            });

        /// <summary>Upper-case acronym of the humps in <paramref name="name"/> (letters starting a hump, plus digits).</summary>
        public static string Acronym(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (c == '_') continue;
                bool hump = i == 0 || name[i - 1] == '_' ||
                            char.IsUpper(c) && (!char.IsUpper(name[i - 1]) || i + 1 < name.Length && char.IsLower(name[i + 1])) ||
                            char.IsDigit(c) && !char.IsDigit(name[i - 1]);
                if (hump) sb.Append(char.ToUpperInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>True when <paramref name="name"/> follows UE naming (A/U/F/S/I/E/T + uppercase).</summary>
        public static bool HasUnrealPrefix(string name) =>
            name != null && name.Length > 1 && "AUFSIET".IndexOf(name[0]) >= 0 && char.IsUpper(name[1]);

        /// <summary>Does <paramref name="term"/> match the start of the name's acronym (with or without UE prefix)?</summary>
        public static bool Matches(string term, string name)
        {
            if (string.IsNullOrEmpty(term) || term.Length < 2) return false;
            var stem = System.IO.Path.GetFileNameWithoutExtension(name);
            var acronym = Acronym(stem);
            if (acronym.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return true;
            return HasUnrealPrefix(stem) && acronym.Length > 1 && acronym.Substring(1).StartsWith(term, StringComparison.OrdinalIgnoreCase);
        }

        public void Clear() => _entries.Clear();

        public void Add(string name)
        {
            foreach (var key in Keys(name)) Insert(new KeyValuePair<string, string>(key, name));
        }

        public void Remove(string name)
        {
            foreach (var key in Keys(name))
            {
                int idx = _entries.BinarySearch(new KeyValuePair<string, string>(key, name), ByKey);
                if (idx >= 0) _entries.RemoveAt(idx);
            }
        }

        /// <summary>Bulk load; faster than repeated <see cref="Add"/>.</summary>
        public void Load(IEnumerable<string> names)
        {
            _entries.Clear();
            foreach (var name in names)
                foreach (var key in Keys(name)) _entries.Add(new KeyValuePair<string, string>(key, name));
            _entries.Sort(ByKey);
        }

        /// <summary>Names whose acronym starts with <paramref name="term"/> (case-insensitive).</summary>
        public IEnumerable<string> Find(string term, int max)
        {
            if (string.IsNullOrEmpty(term) || term.Length < 2) yield break;
            var key = term.ToUpperInvariant();
            int idx = _entries.BinarySearch(new KeyValuePair<string, string>(key, ""), ByKey);
            if (idx < 0) idx = ~idx;
            int count = 0;
            for (; idx < _entries.Count && count < max && _entries[idx].Key.StartsWith(key, StringComparison.Ordinal); idx++, count++)
                yield return _entries[idx].Value;
        }

        private static IEnumerable<string> Keys(string name)
        {
            var acronym = Acronym(name);
            if (acronym.Length < 2) yield break;
            yield return acronym;
            if (HasUnrealPrefix(name) && acronym.Length > 2) yield return acronym.Substring(1);
        }

        private void Insert(KeyValuePair<string, string> entry)
        {
            int idx = _entries.BinarySearch(entry, ByKey);
            if (idx < 0) _entries.Insert(~idx, entry);
        }
    }
}
