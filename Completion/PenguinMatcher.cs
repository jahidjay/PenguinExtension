using System;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// Visual Assist-style matching of what was typed against a candidate name. Lower score = better match:
    ///   0 exact · 1 case-sensitive prefix · 2 prefix · 3 acronym (cmc → CharacterMovementComponent)
    ///   · 4 camel-hump (GetActLoc) · 5 substring · -1 no match.
    /// Unreal type prefixes are optional for humps: "cmc" and "CharMov" both find UCharacterMovementComponent.
    /// </summary>
    internal static class PenguinMatcher
    {
        public const int NoMatch = -1;

        public static int Score(string typed, string name)
        {
            if (string.IsNullOrEmpty(typed)) return 2;
            if (string.IsNullOrEmpty(name) || typed.Length > name.Length + 1) return NoMatch;
            if (string.Equals(typed, name, StringComparison.Ordinal)) return 0;
            if (name.StartsWith(typed, StringComparison.Ordinal)) return 1;
            if (name.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 2;
            if (typed.Length >= 2 && Acronym(name).StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 3;
            if (Humps(typed, name) || HasUnrealPrefix(name) && Humps(typed, name.Substring(1))) return 4;
            if (typed.Length >= 3 && name.IndexOf(typed, StringComparison.OrdinalIgnoreCase) >= 0) return 5;
            return NoMatch;
        }

        /// <summary>Hump initials without the Unreal type prefix: UCharacterMovementComponent → "CMC", Get_Value2 → "GV2".</summary>
        public static string Acronym(string name)
        {
            var sb = new System.Text.StringBuilder();
            int start = HasUnrealPrefix(name) ? 1 : 0;
            for (int i = start; i < name.Length; i++)
            {
                char c = name[i];
                if (i == start || char.IsUpper(c) && !char.IsUpper(name[i - 1]) || name[i - 1] == '_' && c != '_' || char.IsDigit(c) && !char.IsDigit(name[i - 1]))
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>AActor, UObject, FVector, EState, IInterface, TArray, SWidget → the first letter is a type prefix.</summary>
        private static bool HasUnrealPrefix(string name) =>
            name.Length > 2 && "AUFEITS".IndexOf(name[0]) >= 0 && char.IsUpper(name[1]);

        /// <summary>
        /// Every typed character matches either the start of a hump (uppercase letter, digit run, or after '_')
        /// or continues the previous match. The first typed character must match the first name character.
        /// Iterative with bounded backtracking (names are short).
        /// </summary>
        public static bool Humps(string typed, string name)
        {
            if (typed.Length == 0) return true;
            if (char.ToLowerInvariant(typed[0]) != char.ToLowerInvariant(name[0])) return false;
            return Match(typed, 1, name, 1, 0);
        }

        private static bool Match(string p, int pi, string n, int ni, int depth)
        {
            if (pi == p.Length) return true;
            if (depth > 64) return false;
            char want = char.ToLowerInvariant(p[pi]);
            // Continue the current hump
            if (ni < n.Length && char.ToLowerInvariant(n[ni]) == want && Match(p, pi + 1, n, ni + 1, depth + 1)) return true;
            // Or jump to a later hump start
            for (int k = ni + 1; k < n.Length; k++)
            {
                bool hump = char.IsUpper(n[k]) || n[k - 1] == '_' || char.IsDigit(n[k]) && !char.IsDigit(n[k - 1]);
                if (hump && char.ToLowerInvariant(n[k]) == want && Match(p, pi + 1, n, k + 1, depth + 1)) return true;
            }
            return false;
        }
    }
}
