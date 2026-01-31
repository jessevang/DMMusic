using StardewModdingAPI;
using System;

namespace DMMusic.Framework
{
    /// <summary>
    /// Minimal GMCM API interface for DMMusic.
    /// Keep it small and signatures must match GMCM exactly for SMAPI proxy mapping.
    ///
    /// IMPORTANT:
    /// - GMCM 1.15.0 does NOT include AddSubHeader (added in 1.16.0).
    /// - If your interface includes methods that don't exist in the installed GMCM,
    ///   SMAPI will fail proxy mapping on load.
    /// </summary>
    public interface IGenericModConfigMenuApi
    {
        void Register(IManifest mod, Action reset, Action save, bool titleScreenOnly = false);

        void AddSectionTitle(IManifest mod, Func<string> text, Func<string> tooltip = null);

        void AddBoolOption(
            IManifest mod,
            Func<bool> getValue,
            Action<bool> setValue,
            Func<string> name,
            Func<string> tooltip = null,
            string fieldId = null
        );

        void AddNumberOption(
            IManifest mod,
            Func<int> getValue,
            Action<int> setValue,
            Func<string> name,
            Func<string> tooltip = null,
            int? min = null,
            int? max = null,
            int? interval = null,
            Func<int, string> formatValue = null,
            string fieldId = null
        );

        void AddPage(IManifest mod, string pageId, Func<string> pageTitle = null);
        void AddPageLink(IManifest mod, string pageId, Func<string> text, Func<string> tooltip = null);

        // Optional but safe on 1.15.0 and useful for grouping/labels.
        void AddParagraph(IManifest mod, Func<string> text);
    }
}
