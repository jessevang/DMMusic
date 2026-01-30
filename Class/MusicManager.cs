using DMMusic.Lib.NVorbis;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
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

        public static string GetSuggestionLines(string trackId)
        {
            var ctx = MusicContextInfo.Get();
            var keys = MusicReplacementResolver.BuildCandidateKeys(trackId, ctx);
            return MusicReplacementResolver.BuildSuggestionLines(keys);
        }

        public static string GetMusicContextDebug()
        {
            return MusicContextInfo.Get().ToDebugString();
        }

        public static bool TryPlayTrack(string trackId, out string matchedKey, out string pickedRelativePath)
        {
            matchedKey = "";
            pickedRelativePath = "";

            if (_replacementMap.Count == 0)
                ReloadConfig();

            var ctx = MusicContextInfo.Get();

            if (!MusicReplacementResolver.TryResolveKey(_replacementMap, trackId, ctx,
                    out matchedKey, out var paths, out _))
            {
                return false;
            }

            // Sticky behavior: if same matched key requested again, keep current instance.
            if (string.Equals(_currentReplacementGroupKey, matchedKey, StringComparison.OrdinalIgnoreCase) &&
                _currentReplacementInstanceKey != null &&
                _instances.TryGetValue(_currentReplacementInstanceKey, out var current) &&
                current != null)
            {
                if (_vanillaMusicVolume == null)
                {
                    _vanillaMusicVolume = Game1.musicPlayerVolume;
                    Game1.musicPlayerVolume = 0f;
                }

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

            // Choose a file (only when key changes or no active instance)
            MusicPath picked = PickRandom(paths);
            if (picked == null || string.IsNullOrWhiteSpace(picked.RelativePath))
                return false;

            pickedRelativePath = picked.RelativePath;

            // If key changed, stop previous group
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

                if (_vanillaMusicVolume == null)
                {
                    _vanillaMusicVolume = Game1.musicPlayerVolume;
                    Game1.musicPlayerVolume = 0f;
                }

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

        private static MusicPath PickRandom(List<MusicPath> paths)
        {
            if (paths == null || paths.Count == 0)
                return null;

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
            // Return the mod that provided the currently playing replacement (best-effort)
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

            // fallback to this mod's name
            string selfId = ModEntry.SHelper.ModRegistry.ModID;
            return ModEntry.SHelper.ModRegistry.Get(selfId)?.Manifest?.Name ?? selfId;
        }


    }
}
