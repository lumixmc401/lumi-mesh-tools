using System.Text.RegularExpressions;

namespace LumiMeshTools.Editor
{
    /// <summary>
    /// Flips the left/right marker in a name. Shared by bone remapping and blend shape pairing,
    /// so both agree on what counts as a mirrored pair.
    /// </summary>
    public static class NameSymmetry
    {
        static readonly string[][] WordPairs =
        {
            new[] { "Left", "Right" },
            new[] { "left", "right" },
            new[] { "LEFT", "RIGHT" },
        };

        // A lone L or R that is fenced off by a separator or the ends of the string, so that
        // "Clothes" or "Skirt" are never mistaken for side markers.
        static readonly Regex LoneSideLetter =
            new Regex(@"(?<=^|[_.\- ])[LlRr](?=$|[_.\- ])", RegexOptions.Compiled);

        /// <summary>Returns the opposite-side name, or false when the name carries no side marker.</summary>
        public static bool TryFlip(string name, out string flipped)
        {
            flipped = null;
            if (string.IsNullOrEmpty(name)) return false;

            foreach (var pair in WordPairs)
            {
                if (name.Contains(pair[0])) { flipped = name.Replace(pair[0], pair[1]); return true; }
                if (name.Contains(pair[1])) { flipped = name.Replace(pair[1], pair[0]); return true; }
            }

            var matches = LoneSideLetter.Matches(name);
            if (matches.Count == 0) return false;

            // The last marker wins: "L_Hand_L_end" is keyed by its trailing side, and names that
            // only carry one marker are unaffected by the choice.
            int at = matches[matches.Count - 1].Index;
            char c = name[at];
            char swapped = c == 'L' ? 'R' : c == 'R' ? 'L' : c == 'l' ? 'r' : 'l';
            flipped = name.Substring(0, at) + swapped + name.Substring(at + 1);
            return true;
        }
    }
}
