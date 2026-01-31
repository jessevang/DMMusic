using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using System;
using System.Collections.Generic;

namespace DMMusic
{
    internal static class MusicPatches
    {
        private static readonly Random _rng = new();

        private static string? _activeEventId;
        private static bool _eventHasCustomReplacement;

        private static readonly Dictionary<string, int> _eventTrackPlayCounts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DateTime> _eventTrackLastRequestUtc = new(StringComparer.OrdinalIgnoreCase);

        private static string? _lastLoggedTrack;
        private static DateTime _lastLoggedTrackUtc = DateTime.MinValue;
        private static readonly TimeSpan LogDebounce = TimeSpan.FromMilliseconds(500);

        private static DateTime _lastNoneStopUtc = DateTime.MinValue;
        private static readonly TimeSpan NoneStopDebounce = TimeSpan.FromMilliseconds(400);

        private static readonly TimeSpan TrackPlayDebounceWindow = TimeSpan.FromMilliseconds(900);
        private static string? _lastRainBlockedEventId;
        private static DateTime _lastRainBlockedUtc = DateTime.MinValue;
        private static readonly TimeSpan RainBlockedLogDebounce = TimeSpan.FromSeconds(2);

        public static bool ChangeMusicTrack_Prefix(ref string newTrackName, bool track_interruptable, MusicContext music_context)
        {
            var ctxInfo = MusicContextInfo.Get();
            bool eventUp = ctxInfo.EventUp && !string.IsNullOrWhiteSpace(ctxInfo.EventId);


            if (!eventUp)
            {
                _activeEventId = null;
                _eventHasCustomReplacement = false;
                _eventTrackPlayCounts.Clear();
                _eventTrackLastRequestUtc.Clear();

                _lastRainBlockedEventId = null;
                _lastRainBlockedUtc = DateTime.MinValue;
            }
            else
            {

                if (!string.Equals(_activeEventId, ctxInfo.EventId, StringComparison.OrdinalIgnoreCase))
                {
                    _activeEventId = ctxInfo.EventId;
                    _eventHasCustomReplacement = false;
                    _eventTrackPlayCounts.Clear();
                    _eventTrackLastRequestUtc.Clear();

                    _lastRainBlockedEventId = null;
                    _lastRainBlockedUtc = DateTime.MinValue;
                }
            }

            if (string.IsNullOrWhiteSpace(newTrackName))
            {
                MusicManager.StopAllTracks(disposeInstances: false);
                _eventHasCustomReplacement = false;
                return true;
            }

            if (string.Equals(newTrackName, "none", StringComparison.OrdinalIgnoreCase))
            {
                var now = DateTime.UtcNow;
                if (_lastNoneStopUtc == DateTime.MinValue || (now - _lastNoneStopUtc) >= NoneStopDebounce)
                {
                    _lastNoneStopUtc = now;
                    MusicManager.StopAllTracks(disposeInstances: false);
                    _eventHasCustomReplacement = false;
                }
                return true;
            }

            // then block "rain" from hijacking the music during the event.
            if (eventUp && _eventHasCustomReplacement && string.Equals(newTrackName, "rain", StringComparison.OrdinalIgnoreCase))
            {
                MusicManager.StopVanillaAudioForContext(music_context);

                if (ModEntry.Config.EnableDebugLogging)
                    LogRainBlockedOnce(ctxInfo);

                return false; 
            }

            int? trackPlayIndex = ComputeTrackPlayIndex(ctxInfo, newTrackName);

            int pct = ModEntry.Config.VanillaInsteadPercent;
            if (pct < 0) pct = 0;
            if (pct > 100) pct = 100;

            if (pct > 0 && MusicManager.HasReplacement(newTrackName, trackPlayIndex, out string matchedKeyForRoll))
            {
                int roll = _rng.Next(1, 101);
                if (roll <= pct)
                {
                    MusicManager.StopAllTracks(disposeInstances: false);
                    _eventHasCustomReplacement = false;

                    if (ModEntry.Config.EnableDebugLogging)
                        ModEntry.SMonitor.Log($"Track='{newTrackName}' => VANILLA (random skip {pct}%) Key='{matchedKeyForRoll}'", LogLevel.Info);

                    return true;
                }
            }

            bool replaced = MusicManager.TryPlayTrack(newTrackName, trackPlayIndex, out string matchedKey, out string pickedPath);

            if (replaced)
            {
                MusicManager.StopVanillaAudioForContext(music_context);

                if (eventUp)
                    _eventHasCustomReplacement = true;
            }

            LogDebug(replaced, newTrackName, ctxInfo, matchedKey, pickedPath, trackPlayIndex);

            if (!replaced)
            {
                MusicManager.StopAllTracks(disposeInstances: false);
       
            }

            return !replaced;
        }

