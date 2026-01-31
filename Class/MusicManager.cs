using DMMusic.Lib.NVorbis;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DMMusic
{
    // path to music files from music packs
    internal sealed record MusicPath(string RelativePath, string SourceModId, string SourceModName, string BaseDir);

    internal static class MusicManager
    {
        // key -> list of music entries (1 or many)
        private static Dictionary<string, List<MusicPath>> _replacementMap = new(StringComparer.OrdinalIgnoreCase);

        // current chosen entry (for logging + source mod)
        private static MusicPath? _currentReplacementPicked;
        private static string? _currentReplacementSourceModId;

        // used to not play 2 songs at the same time (exact instance key)
        private static string? _currentReplacementInstanceKey;

        // cache instances by matchedKey||SourceModId||relativePath (so shuffle works safely)
        private static readonly Dictionary<string, SoundEffectInstance> _instances = new(StringComparer.OrdinalIgnoreCase);

        private static float? _vanillaMusicVolume;
        private static string? _currentReplacementGroupKey; // matchedKey currently active (logical group)

        private static readonly Random _rng = new();

        // tracks music for inactive windows + debounce
        private static DateTime _lastLoopRestartAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan LoopRestartDebounce = TimeSpan.FromSeconds(2);

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

            AudioChannels audioChannels = (channels <= 1) ? AudioChannels.Mono : AudioChannels.Stereo;
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

            if (_currentReplacementInstanceKey == null)
                return;

            if (!_instances.TryGetValue(_currentReplacementInstanceKey, out var active) || active == null)
                return;

            bool gameIsActive = true;
            try
            {
                gameIsActive = Game1.game1 != null && Game1.game1.IsActive;
            }
            catch { }

            if (!gameIsActive)
                return;

            if (active.State == SoundState.Stopped)
            {
                // If the currently playing replacement was disabled via GMCM, stop it and restore vanilla.
                if (_currentReplacementGroupKey != null && !IsCurrentReplacementEnabledFor(_currentReplacementGroupKey))
                {
                    StopAllTracks(disposeInstances: false);
                    return;
                }

                var now = DateTime.UtcNow;

                if (_lastLoopRestartAttemptUtc == DateTime.MinValue ||
                    (now - _lastLoopRestartAttemptUtc) >= LoopRestartDebounce)
                {
                    _lastLoopRestartAttemptUtc = now;

                    try
                    {
                        ApplyGameMusicVolume(active);
                        active.Play();
                    }
                    catch { }
                }
            }
        }

        public static void ReloadConfig()
        {
            try
            {
                ModEntry.SMonitor.Log("DMMusic: ReloadConfig() – reading musicReplacements.json (base + content packs)...", LogLevel.Info);

                var options = new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                };

                var merged = new Dictionary<string, List<MusicPath>>(StringComparer.OrdinalIgnoreCase);

                int filesLoaded = 0;
                int packsLoaded = 0;

                void MergeFile(string fullPath, string baseDir, string sourceModId, string sourceModName)
                {
                    if (!File.Exists(fullPath))
                        return;

                    string json = File.ReadAllText(fullPath);

                    using JsonDocument doc = JsonDocument.Parse(json, options);

                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        ModEntry.SMonitor.Log($"DMMusic: '{sourceModId}' musicReplacements.json root must be a JSON object.", LogLevel.Warn);
                        return;
                    }

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        string key = prop.Name;
                        JsonElement val = prop.Value;

                        // normalize into list of relative paths
                        List<string>? relPaths = null;

                        if (val.ValueKind == JsonValueKind.String)
                        {
                            string? p = val.GetString();
                            if (!string.IsNullOrWhiteSpace(p))
                                relPaths = new List<string> { p! };
                        }
                        else if (val.ValueKind == JsonValueKind.Array)
                        {
                            relPaths = new List<string>();

                            foreach (var item in val.EnumerateArray())
                            {
                                if (item.ValueKind != JsonValueKind.String)
                                    continue;

                                string? p = item.GetString();
                                if (!string.IsNullOrWhiteSpace(p))
                                    relPaths.Add(p!);
                            }

                            if (relPaths.Count == 0)
                                relPaths = null;
                        }
                        else if (val.ValueKind == JsonValueKind.Null)
                        {
                            relPaths = null;
                        }
                        else
                        {
                            ModEntry.SMonitor.Log($"DMMusic: Unsupported value for '{key}' in '{sourceModId}'. Use string or string array.", LogLevel.Warn);
                            relPaths = null;
                        }

                        if (relPaths == null)
                            continue;

                        if (!merged.TryGetValue(key, out var list))
                            merged[key] = list = new List<MusicPath>();

                        foreach (var rel in relPaths)
                        {
                            if (string.IsNullOrWhiteSpace(rel))
                                continue;

                            list.Add(new MusicPath(rel, sourceModId, sourceModName, baseDir));
                        }
                    }

                    filesLoaded++;
                }

                // 1) Base file in DMMusic (optional)
                string baseModId = ModEntry.SHelper.ModRegistry.ModID;
                string baseModName = ModEntry.SHelper.ModRegistry.Get(baseModId)?.Manifest?.Name ?? baseModId;
                string baseDir = ModEntry.SHelper.DirectoryPath;
                string baseJson = Path.Combine(baseDir, "musicReplacements.json");

                if (File.Exists(baseJson))
                    MergeFile(baseJson, baseDir, baseModId, baseModName);
                else
                    ModEntry.SMonitor.Log("DMMusic: Base musicReplacements.json not found (OK if using only content packs).", LogLevel.Trace);

                // 2) Content packs targeting DMMusic
                foreach (var pack in ModEntry.SHelper.ContentPacks.GetOwned())
                {
                    string packDir = pack.DirectoryPath;
                    string packJson = Path.Combine(packDir, "musicReplacements.json");

                    if (!File.Exists(packJson))
                    {
                        ModEntry.SMonitor.Log($"DMMusic: Content pack '{pack.Manifest.UniqueID}' has no musicReplacements.json (skipping).", LogLevel.Trace);
                        continue;
                    }

                    MergeFile(packJson, packDir, pack.Manifest.UniqueID, pack.Manifest.Name);
                    packsLoaded++;
                }

                _replacementMap = merged;

                ModEntry.SMonitor.Log(
                    $"DMMusic: Loaded {_replacementMap.Count} music replacement keys from {filesLoaded} file(s) ({packsLoaded} content pack(s)).",
                    LogLevel.Info
                );
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Failed to load musicReplacements.json: {ex}", LogLevel.Error);
                _replacementMap = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static string GetSuggestionLines(string trackId, int? trackPlayIndex = null)
        {
            var ctx = MusicContextInfo.Get();
            var keys = MusicReplacementResolver.BuildCandidateKeys(trackId, ctx, trackPlayIndex);
            return MusicReplacementResolver.BuildSuggestionLines(keys);
        }

        public static string GetMusicContextDebug()
        {
            return MusicContextInfo.Get().ToDebugString();
        }

        // Default overload (no TrackPlayIndex)
        public static bool TryPlayTrack(string trackId, out string matchedKey, out string pickedRelativePath)
        {
            return TryPlayTrack(trackId, trackPlayIndex: null, out matchedKey, out pickedRelativePath);
        }

        // Handles how many times a track is played in an event to allow different audios
        public static bool TryPlayTrack(string trackId, int? trackPlayIndex, out string matchedKey, out string pickedRelativePath)
        {
            matchedKey = "";
            pickedRelativePath = "";

            if (_replacementMap.Count == 0)
                ReloadConfig();

            var ctx = MusicContextInfo.Get();

            // Build candidate keys including TrackPlay when available
            var candidateKeys = MusicReplacementResolver.BuildCandidateKeys(trackId, ctx, trackPlayIndex);

            if (!MusicReplacementResolver.TryResolveKey(_replacementMap, trackId, ctx, candidateKeys,
                    out matchedKey, out var rawPaths))
            {
                return false;
            }

            // ----------------------------
            // Filter out disabled entries
            // ----------------------------
            var paths = new List<MusicPath>(rawPaths.Count);
            foreach (var p in rawPaths)
            {
                if (p == null) continue;

                // If config isn't initialized for some reason, treat as enabled.
                if (ModEntry.Config == null || !ModEntry.Config.DisabledReplacementIds.Contains(BuildReplacementId(matchedKey, p)))
                    paths.Add(p);
            }

            // If this key has replacements, but all were disabled, treat as "no replacement"
            if (paths.Count == 0)
                return false;

            static bool SameEntry(MusicPath a, MusicPath b)
            {
                return string.Equals(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.SourceModId, b.SourceModId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.BaseDir, b.BaseDir, StringComparison.OrdinalIgnoreCase);
            }

            // Ensure vanilla music is muted while we have a replacement running.
            void EnsureVanillaMuted()
            {
                if (_vanillaMusicVolume == null)
                {
                    _vanillaMusicVolume = Game1.musicPlayerVolume;
                    Game1.musicPlayerVolume = 0f;
                }
            }

            // ------------------------------------------------------------
            // FILE-STICKY:
            // Even if matchedKey changed, if the new pool contains the SAME file we’re already playing,
            // keep playing without restarting.
            // ------------------------------------------------------------
            if (_currentReplacementPicked != null &&
                _currentReplacementInstanceKey != null &&
                _instances.TryGetValue(_currentReplacementInstanceKey, out var active) &&
                active != null)
            {
                bool poolContainsCurrent = false;
                foreach (var p in paths)
                {
                    if (p != null && SameEntry(p, _currentReplacementPicked))
                    {
                        poolContainsCurrent = true;
                        break;
                    }
                }

                if (poolContainsCurrent)
                {
                    EnsureVanillaMuted();

                    pickedRelativePath = _currentReplacementPicked.RelativePath;
                    _currentReplacementGroupKey = matchedKey;

                    string newInstanceKey = $"{matchedKey}||{_currentReplacementPicked.SourceModId}||{_currentReplacementPicked.RelativePath}";

                    if (!string.Equals(_currentReplacementInstanceKey, newInstanceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _instances[newInstanceKey] = active; // safe alias
                        _currentReplacementInstanceKey = newInstanceKey;
                    }

                    ApplyGameMusicVolume(active);

                    if (active.State == SoundState.Playing)
                        return true;

                    if (active.State == SoundState.Stopped)
                    {
                        try
                        {
                            active.Play();
                            return true;
                        }
                        catch { }
                    }
                }
            }

            // ------------------------------------------------------------
            // KEY-STICKY:
            // Same matchedKey requested again -> keep same instance (no restart).
            // BUT: if the currently-playing entry was disabled in GMCM, do NOT keep it.
            // ------------------------------------------------------------
            if (string.Equals(_currentReplacementGroupKey, matchedKey, StringComparison.OrdinalIgnoreCase) &&
                _currentReplacementInstanceKey != null &&
                _instances.TryGetValue(_currentReplacementInstanceKey, out var current) &&
                current != null)
            {
                if (!IsCurrentReplacementEnabledFor(matchedKey))
                {
                    StopAllTracks(disposeInstances: false);
                    return false; // treat as no replacement so vanilla can play
                }

                EnsureVanillaMuted();
                ApplyGameMusicVolume(current);

                if (_currentReplacementPicked != null)
                {
                    pickedRelativePath = _currentReplacementPicked.RelativePath;
                    _currentReplacementSourceModId = _currentReplacementPicked.SourceModId;
                }

                if (current.State == SoundState.Playing)
                    return true;

                if (current.State == SoundState.Stopped)
                {
                    try
                    {
                        current.Play();
                        return true;
                    }
                    catch { }
                }
            }

            // ------------------------------------------------------------
            // Pick a (enabled) file from this key’s pool
            // ------------------------------------------------------------
            MusicPath picked = PickRandom(paths);
            if (picked == null || string.IsNullOrWhiteSpace(picked.RelativePath))
                return false;

            pickedRelativePath = picked.RelativePath;

            // If key changed, stop previous group (prevents overlap)
            if (!string.Equals(_currentReplacementGroupKey, matchedKey, StringComparison.OrdinalIgnoreCase))
            {
                StopAllTracks(disposeInstances: false);
                _currentReplacementGroupKey = matchedKey;
            }

            string instanceKey = $"{matchedKey}||{picked.SourceModId}||{picked.RelativePath}";

            try
            {
                if (!_instances.TryGetValue(instanceKey, out var instance) || instance == null)
                {
                    string fullPath = Path.Combine(picked.BaseDir, picked.RelativePath);

                    if (!File.Exists(fullPath))
                    {
                        ModEntry.SMonitor.Log(
                            $"DMMusic: File not found. Key='{matchedKey}' Track='{trackId}' Source='{picked.SourceModId}' Path='{fullPath}'.",
                            LogLevel.Warn
                        );
                        return false;
                    }

                    SoundEffect effect = LoadSoundEffectFromFile(fullPath);
                    instance = effect.CreateInstance();
                    _instances[instanceKey] = instance;
                }

                EnsureVanillaMuted();
                ApplyGameMusicVolume(instance);

                if (instance.State != SoundState.Playing)
                    instance.Play();

                _currentReplacementInstanceKey = instanceKey;
                _currentReplacementPicked = picked;
                _currentReplacementSourceModId = picked.SourceModId;

                return true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Error while trying to play replacement for '{trackId}': {ex}", LogLevel.Error);
                return false;
            }
        }

        public static string BuildReplacementId(string matchedKey, MusicPath path)
        {
            return $"{matchedKey}||{path.SourceModId}||{path.RelativePath}".Trim();
        }

        public static bool IsReplacementEnabled(string key, MusicPath path)
        {
            var cfg = ModEntry.Config;
            if (cfg == null)
                return true;

            string id = BuildReplacementId(key, path);
            return !cfg.DisabledReplacementIds.Contains(id);
        }

        /// <summary>Return all replacement entries currently loaded (safe if packs removed).</summary>
        public static IEnumerable<(string Key, MusicPath Path)> GetAllReplacementEntries()
        {
            if (_replacementMap.Count == 0)
                ReloadConfig();

            foreach (var kvp in _replacementMap)
            {
                string key = kvp.Key;
                var list = kvp.Value;
                if (list == null) continue;

                foreach (var p in list)
                {
                    if (p == null) continue;
                    yield return (key, p);
                }
            }
        }

        private static MusicPath PickRandom(List<MusicPath> paths)
        {
            if (paths == null || paths.Count == 0)
                return null!; // you only call when you already know there are items

            if (paths.Count == 1)
                return paths[0];

            return paths[_rng.Next(paths.Count)];
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

                _currentReplacementGroupKey = null;
                _currentReplacementInstanceKey = null;
                _currentReplacementPicked = null;
                _currentReplacementSourceModId = null;

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

        public static string GetSourceModLabel(string pickedRelativePath)
        {
            if (_currentReplacementPicked != null)
                return $"{_currentReplacementPicked.SourceModId} ({_currentReplacementPicked.SourceModName})";

            string self = ModEntry.SHelper.ModRegistry.ModID;
            string selfName = ModEntry.SHelper.ModRegistry.Get(self)?.Manifest?.Name ?? self;
            return $"{self} ({selfName})";
        }

        public static string GetSourceModName()
        {
            if (_currentReplacementPicked != null)
                return _currentReplacementPicked.SourceModName;

            string selfId = ModEntry.SHelper.ModRegistry.ModID;
            return ModEntry.SHelper.ModRegistry.Get(selfId)?.Manifest?.Name ?? selfId;
        }

        public static bool HasReplacement(string trackId, int? trackPlayIndex, out string matchedKey)
        {
            matchedKey = "";

            if (_replacementMap.Count == 0)
                ReloadConfig();

            var ctx = MusicContextInfo.Get();
            var candidateKeys = MusicReplacementResolver.BuildCandidateKeys(trackId, ctx, trackPlayIndex);

            return MusicReplacementResolver.TryResolveKey(
                _replacementMap,
                trackId,
                ctx,
                candidateKeys,
                out matchedKey,
                out _
            );
        }

        public static bool HasReplacement(string trackId, out string matchedKey)
        {
            return HasReplacement(trackId, trackPlayIndex: null, out matchedKey);
        }

        public static void StopVanillaAudioForContext(MusicContext musicContext)
        {
            try
            {
                Game1.stopMusicTrack(musicContext);
            }
            catch { }

            try
            {
                if (Game1.currentSong != null)
                    Game1.currentSong.Stop(AudioStopOptions.Immediate);
            }
            catch { }

            try
            {
                Game1.loopingLocationCues?.StopAll();
            }
            catch { }
        }

        private static bool IsCurrentReplacementEnabledFor(string matchedKey)
        {
            if (_currentReplacementPicked == null)
                return false;

            var cfg = ModEntry.Config;
            if (cfg == null)
                return true;

            string id = BuildReplacementId(matchedKey, _currentReplacementPicked);
            return !cfg.DisabledReplacementIds.Contains(id);
        }
    }
}
