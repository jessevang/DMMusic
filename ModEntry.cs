using DMMusic.Framework;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System;
using System.Linq;
using System.Reflection;

namespace DMMusic
{
    public class ModEntry : Mod
    {
        private bool _prevEventUp;
        public HashSet<string> DisabledModIds { get; set; } = new();

        internal static ModConfig Config { get; set; } = new();

        internal static IMonitor SMonitor = null!;
        internal static IModHelper SHelper = null!;

        public override void Entry(IModHelper helper)
        {
            Config = helper.ReadConfig<ModConfig>();

            SMonitor = this.Monitor;
            SHelper = helper;

            MusicManager.ReloadConfig();

            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            var harmony = new Harmony(this.ModManifest.UniqueID);

            try
            {
                MethodInfo? target = typeof(Game1)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.Name == nameof(Game1.changeMusicTrack))
                    .FirstOrDefault(m =>
                    {
                        var p = m.GetParameters();
                        return p.Length == 3
                               && p[0].ParameterType == typeof(string)
                               && p[1].ParameterType == typeof(bool);
                    });

                if (target == null)
                {
                    SMonitor.Log("DMMusic: Could not find Game1.changeMusicTrack(string,bool,MusicContext) to patch.", LogLevel.Error);
                    return;
                }

                harmony.Patch(
                    original: target,
                    prefix: new HarmonyMethod(typeof(MusicPatches), nameof(MusicPatches.ChangeMusicTrack_Prefix))
                );

                SMonitor.Log(
                    $"DMMusic: Patched {target.DeclaringType?.FullName}.{target.Name}({string.Join(", ", target.GetParameters().Select(p => p.ParameterType.Name))})",
                    LogLevel.Info
                );
            }
            catch (Exception ex)
            {
                SMonitor.Log($"DMMusic: Failed to patch changeMusicTrack: {ex}", LogLevel.Error);
            }
        }

        private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
        {
            GmcmIntegration.RegisterIfAvailable(
                helper: this.Helper,
                monitor: this.Monitor,
                manifest: this.ModManifest,
                getConfig: () => Config,
                setConfig: cfg => Config = cfg,
                ensureReplacementsLoaded: () =>
                {
                    MusicManager.ReloadConfig();
                    return MusicManager.GetAllReplacementEntries().Any();
                }
            );
        }

        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady)
                return;

            bool nowEventUp = Game1.eventUp && Game1.CurrentEvent != null;

            if (_prevEventUp && !nowEventUp)
                MusicManager.StopEventTracks(stopVanillaToo: true);

            _prevEventUp = nowEventUp;

            MusicManager.UpdateVolumes();
        }
    }
}
