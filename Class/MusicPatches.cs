using StardewModdingAPI;
using StardewValley.GameData;
using System;


namespace DMMusic
{
    internal static class MusicPatches
    {
        public static bool ChangeMusicTrack_Prefix(ref string newTrackName, bool track_interruptable, MusicContext music_context)
        {
            var ctxInfo = MusicContextInfo.Get();
            string ctxText = ctxInfo.ToDebugString();

            // Handle empty: stop
            if (string.IsNullOrWhiteSpace(newTrackName))
            {
                MusicManager.StopAllTracks(disposeInstances: false);
                return true;
            }

            // Handle "none": let vanilla handle it, but don't force-stop replacements
            // (stops can cause stutter loops during transitions / focus changes)
            if (string.Equals(newTrackName, "none", StringComparison.OrdinalIgnoreCase))
                return true;

            bool replaced = MusicManager.TryPlayTrack(newTrackName, out string matchedKey, out string pickedPath);

            if (ModEntry.Config.EnableDebugLogging)
            {
                if (replaced)
                {
                    string sourceModName = MusicManager.GetSourceModName();

                    ModEntry.SMonitor.Log(
                        $"Track='{newTrackName}' => Mod='{sourceModName}' Key='{matchedKey}' File='{pickedPath}'",
                        LogLevel.Info
                    );

                }
                else
                {
                    ModEntry.SMonitor.Log(
                        $" Track='{newTrackName}' → VANILLA  ({ctxText})",
                        LogLevel.Info
                    );
                }

                if (ModEntry.Config.ShowSuggestionsAlways)
                {
                    var candidateKeys = MusicReplacementResolver.BuildCandidateKeys(newTrackName, ctxInfo);

                    ModEntry.SMonitor.Log("Want to replace this music? Add a line below into your musicReplacements.json:", LogLevel.Info);
                    foreach (var line in MusicReplacementResolver.BuildSingleLineSuggestions(candidateKeys))
                        ModEntry.SMonitor.Log($"   {line}", LogLevel.Info);
                }
            }

            // If no replacement matched, stop any custom audio so it doesn't overlap with vanilla.
            if (!replaced)
                MusicManager.StopAllTracks(disposeInstances: false);

            // If we replaced, block vanilla music; otherwise allow it.
            return !replaced;
        }
    }
}
