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
        private static readonly TimeSpan TrackPlayDebounceWindow = TimeSpan.FromMilliseconds(900);

        private static string? _lastRainBlockedEventId;
        private static DateTime _lastRainBlockedUtc = DateTime.MinValue;
        private static readonly TimeSpan RainBlockedLogDebounce = TimeSpan.FromSeconds(2);

        private static DateTime _lastNoneStopUtc = DateTime.MinValue;
        private static readonly TimeSpan NoneStopDebounce = TimeSpan.FromMilliseconds(400);

        private static readonly Dictionary<string, DateTime> _lastLogUtcByKey = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan LogCooldownDefault = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan LogCooldownAmbient = TimeSpan.FromSeconds(10);

        private static string? _decisionKeyActive;
        private static bool _decisionUseVanilla;

        private static string? _lastRequestedSessionKey;

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

            string sessionKey = eventUp
                ? $"EID={ctxInfo.EventId ?? "Unknown"}||TRACK={newTrackName}"
                : $"TRACK={newTrackName}";

            if (!string.Equals(_lastRequestedSessionKey, sessionKey, StringComparison.OrdinalIgnoreCase))
            {
                _lastRequestedSessionKey = sessionKey;
                _decisionKeyActive = null;
            }

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
                string decisionKey = sessionKey;

                if (!string.Equals(_decisionKeyActive, decisionKey, StringComparison.OrdinalIgnoreCase))
                {
                    _decisionKeyActive = decisionKey;
                    int roll = _rng.Next(1, 101);
                    _decisionUseVanilla = roll <= pct;
                }

                if (_decisionUseVanilla)
                {
                    MusicManager.StopAllTracks(disposeInstances: false);
                    _eventHasCustomReplacement = false;

                    LogDebugState(
                        replaced: false,
                        track: newTrackName,
                        ctxInfo: ctxInfo,
                        matchedKey: matchedKeyForRoll,
                        pickedPath: "",
                        trackPlayIndex: trackPlayIndex,
                        note: $"(random skip {pct}%)"
                    );

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

            LogDebugState(replaced, newTrackName, ctxInfo, matchedKey, pickedPath, trackPlayIndex);

            if (!replaced)
            {
                MusicManager.StopAllTracks(disposeInstances: false);
                _eventHasCustomReplacement = false;
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

        private static void LogDebugState(bool replaced, string track, MusicContextInfo ctxInfo, string matchedKey, string pickedPath, int? trackPlayIndex, string? note = null)
        {
            if (!ModEntry.Config.EnableDebugLogging)
                return;

            string baseKey = $"{(replaced ? "R" : "V")}||{track}||{matchedKey}||{pickedPath}||{note ?? ""}";
            if (ctxInfo.EventUp)
                baseKey += $"||EventId={ctxInfo.EventId ?? "Unknown"}||TrackPlay={(trackPlayIndex.HasValue ? trackPlayIndex.Value.ToString() : "-")}";

            var now = DateTime.UtcNow;
            var cooldown = IsAmbientLike(track) ? LogCooldownAmbient : LogCooldownDefault;

            if (_lastLogUtcByKey.TryGetValue(baseKey, out var last) && (now - last) < cooldown)
                return;

            _lastLogUtcByKey[baseKey] = now;

            if (replaced)
            {
                string sourceModName = MusicManager.GetSourceModName();
                if (!string.IsNullOrEmpty(note))
                    ModEntry.SMonitor.Log($"Track='{track}' => Mod='{sourceModName}' Key='{matchedKey}' File='{pickedPath}' {note}", LogLevel.Info);
                else
                    ModEntry.SMonitor.Log($"Track='{track}' => Mod='{sourceModName}' Key='{matchedKey}' File='{pickedPath}'", LogLevel.Info);
            }
            else
            {
                if (!string.IsNullOrEmpty(note))
                    ModEntry.SMonitor.Log($"Track='{track}' => VANILLA {note} ({ctxInfo.ToDebugString()})", LogLevel.Info);
                else
                    ModEntry.SMonitor.Log($"Track='{track}' => VANILLA ({ctxInfo.ToDebugString()})", LogLevel.Info);
            }

            if (!ModEntry.Config.ShowSuggestionsAlways)
                return;

            string sugKey = $"SUG||{track}||CTX={ctxInfo.Location ?? "Unknown"}||EventUp={ctxInfo.EventUp}";
            if (ctxInfo.EventUp)
                sugKey += $"||EventId={ctxInfo.EventId ?? "Unknown"}";
            sugKey += $"||TrackPlay={(trackPlayIndex.HasValue ? trackPlayIndex.Value.ToString() : "-")}";

            var sugCooldown = IsAmbientLike(track) ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(15);

            if (_lastLogUtcByKey.TryGetValue(sugKey, out var lastSug) && (now - lastSug) < sugCooldown)
                return;

            _lastLogUtcByKey[sugKey] = now;

            var candidateKeys = MusicReplacementResolver.BuildCandidateKeys(track, ctxInfo, trackPlayIndex);

            var lines = MusicReplacementResolver.BuildSingleLineSuggestions(candidateKeys);
            if (lines == null)
                return;

            string block = "Want to replace this music? Add a line below into your musicReplacements.json:";
            foreach (var line in lines)
                block += "\n                  " + line;

            block += "\n----------------------------------------------------------------------------------";

            ModEntry.SMonitor.Log(block, LogLevel.Info);
        }




        private static bool IsAmbientLike(string track)
        {
            if (string.IsNullOrWhiteSpace(track))
                return false;

            return track.IndexOf("ambient", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
