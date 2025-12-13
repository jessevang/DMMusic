using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using System;
using System.Collections.Generic;
using System.IO;

namespace DMMusic
{
    internal static class MusicManager
    {
        private static Dictionary<string, string> _replacementMap = new();
        private static readonly Dictionary<string, SoundEffectInstance> _instances = new();
        private static float? _vanillaMusicVolume;

        // Apply Stardew's music volume slider to an instance
        private static void ApplyGameMusicVolume(SoundEffectInstance instance)
        {
            if (instance != null && Game1.options != null)
                instance.Volume = Game1.options.musicVolumeLevel;
        }

        // Called each tick to keep volume in sync with slider
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
        }

        public static void ReloadConfig()
        {
            try
            {
                ModEntry.SMonitor.Log("DMMusic: ReloadConfig() – trying to read musicReplacements.json...", LogLevel.Info);

                var map = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>("musicReplacements.json");

                if (map == null)
                {
                    ModEntry.SMonitor.Log(
                        "DMMusic: musicReplacements.json not found or empty. No tracks will be replaced until you create it.",
                        LogLevel.Warn
                    );
                    _replacementMap = new Dictionary<string, string>();
                    return;
                }

                _replacementMap = map;

                ModEntry.SMonitor.Log(
                    $"DMMusic: Loaded {_replacementMap.Count} music replacement entries from musicReplacements.json.",
                    LogLevel.Info
                );

                foreach (var kvp in _replacementMap)
                {
                    ModEntry.SMonitor.Log(
                        $"DMMusic: Replacement mapping: '{kvp.Key}' -> '{kvp.Value}'",
                        LogLevel.Info
                    );
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log(
                    $"DMMusic: Failed to load musicReplacements.json, using empty map. Error: {ex}",
                    LogLevel.Error
                );
                _replacementMap = new Dictionary<string, string>();
            }
        }

        public static bool TryPlayTrack(string trackId)
        {
            if (_replacementMap.Count == 0)
            {
                ModEntry.SMonitor.Log(
                    "DMMusic: Replacement map empty in TryPlayTrack(). Attempting lazy ReloadConfig()...",
                    LogLevel.Info
                );
                ReloadConfig();
            }

            if (!_replacementMap.TryGetValue(trackId, out string? relativePath) ||
                string.IsNullOrWhiteSpace(relativePath))
            {
                ModEntry.SMonitor.Log(
                    $"DMMusic: No replacement configured for track '{trackId}'. Letting vanilla play.",
                    LogLevel.Info
                );
                return false;
            }

            try
            {
                if (!_instances.TryGetValue(trackId, out var instance) || instance == null)
                {
                    string fullPath = Path.Combine(ModEntry.SHelper.DirectoryPath, relativePath);

                    if (!File.Exists(fullPath))
                    {
                        ModEntry.SMonitor.Log(
                            $"DMMusic: Replacement file for '{trackId}' not found at '{fullPath}'.",
                            LogLevel.Warn
                        );
                        return false;
                    }

                    using var stream = File.OpenRead(fullPath);
                    var effect = SoundEffect.FromStream(stream, true);
                    instance = effect.CreateInstance();
                    _instances[trackId] = instance;
                }

                if (_vanillaMusicVolume == null)
                {
                    _vanillaMusicVolume = Game1.musicPlayerVolume;
                    Game1.musicPlayerVolume = 0f;
                    ModEntry.SMonitor.Log("DMMusic: Muted vanilla music player volume.", LogLevel.Trace);
                }

                if (instance.State == SoundState.Playing)
                {
                    ModEntry.SMonitor.Log(
                        $"DMMusic: Track '{trackId}' already playing, not restarting.",
                        LogLevel.Trace
                    );

                    // keep already-playing track in sync with current music volume
                    ApplyGameMusicVolume(instance);
                    return true;
                }

                instance.Play();

                // apply game music volume when starting playback
                ApplyGameMusicVolume(instance);

                ModEntry.SMonitor.Log(
                    $"DMMusic: Playing replacement for '{trackId}' from '{relativePath}'.",
                    LogLevel.Info
                );

                return true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log(
                    $"DMMusic: Error while trying to play replacement for '{trackId}': {ex}",
                    LogLevel.Error
                );
                return false;
            }
        }

        public static void StopAllTracks()
        {
            try
            {
                foreach (var kvp in _instances)
                {
                    var inst = kvp.Value;
                    if (inst != null && inst.State != SoundState.Stopped)
                    {
                        inst.Stop();
                        ModEntry.SMonitor.Log($"DMMusic: Stopped custom track instance for '{kvp.Key}'.", LogLevel.Trace);
                    }
                }

                if (_vanillaMusicVolume != null)
                {
                    Game1.musicPlayerVolume = _vanillaMusicVolume.Value;
                    _vanillaMusicVolume = null;
                    ModEntry.SMonitor.Log("DMMusic: Restored vanilla music volume.", LogLevel.Trace);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"DMMusic: Error while stopping custom tracks: {ex}", LogLevel.Error);
            }
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
            ModEntry.SMonitor.Log(
                $"[DMMusic DEBUG] Game is requesting music track: '{newTrackName}' " +
                $"(Interruptable={track_interruptable}, Context={music_context})",
                LogLevel.Info
            );

            if (string.IsNullOrEmpty(newTrackName))
            {
                ModEntry.SMonitor.Log("[DMMusic DEBUG] newTrackName was NULL or EMPTY.", LogLevel.Info);
                MusicManager.StopAllTracks();
                return true;
            }

            if (MusicManager.TryPlayTrack(newTrackName))
            {
                return false;
            }

            MusicManager.StopAllTracks();
            return true;
        }
    }
}
