using DMMusic.Lib.NVorbis;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using System;
using System.Collections.Generic;
using System.IO;

namespace DMMusic
{
    internal sealed record MusicPath(string RelativePath, string SourceModId, string SourceModName, string BaseDir);

    internal static class MusicManager
    {
        private static Dictionary<string, List<MusicPath>> _replacementMap = new(StringComparer.OrdinalIgnoreCase);

        private static MusicPath? _currentReplacementPicked;
        private static string? _currentReplacementSourceModId;
        private static string? _currentReplacementInstanceKey;

        private static readonly Dictionary<string, SoundEffectInstance> _instances = new(StringComparer.OrdinalIgnoreCase);

        private static float? _vanillaMusicVolume;
        private static string? _currentReplacementGroupKey;

        private static string? _currentCustomTrackId;

        private static string? _lastCustomStartTrackId;
        private static DateTime _lastCustomStartUtc = DateTime.MinValue;
        private static readonly TimeSpan DuplicateCustomStartDebounce = TimeSpan.FromMilliseconds(450);

        private static readonly Random _rng = new();

        private static DateTime _lastLoopRestartAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan LoopRestartDebounce = TimeSpan.FromMilliseconds(150);

        private static readonly HashSet<string> _eventInstanceKeys = new(StringComparer.OrdinalIgnoreCase);

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

        public static bool IsCustomPlayingForTrack(string trackId)
        {
            if (string.IsNullOrWhiteSpace(trackId))
                return false;

            if (_currentReplacementInstanceKey == null)
                return false;

            if (!_instances.TryGetValue(_currentReplacementInstanceKey, out var inst) || inst == null)
                return false;

            if (inst.State != SoundState.Playing)
                return false;

            return string.Equals(_currentCustomTrackId, trackId, StringComparison.OrdinalIgnoreCase);
        }

