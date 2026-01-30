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
        internal static ModConfig Config { get; private set; } = new();

        internal static IMonitor SMonitor = null!;
        internal static IModHelper SHelper = null!;

        public override void Entry(IModHelper helper)
        {

            Config = helper.ReadConfig<ModConfig>();

            SMonitor = this.Monitor;
            SHelper = helper;

            MusicManager.ReloadConfig();
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            var harmony = new Harmony(this.ModManifest.UniqueID);

            try
            {
                // Stardew 1.6 has changeMusicTrack(string, bool, MusicContext)
                MethodInfo? target = typeof(Game1)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => m.Name == nameof(Game1.changeMusicTrack))
                    .FirstOrDefault(m =>
                    {
                        var p = m.GetParameters();
                        return p.Length == 3
                               && p[0].ParameterType == typeof(string)
                               && p[1].ParameterType == typeof(bool);
                        // p[2] is MusicContext in 1.6; we don't hard-check type to be resilient
                    });

                if (target == null)
                {
                    SMonitor.Log("DMMusic: Could not find Stardew 1.6 Game1.changeMusicTrack(string,bool,MusicContext) to patch.", LogLevel.Error);
                    return;
                }

                harmony.Patch(
                    original: target,
                    prefix: new HarmonyMethod(typeof(MusicPatches), nameof(MusicPatches.ChangeMusicTrack_Prefix))
                );

                SMonitor.Log($"DMMusic: Patched {target.DeclaringType?.FullName}.{target.Name}({string.Join(", ", target.GetParameters().Select(p => p.ParameterType.Name))})", LogLevel.Info);
            }
            catch (Exception ex)
            {
                SMonitor.Log($"DMMusic: Failed to patch changeMusicTrack: {ex}", LogLevel.Error);
            }
            
        }

        
        private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
        {
            MusicManager.UpdateVolumes();
        }
        
    }
}
