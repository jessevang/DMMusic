using HarmonyLib;
using Microsoft.Xna.Framework.Audio;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using System;

namespace DMMusic
{
    public class ModEntry : Mod
    {
        internal static IMonitor SMonitor = null!;
        internal static IModHelper SHelper = null!;

        public override void Entry(IModHelper helper)
        {
            SMonitor = this.Monitor;
            SHelper = helper;
            MusicManager.ReloadConfig();
            SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            var harmony = new Harmony(this.ModManifest.UniqueID);

            try
            {
                harmony.Patch(
                    original: AccessTools.Method(typeof(Game1), nameof(Game1.changeMusicTrack)),
                    prefix: new HarmonyMethod(typeof(MusicPatches), nameof(MusicPatches.ChangeMusicTrack_Prefix))
                );

                SMonitor.Log("DMMusic: Patched Game1.changeMusicTrack successfully.", LogLevel.Info);
            }
            catch (Exception ex)
            {
                SMonitor.Log($"DMMusic: Failed to patch changeMusicTrack: {ex}", LogLevel.Error);
            }
        }

        
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            MusicManager.UpdateVolumes();
        }



    }


}