        public static void ClearCurrentCustomTrackId()
        {
            _currentCustomTrackId = null;
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

            if (_currentReplacementInstanceKey != null)
                EnforceVanillaMutedAndStopped();

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

            if (_currentReplacementGroupKey != null && !IsCurrentReplacementEnabledFor(_currentReplacementGroupKey))
            {
                StopAllTracks(disposeInstances: false);
                return;
            }

            if (active.State == SoundState.Stopped)
            {
                var now = DateTime.UtcNow;

                if (_lastLoopRestartAttemptUtc == DateTime.MinValue ||
                    (now - _lastLoopRestartAttemptUtc) >= LoopRestartDebounce)
                {
                    _lastLoopRestartAttemptUtc = now;

                    try { StopInstanceImmediate(active); } catch { }

                    try
                    {
                        active.Volume = vol;
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

                var options = new System.Text.Json.JsonDocumentOptions
                {
                    CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
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

                    using var doc = System.Text.Json.JsonDocument.Parse(json, options);

                    if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        ModEntry.SMonitor.Log($"DMMusic: '{sourceModId}' musicReplacements.json root must be a JSON object.", LogLevel.Warn);
                        return;
                    }

                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        string key = prop.Name;
                        var val = prop.Value;

                        List<string>? relPaths = null;

                        if (val.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            string? p = val.GetString();
                            if (!string.IsNullOrWhiteSpace(p))
                                relPaths = new List<string> { p! };
                        }
                        else if (val.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            relPaths = new List<string>();

                            foreach (var item in val.EnumerateArray())
                            {
                                if (item.ValueKind != System.Text.Json.JsonValueKind.String)
                                    continue;

                                string? p = item.GetString();
                                if (!string.IsNullOrWhiteSpace(p))
                                    relPaths.Add(p!);
                            }

                            if (relPaths.Count == 0)
                                relPaths = null;
                        }
                        else if (val.ValueKind == System.Text.Json.JsonValueKind.Null)
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

                string baseModId = ModEntry.SHelper.ModRegistry.ModID;
                string baseModName = ModEntry.SHelper.ModRegistry.Get(baseModId)?.Manifest?.Name ?? baseModId;
                string baseDir = ModEntry.SHelper.DirectoryPath;
                string baseJson = Path.Combine(baseDir, "musicReplacements.json");

                if (File.Exists(baseJson))
                    MergeFile(baseJson, baseDir, baseModId, baseModName);
                else
                    ModEntry.SMonitor.Log("DMMusic: Base musicReplacements.json not found (OK if using only content packs).", LogLevel.Trace);

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

        public static bool TryPlayTrack(string trackId, out string matchedKey, out string pickedRelativePath)
        {
            return TryPlayTrack(trackId, trackPlayIndex: null, out matchedKey, out pickedRelativePath);
        }

        public static bool TryPlayTrack(string trackId, int? trackPlayIndex, out string matchedKey, out string pickedRelativePath)
        {
            matchedKey = "";
            pickedRelativePath = "";

            if (_replacementMap.Count == 0)
                ReloadConfig();

            if (IsCustomPlayingForTrack(trackId))
            {
                if (_currentReplacementPicked != null)
                    pickedRelativePath = _currentReplacementPicked.RelativePath;
                if (_currentReplacementGroupKey != null)
                    matchedKey = _currentReplacementGroupKey;
                return true;
            }

            var nowUtc = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(_lastCustomStartTrackId) &&
                string.Equals(_lastCustomStartTrackId, trackId, StringComparison.OrdinalIgnoreCase) &&
                _lastCustomStartUtc != DateTime.MinValue &&
                (nowUtc - _lastCustomStartUtc) < DuplicateCustomStartDebounce)
            {
                if (_currentReplacementInstanceKey != null &&
                    _instances.TryGetValue(_currentReplacementInstanceKey, out var inst) &&
                    inst != null)
                {
                    EnforceVanillaMutedAndStopped();
                    ApplyGameMusicVolume(inst);

                    if (inst.State == SoundState.Playing)
                    {
                        _currentCustomTrackId = trackId;
                        if (_currentReplacementPicked != null)
                            pickedRelativePath = _currentReplacementPicked.RelativePath;
                        if (_currentReplacementGroupKey != null)
                            matchedKey = _currentReplacementGroupKey;
                        return true;
                    }
                }
            }

            var ctx = MusicContextInfo.Get();
            var candidateKeys = MusicReplacementResolver.BuildCandidateKeys(trackId, ctx, trackPlayIndex);

            if (!MusicReplacementResolver.TryResolveKey(_replacementMap, trackId, ctx, candidateKeys,
                    out matchedKey, out var rawPaths))
            {
                return false;
            }

            var paths = new List<MusicPath>(rawPaths.Count);
            foreach (var p in rawPaths)
            {
                if (p == null) continue;
                if (IsSourceModDisabled(p.SourceModId)) continue;

                if (ModEntry.Config == null || !ModEntry.Config.DisabledReplacementIds.Contains(BuildReplacementId(matchedKey, p)))
                    paths.Add(p);
            }

            if (paths.Count == 0)
                return false;

            static bool SameEntry(MusicPath a, MusicPath b)
            {
                return string.Equals(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.SourceModId, b.SourceModId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.BaseDir, b.BaseDir, StringComparison.OrdinalIgnoreCase);
            }

            void EnsureVanillaMuted()
            {
                if (_vanillaMusicVolume == null)
                {
                    _vanillaMusicVolume = Game1.musicPlayerVolume;
                    Game1.musicPlayerVolume = 0f;
                }
            }

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
                    EnforceVanillaMutedAndStopped();

                    pickedRelativePath = _currentReplacementPicked.RelativePath;
                    _currentReplacementGroupKey = matchedKey;

                    string newInstanceKey = $"{matchedKey}||{_currentReplacementPicked.SourceModId}||{_currentReplacementPicked.RelativePath}";

                    if (!string.Equals(_currentReplacementInstanceKey, newInstanceKey, StringComparison.OrdinalIgnoreCase))
                    {
                        _instances[newInstanceKey] = active;
                        _currentReplacementInstanceKey = newInstanceKey;
                    }

                    if (ctx.EventUp && _currentReplacementInstanceKey != null)
                        _eventInstanceKeys.Add(_currentReplacementInstanceKey);

                    ApplyGameMusicVolume(active);

                    if (active.State == SoundState.Playing)
                    {
                        _currentCustomTrackId = trackId;
                        _lastCustomStartTrackId = trackId;
                        _lastCustomStartUtc = nowUtc;
                        return true;
                    }

                    if (active.State == SoundState.Stopped)
                    {
                        try
                        {
                            StopInstanceImmediate(active);
                            active.Play();
                            _currentCustomTrackId = trackId;
                            _lastCustomStartTrackId = trackId;
                            _lastCustomStartUtc = nowUtc;
                            return true;
                        }
                        catch { }
                    }
                }
            }

            if (string.Equals(_currentReplacementGroupKey, matchedKey, StringComparison.OrdinalIgnoreCase) &&
                _currentReplacementInstanceKey != null &&
                _instances.TryGetValue(_currentReplacementInstanceKey, out var current) &&
                current != null)
            {
                if (!IsCurrentReplacementEnabledFor(matchedKey))
                {
                    StopAllTracks(disposeInstances: false);
                    return false;
                }

                EnsureVanillaMuted();
                EnforceVanillaMutedAndStopped();
                ApplyGameMusicVolume(current);

                if (_currentReplacementPicked != null)
                {
                    pickedRelativePath = _currentReplacementPicked.RelativePath;
                    _currentReplacementSourceModId = _currentReplacementPicked.SourceModId;
                }

                if (ctx.EventUp && _currentReplacementInstanceKey != null)
                    _eventInstanceKeys.Add(_currentReplacementInstanceKey);

                if (current.State == SoundState.Playing)
                {
                    _currentCustomTrackId = trackId;
                    _lastCustomStartTrackId = trackId;
                    _lastCustomStartUtc = nowUtc;
                    return true;
                }

                if (current.State == SoundState.Stopped)
                {
                    try
                    {
                        StopInstanceImmediate(current);
                        current.Play();
                        _currentCustomTrackId = trackId;
                        _lastCustomStartTrackId = trackId;
                        _lastCustomStartUtc = nowUtc;
                        return true;
                    }
                    catch { }
                }
            }

            MusicPath picked = PickRandom(paths);
            if (picked == null || string.IsNullOrWhiteSpace(picked.RelativePath))
                return false;

            pickedRelativePath = picked.RelativePath;

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
                EnforceVanillaMutedAndStopped();
                ApplyGameMusicVolume(instance);

                if (instance.State != SoundState.Playing)
                {
                    try { StopInstanceImmediate(instance); } catch { }
                    instance.Play();
                }

                _currentReplacementInstanceKey = instanceKey;
                _currentReplacementPicked = picked;
                _currentReplacementSourceModId = picked.SourceModId;
                _currentCustomTrackId = trackId;

                if (ctx.EventUp)
                    _eventInstanceKeys.Add(instanceKey);

                _lastCustomStartTrackId = trackId;
                _lastCustomStartUtc = nowUtc;

                return true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Error while trying to play replacement for '{trackId}': {ex}", LogLevel.Error);
                return false;
            }
        }

