using System;
using System.Collections.Generic;

namespace DMMusic
{
    internal static class MusicReplacementResolver
    {
        // Priority: most-specific -> least-specific
        public static List<string> BuildCandidateKeys(string trackId, MusicContextInfo ctx)
        {
            var list = new List<string>(capacity: 6);

            if (ctx.EventUp && ctx.EventId != null)
            {
                list.Add($"{trackId}|Location={ctx.Location}|EventId={ctx.EventId}");
                list.Add($"{trackId}|EventId={ctx.EventId}");
            }

            if (ctx.EventUp)
            {
                list.Add($"{trackId}|Location={ctx.Location}|EventUp");
                list.Add($"{trackId}|EventUp");
            }

            list.Add($"{trackId}|Location={ctx.Location}");
            list.Add(trackId);

            return list;
        }

        public static bool TryResolveKey(
            Dictionary<string, List<MusicPath>> map,
            string trackId,
            MusicContextInfo ctx,
            out string matchedKey,
            out List<MusicPath> paths,
            out List<string> candidateKeys
        )
        {
            matchedKey = "";
            paths = new List<MusicPath>();
            candidateKeys = BuildCandidateKeys(trackId, ctx);

            foreach (var key in candidateKeys)
            {
                if (map.TryGetValue(key, out var list) && list != null && list.Count > 0)
                {
                    matchedKey = key;
                    paths = list;
                    return true;
                }
            }

            return false;
        }

        // Suggest using array form so it's ready for shuffle (even if you only put 1 entry)
        public static string BuildSuggestionLines(IEnumerable<string> candidateKeys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();

            foreach (var k in candidateKeys)
            {
                if (!seen.Add(k))
                    continue;

                lines.Add($"  \"{k}\": [");
                lines.Add($"    \"\"");
                lines.Add($"  ],");
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static IEnumerable<string> BuildSingleLineSuggestions(IEnumerable<string> keys)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in keys)
            {
                if (!seen.Add(k))
                    continue;

                yield return $"\"{k}\": [ \"\" ],";
            }
        }
    }
}