        private static void LogRainBlockedOnce(MusicContextInfo ctxInfo)
        {

            string eid = ctxInfo.EventId!;

            var now = DateTime.UtcNow;


            bool sameEvent = string.Equals(_lastRainBlockedEventId, eid, StringComparison.OrdinalIgnoreCase);
            bool withinDebounce = (_lastRainBlockedUtc != DateTime.MinValue) && (now - _lastRainBlockedUtc) < RainBlockedLogDebounce;

            if (sameEvent && withinDebounce)
                return;

            // The debounce here prevents spam even if called constantly.
            _lastRainBlockedEventId = eid;
            _lastRainBlockedUtc = now;

            ModEntry.SMonitor.Log(
                $"Track='rain' => BLOCKED (event has custom music) ({ctxInfo.ToDebugString()})",
                LogLevel.Info
            );
        }

        private static int? ComputeTrackPlayIndex(MusicContextInfo ctxInfo, string trackName)
        {
            if (!(ctxInfo.EventUp && !string.IsNullOrWhiteSpace(ctxInfo.EventId)))
                return null;

            var now = DateTime.UtcNow;

            if (_eventTrackLastRequestUtc.TryGetValue(trackName, out var last) &&
                (now - last) < TrackPlayDebounceWindow)
            {
                if (_eventTrackPlayCounts.TryGetValue(trackName, out int existing))
                    return existing;

                _eventTrackPlayCounts[trackName] = 1;
                return 1;
            }

            _eventTrackLastRequestUtc[trackName] = now;

            if (!_eventTrackPlayCounts.TryGetValue(trackName, out int count))
                count = 0;

            count++;
            _eventTrackPlayCounts[trackName] = count;

            return count;
        }

        private static void LogDebug(bool replaced, string track, MusicContextInfo ctxInfo, string matchedKey, string pickedPath, int? trackPlayIndex)
        {
            if (!ModEntry.Config.EnableDebugLogging)
                return;

            var now = DateTime.UtcNow;
            if (string.Equals(_lastLoggedTrack, track, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastLoggedTrackUtc) < LogDebounce)
            {
                return;
            }

            _lastLoggedTrack = track;
            _lastLoggedTrackUtc = now;

            if (replaced)
            {
                string sourceModName = MusicManager.GetSourceModName();
                ModEntry.SMonitor.Log($"Track='{track}' => Mod='{sourceModName}' Key='{matchedKey}' File='{pickedPath}'", LogLevel.Info);
            }
            else
            {
                ModEntry.SMonitor.Log($"Track='{track}' => VANILLA ({ctxInfo.ToDebugString()})", LogLevel.Info);
            }

            if (!ModEntry.Config.ShowSuggestionsAlways)
                return;

            var candidateKeys = MusicReplacementResolver.BuildCandidateKeys(track, ctxInfo, trackPlayIndex);

            ModEntry.SMonitor.Log("Want to replace this music? Add a line below into your musicReplacements.json:", LogLevel.Info);
            foreach (var line in MusicReplacementResolver.BuildSingleLineSuggestions(candidateKeys))
                ModEntry.SMonitor.Log($"   {line}", LogLevel.Info);
        }
    }
}