        public static string BuildReplacementId(string matchedKey, MusicPath path)
            => $"{matchedKey}||{path.SourceModId}||{path.RelativePath}".Trim();

        public static bool IsReplacementEnabled(string key, MusicPath path)
        {
            var cfg = ModEntry.Config;
            if (cfg == null)
                return true;

            string id = BuildReplacementId(key, path);
            return !cfg.DisabledReplacementIds.Contains(id);
        }

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
                return null!;

            if (paths.Count == 1)
                return paths[0];

            return paths[_rng.Next(paths.Count)];
        }

        public static void StopEventTracks(bool stopVanillaToo)
        {
            try
            {
                foreach (var key in _eventInstanceKeys)
                {
                    if (!_instances.TryGetValue(key, out var inst) || inst == null)
                        continue;

                    try
                    {
                        if (inst.State != SoundState.Stopped)
                            StopInstanceImmediate(inst);

                        inst.Dispose();
                    }
                    catch { }

                    _instances.Remove(key);

                    if (_currentReplacementInstanceKey != null &&
                        string.Equals(_currentReplacementInstanceKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentReplacementInstanceKey = null;
                        _currentReplacementGroupKey = null;
                        _currentReplacementPicked = null;
                        _currentReplacementSourceModId = null;
                        _currentCustomTrackId = null;
                    }
                }

                _eventInstanceKeys.Clear();

                if (stopVanillaToo)
                    StopVanillaAudioForContext(MusicContext.Default);

                if (_currentReplacementInstanceKey == null && _vanillaMusicVolume != null)
                {
                    Game1.musicPlayerVolume = _vanillaMusicVolume.Value;
                    _vanillaMusicVolume = null;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Error while stopping event-scoped tracks: {ex}", LogLevel.Error);
            }
        }

        public static void StopAllCustomTracks(bool stopVanillaToo, bool disposeInstances)
        {
            StopAllTracks(disposeInstances);

            if (stopVanillaToo)
                StopVanillaAudioForContext(MusicContext.Default);
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
                            StopInstanceImmediate(inst);

                        if (disposeInstances)
                            inst.Dispose();
                    }
                    catch { }
                }

                if (disposeInstances)
                    _instances.Clear();

                _eventInstanceKeys.Clear();

                _currentReplacementGroupKey = null;
                _currentReplacementInstanceKey = null;
                _currentReplacementPicked = null;
                _currentReplacementSourceModId = null;
                _currentCustomTrackId = null;

                _lastCustomStartTrackId = null;
                _lastCustomStartUtc = DateTime.MinValue;

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
            => HasReplacement(trackId, trackPlayIndex: null, out matchedKey);

        public static void StopVanillaAudioForContext(MusicContext musicContext)
        {
            try { Game1.stopMusicTrack(musicContext); } catch { }

            try
            {
                if (Game1.currentSong != null)
                    Game1.currentSong.Stop(AudioStopOptions.Immediate);
            }
            catch { }

            try { Game1.loopingLocationCues?.StopAll(); } catch { }
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

        private static bool IsSourceModDisabled(string sourceModId)
        {
            var cfg = ModEntry.Config;
            if (cfg == null)
                return false;

            if (string.IsNullOrWhiteSpace(sourceModId))
                return false;

            return cfg.DisabledModIds.Contains(sourceModId);
        }

        private static void EnforceVanillaMutedAndStopped()
        {
            try
            {
                if (_vanillaMusicVolume == null)
                    _vanillaMusicVolume = Game1.musicPlayerVolume;

                Game1.musicPlayerVolume = 0f;
            }
            catch { }

            try
            {
                if (Game1.currentSong != null && Game1.currentSong.IsPlaying)
                    Game1.currentSong.Stop(AudioStopOptions.Immediate);
            }
            catch { }

            try { Game1.loopingLocationCues?.StopAll(); } catch { }
        }

        private static void StopInstanceImmediate(SoundEffectInstance inst)
        {
            if (inst == null) return;

            try
            {
                var m = inst.GetType().GetMethod("Stop", new[] { typeof(bool) });
                if (m != null)
                {
                    m.Invoke(inst, new object[] { true });
                    return;
                }
            }
            catch { }

            try { inst.Stop(); } catch { }
        }
    }
}
