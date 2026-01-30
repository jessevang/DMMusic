using DMMusic.Lib.NVorbis;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace DMMusic
{
    internal static class MusicManager
    {
        // NOTE: keys can be:
        // Track
        // Track|Location=Temp
        // Track|EventUp
        // Track|EventId=123456
        // Track|Location=Mine|EventUp
        // Track|Location=Town|EventId=123456
        private static Dictionary<string, string> _replacementMap = new(StringComparer.OrdinalIgnoreCase);

        // cache instances by *matched config key* so different conditions can have different audio
        private static readonly Dictionary<string, SoundEffectInstance> _instances = new(StringComparer.OrdinalIgnoreCase);

        private static float? _vanillaMusicVolume;
        private static string? _currentReplacementKey; // matched config key currently playing

        // ---------------------------
        // audio load (wav/ogg)
        // ---------------------------
        private static SoundEffect LoadSoundEffectFromFile(string fullPath)
        {
            string ext = Path.GetExtension(fullPath).ToLowerInvariant();

            if (ext == ".wav")
            {
                using var s = File.OpenRead(fullPath);
                return SoundEffect.FromStream(s);
            }

            if (ext == ".ogg")
                return LoadOggSoundEffectNVorbis(fullPath);

            throw new NotSupportedException($"DMMusic: Unsupported audio type '{ext}' for '{fullPath}'. Use .wav or .ogg.");
        }

        private static SoundEffect LoadOggSoundEffectNVorbis(string path)
        {
            using var vorbis = new VorbisReader(path);

            int sampleRate = vorbis.SampleRate;
            int channels = vorbis.Channels;

            const int framesPerRead = 4096;
            float[] floatBuffer = new float[framesPerRead * channels];

            using var pcmStream = new MemoryStream(capacity: 1024 * 1024);

            while (true)
            {
                int samplesRead = vorbis.ReadSamples(floatBuffer, 0, floatBuffer.Length);
                if (samplesRead <= 0)
                    break;

                for (int i = 0; i < samplesRead; i++)
                {
                    float f = floatBuffer[i];
                    if (f > 1f) f = 1f;
                    else if (f < -1f) f = -1f;

                    short pcm = (short)Math.Round(f * short.MaxValue);
                    pcmStream.WriteByte((byte)(pcm & 0xFF));
                    pcmStream.WriteByte((byte)((pcm >> 8) & 0xFF));
                }
            }

            byte[] pcmData = pcmStream.ToArray();

            AudioChannels audioChannels =
                (channels <= 1) ? AudioChannels.Mono : AudioChannels.Stereo;

            return new SoundEffect(pcmData, sampleRate, audioChannels);
        }

        private static void ApplyGameMusicVolume(SoundEffectInstance instance)
        {
            if (instance != null && Game1.options != null)
                instance.Volume = Game1.options.musicVolumeLevel;
        }

        public static void UpdateVolumes()
        {
            if (Game1.options == null)
                return;

            float vol = Game1.options.musicVolumeLevel;

            foreach (var inst in _instances.Values)
            {
                if (inst != null && inst.State == SoundState.Playing)
                    inst.Volume = vol;
            }

            // manual loop
            if (_currentReplacementKey != null &&
                _instances.TryGetValue(_currentReplacementKey, out var active) &&
                active != null &&
                active.State == SoundState.Stopped)
            {
                try
                {
                    ApplyGameMusicVolume(active);
                    active.Play();
                }
                catch { }
            }
        }

        public static void ReloadConfig()
        {
            try
            {
                ModEntry.SMonitor.Log("DMMusic: ReloadConfig() – trying to read musicReplacements.json...", LogLevel.Info);

                var map = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>("musicReplacements.json");

                if (map == null)
                {
                    ModEntry.SMonitor.Log("DMMusic: musicReplacements.json not found or empty.", LogLevel.Warn);
                    _replacementMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    return;
                }

                _replacementMap = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);

                ModEntry.SMonitor.Log($"DMMusic: Loaded {_replacementMap.Count} music replacement entries.", LogLevel.Info);
                foreach (var kvp in _replacementMap)
                    ModEntry.SMonitor.Log($"DMMusic: Replacement mapping: '{kvp.Key}' -> '{kvp.Value}'", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Failed to load musicReplacements.json. Error: {ex}", LogLevel.Error);
                _replacementMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        // ---------------------------
        // Context + matching
        // ---------------------------
        private readonly struct MusicContextData
        {
            public readonly string Location;
            public readonly bool EventUp;
            public readonly string? EventId;

            public MusicContextData(string location, bool eventUp, string? eventId)
            {
                Location = location;
                EventUp = eventUp;
                EventId = eventId;
            }
        }

        private static MusicContextData GetContextData()
        {
            string locName = Game1.player?.currentLocation?.NameOrUniqueName ?? "UnknownLocation";

            bool eventUp = Game1.eventUp && Game1.CurrentEvent != null;
            string? eventId = null;

            if (eventUp)
            {
                var ev = Game1.CurrentEvent!;
                eventId = TryGetMemberAsString(ev, "id")
                       ?? TryGetMemberAsString(ev, "Id")
                       ?? TryGetMemberAsString(ev, "eventId");
            }

            return new MusicContextData(locName, eventUp, string.IsNullOrWhiteSpace(eventId) ? null : eventId);
        }

        // Returns candidates in priority order (most specific first)
        private static List<string> BuildCandidateKeys(string trackId, MusicContextData ctx)
        {
            var list = new List<string>(capacity: 6);

            // Most specific
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

        // For logging: show what to paste into json (with empty value placeholders)
        public static string GetConfigSuggestion(string trackId)
        {
            var ctx = GetContextData();
            var keys = BuildCandidateKeys(trackId, ctx);

            // Only show unique suggestions, preserve order
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var lines = new List<string>();
            foreach (var k in keys)
            {
                if (seen.Add(k))
                    lines.Add($"  \"{k}\": \"\",");
            }

            return string.Join(Environment.NewLine, lines);
        }

        // Keep your old debug string (but now includes EventId only when meaningful)
        public static string GetMusicContextDebug()
        {
            try
            {
                var ctx = GetContextData();
                if (!ctx.EventUp)
                    return $"EventUp=false, Location={ctx.Location}";
                return $"EventUp=true, Location={ctx.Location}, EventId={ctx.EventId ?? "UnknownEventId"}";
            }
            catch
            {
                return "EventContext=Unknown";
            }
        }

        // ---------------------------
        // Playback using conditional keys
        // ---------------------------
        public static bool TryPlayTrack(string trackId)
        {
            if (_replacementMap.Count == 0)
                ReloadConfig();

            var ctx = GetContextData();
            var candidates = BuildCandidateKeys(trackId, ctx);

            // Find the best match
            string? matchedKey = null;
            string? relativePath = null;

            foreach (var key in candidates)
            {
                if (_replacementMap.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path))
                {
                    matchedKey = key;
                    relativePath = path;
                    break;
                }
            }

            if (matchedKey == null || string.IsNullOrWhiteSpace(relativePath))
            {
                ModEntry.SMonitor.Log(
                    $"DMMusic: No replacement configured for track '{trackId}'. " +
                    $"Try adding one of these keys:\n{GetConfigSuggestion(trackId)}",
                    LogLevel.Trace
                );
                return false;
            }

            try
            {
                // prevent overlap (compare against matched key, not raw trackId)
                if (!string.Equals(_currentReplacementKey, matchedKey, StringComparison.OrdinalIgnoreCase))
                {
                    StopAllTracks(disposeInstances: false);
                    _currentReplacementKey = matchedKey;
                }

                if (!_instances.TryGetValue(matchedKey, out var instance) || instance == null)
                {
                    string fullPath = Path.Combine(ModEntry.SHelper.DirectoryPath, relativePath);

                    if (!File.Exists(fullPath))
                    {
                        ModEntry.SMonitor.Log($"DMMusic: Replacement file for '{matchedKey}' not found at '{fullPath}'.", LogLevel.Warn);
                        return false;
                    }

                    SoundEffect effect = LoadSoundEffectFromFile(fullPath);

                    instance = effect.CreateInstance();
                    _instances[matchedKey] = instance;
                }

                // mute vanilla
                if (_vanillaMusicVolume == null)
                {
                    _vanillaMusicVolume = Game1.musicPlayerVolume;
                    Game1.musicPlayerVolume = 0f;
                }

                if (instance.State == SoundState.Playing)
                {
                    ApplyGameMusicVolume(instance);
                    return true;
                }

                ApplyGameMusicVolume(instance);
                instance.Play();

                ModEntry.SMonitor.Log($"DMMusic: Playing replacement for '{matchedKey}' from '{relativePath}'.", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Error while trying to play replacement for '{trackId}': {ex}", LogLevel.Error);
                return false;
            }
        }

        public static void StopAllTracks(bool disposeInstances)
        {
            try
            {
                foreach (var kvp in _instances)
                {
                    var inst = kvp.Value;
                    if (inst == null) continue;

                    try
                    {
                        if (inst.State != SoundState.Stopped)
                            inst.Stop();

                        if (disposeInstances)
                            inst.Dispose();
                    }
                    catch { }
                }

                if (disposeInstances)
                    _instances.Clear();

                _currentReplacementKey = null;

                if (_vanillaMusicVolume != null)
                {
                    Game1.musicPlayerVolume = _vanillaMusicVolume.Value;
                    _vanillaMusicVolume = null;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Error while stopping custom tracks: {ex}", LogLevel.Error);
            }
        }

        private static string? TryGetMemberAsString(object instance, string name)
        {
            var t = instance.GetType();

            var prop = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (prop != null)
            {
                var val = prop.GetValue(instance);
                if (val != null) return val.ToString();
            }

            var field = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                var val = field.GetValue(instance);
                if (val != null) return val.ToString();
            }

            return null;
        }
    }

    internal static class MusicPatches
    {
        public static bool ChangeMusicTrack_Prefix(
            ref string newTrackName,
            bool track_interruptable,
            MusicContext music_context
        )
        {
            string ctx = MusicManager.GetMusicContextDebug();

            // Show suggestions right in the debug log (so you can copy/paste)
            string suggestions = "";
            if (!string.IsNullOrEmpty(newTrackName) && !string.Equals(newTrackName, "none", StringComparison.OrdinalIgnoreCase))
                suggestions = "\nSuggested musicReplacements.json keys:\n" + MusicManager.GetConfigSuggestion(newTrackName);

            ModEntry.SMonitor.Log(
                $"[DMMusic DEBUG] Game is requesting music track: '{newTrackName}' " +
                $"(Interruptable={track_interruptable}, Context={music_context}, {ctx})" +
                suggestions,
                LogLevel.Info
            );

            if (string.IsNullOrEmpty(newTrackName))
            {
                MusicManager.StopAllTracks(disposeInstances: false);
                return true;
            }

            if (MusicManager.TryPlayTrack(newTrackName))
                return false;

            MusicManager.StopAllTracks(disposeInstances: false);
            return true;
        }
    }
}
