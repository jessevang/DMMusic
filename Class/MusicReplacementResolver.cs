using System;
using System.Collections.Generic;

namespace DMMusic
{
    internal static class MusicReplacementResolver
    {

        public static List<string> BuildCandidateKeys(string trackId, MusicContextInfo ctx, int? trackPlayIndex)
        {
            var list = new List<string>(capacity: 10);


            if (ctx.EventUp && ctx.EventId != null && trackPlayIndex.HasValue)
            {
                list.Add($"{trackId}|Location={ctx.Location}|EventId={ctx.EventId}|TrackPlay={trackPlayIndex.Value}");
                list.Add($"{trackId}|EventId={ctx.EventId}|TrackPlay={trackPlayIndex.Value}");
            }


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

        // Backwards-compatible wrapper (existing calls)
        public static List<string> BuildCandidateKeys(string trackId, MusicContextInfo ctx)
            => BuildCandidateKeys(trackId, ctx, trackPlayIndex: null);

        public static bool TryResolveKey(
            Dictionary<string, List<MusicPath>> map,
            string trackId,
            MusicContextInfo ctx,
            List<string> candidateKeys,
            out string matchedKey,
            out List<MusicPath> paths
        )
        {
            matchedKey = "";
            paths = new List<MusicPath>();

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

        // Existing signature preserved (so older calls still compile)
        public static bool TryResolveKey(
            Dictionary<string, List<MusicPath>> map,
            string trackId,
            MusicContextInfo ctx,
            out string matchedKey,
            out List<MusicPath> paths,
            out List<string> candidateKeys
        )
        {
            candidateKeys = BuildCandidateKeys(trackId, ctx, trackPlayIndex: null);
            return TryResolveKey(map, trackId, ctx, candidateKeys, out matchedKey, out paths);
        }


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
