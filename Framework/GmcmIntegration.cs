using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DMMusic.Framework
{
    /// <summary>
    /// Generic Mod Config Menu integration for Music Replacement Framework.
    /// Single-page layout: General -> SMAPI options -> Music replacements list.
    /// GMCM 1.15.0+ safe (does NOT use AddSubHeader).
    /// </summary>
    internal static class GmcmIntegration
    {
        /// <param name="ensureReplacementsLoaded">
        /// Called before we enumerate replacements for the GMCM UI.
        /// Return true if replacements are ready; false if none exist (or failed).
        /// Example: () => MusicManager.TryBuildReplacementCache()
        /// </param>
        public static void RegisterIfAvailable(
            IModHelper helper,
            IMonitor monitor,
            IManifest manifest,
            Func<ModConfig> getConfig,
            Action<ModConfig> setConfig,
            Func<bool> ensureReplacementsLoaded
        )
        {
            var api = helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
            if (api == null)
            {
                monitor.Log("GMCM not found. Skipping config UI registration.", LogLevel.Trace);
                return;
            }

            string T(string key) => helper.Translation.Get(key);

            // ----------------------------------------------------------
            // Register (must be called first)
            // ----------------------------------------------------------
            api.Register(
                mod: manifest,
                reset: () => setConfig(new ModConfig()),
                save: () => helper.WriteConfig(getConfig()),
                titleScreenOnly: false
            );

            // ==========================================================
            // Section: General
            // ==========================================================
            api.AddSectionTitle(
                mod: manifest,
                text: () => T("gmcm.section.general.name"),
                tooltip: () => T("gmcm.section.general.tooltip")
            );

            api.AddNumberOption(
                mod: manifest,
                getValue: () => getConfig().VanillaInsteadPercent,
                setValue: v =>
                {
                    var cfg = getConfig();
                    cfg.VanillaInsteadPercent = v;
                    setConfig(cfg);
                },
                name: () => T("gmcm.vanillaInsteadPercent.name"),
                tooltip: () => T("gmcm.vanillaInsteadPercent.tooltip"),
                min: 0,
                max: 100,
                interval: 5,
                fieldId: "VanillaInsteadPercent"
            );

            // ==========================================================
            // Section: SMAPI Console Logs
            // ==========================================================
            api.AddSectionTitle(
                mod: manifest,
                text: () => T("gmcm.section.general.name2"),
                tooltip: () => T("gmcm.section.general.tooltip2")
            );

            api.AddBoolOption(
                mod: manifest,
                getValue: () => getConfig().EnableDebugLogging,
                setValue: v =>
                {
                    var cfg = getConfig();
                    cfg.EnableDebugLogging = v;
                    setConfig(cfg);
                },
                name: () => T("gmcm.enableDebugLogging.name"),
                tooltip: () => T("gmcm.enableDebugLogging.tooltip"),
                fieldId: "EnableDebugLogging"
            );

            api.AddBoolOption(
                mod: manifest,
                getValue: () => getConfig().ShowSuggestionsAlways,
                setValue: v =>
                {
                    var cfg = getConfig();
                    cfg.ShowSuggestionsAlways = v;
                    setConfig(cfg);
                },
                name: () => T("gmcm.showSuggestionsAlways.name"),
                tooltip: () => T("gmcm.showSuggestionsAlways.tooltip"),
                fieldId: "ShowSuggestionsAlways"
            );

            // ==========================================================
            // Section: Music replacements (NO separate page)
            // ==========================================================
            api.AddSectionTitle(
                mod: manifest,
                text: () => T("gmcm.page.replacements.title"),        // "Music replacements"
                tooltip: () => T("gmcm.page.replacements.tooltip")    // Enable/disable...
            );

            // Make sure entries are actually loaded BEFORE we build the list.
            bool ready = false;
            try
            {
                ready = ensureReplacementsLoaded?.Invoke() ?? true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed while ensuring replacement entries are loaded: {ex}", LogLevel.Warn);
            }

            var groups = MusicManager
                .GetAllReplacementEntries()
                .GroupBy(e => e.Path.SourceModName)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!ready || groups.Count == 0)
            {
                api.AddParagraph(
                    mod: manifest,
                    text: () =>
                        "No replacement entries were found.\n\n" +
                        "If you installed content packs, make sure:\n" +
                        "• the packs are loaded before GMCM registers (GameLaunched)\n" +
                        "• your MusicManager has built its replacement list\n" +
                        "• your musicReplacements.json (or pack data) is being discovered"
                );

                monitor.Log("GMCM: No replacement entries found when building UI.", LogLevel.Info);
                return;
            }

            foreach (var packGroup in groups)
            {
                string packName = string.IsNullOrWhiteSpace(packGroup.Key) ? "Unknown Mod" : packGroup.Key;

                // Use SectionTitle as a safe “group header” (GMCM 1.15 safe)
                api.AddSectionTitle(
                    mod: manifest,
                    text: () => packName,
                    tooltip: () => $"Toggle replacement files provided by {packName}."
                );

                foreach (var entry in packGroup
                    // ✅ sort by requested track name (prefix before the first '|')
                    .OrderBy(e => GetTrackName(e.Key), NaturalStringComparer.Instance)
                    // then by file path
                    .ThenBy(e => e.Path.RelativePath ?? "", NaturalStringComparer.Instance)
                    // stable tie-breaker
                    .ThenBy(e => e.Key ?? "", NaturalStringComparer.Instance))
                {
                    string key = entry.Key;
                    var path = entry.Path;

                    // Stable ID stored in config
                    string id = MusicManager.BuildReplacementId(key, path);

                    // Label: key + relative path
                    string label = $"{key}  →  {path.RelativePath}";

                    api.AddBoolOption(
                        mod: manifest,
                        getValue: () =>
                        {
                            var cfg = getConfig();
                            return !cfg.DisabledReplacementIds.Contains(id);
                        },
                        setValue: enabled =>
                        {
                            var cfg = getConfig();
                            if (enabled)
                                cfg.DisabledReplacementIds.Remove(id);
                            else
                                cfg.DisabledReplacementIds.Add(id);

                            setConfig(cfg);
                        },
                        name: () => label,
                        tooltip: () => $"Mod: {path.SourceModName}\nKey: {key}\nFile: {path.RelativePath}",
                        fieldId: $"rep::{id}"
                    );
                }
            }

            monitor.Log($"Registered GMCM options (replacements: {groups.Sum(g => g.Count())}).", LogLevel.Info);
        }

        private static string GetTrackName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            int pipe = key.IndexOf('|');
            return pipe >= 0 ? key.Substring(0, pipe) : key;
        }

        /// <summary>
        /// Natural-ish comparer so spring2 < spring10, etc.
        /// (Case-insensitive, compares digit runs numerically.)
        /// </summary>
        private sealed class NaturalStringComparer : IComparer<string>
        {
            public static readonly NaturalStringComparer Instance = new();

            public int Compare(string? a, string? b)
            {
                if (ReferenceEquals(a, b)) return 0;
                if (a is null) return -1;
                if (b is null) return 1;

                int ia = 0, ib = 0;

                while (ia < a.Length && ib < b.Length)
                {
                    char ca = a[ia];
                    char cb = b[ib];

                    // numeric chunk
                    if (char.IsDigit(ca) && char.IsDigit(cb))
                    {
                        long va = 0;
                        int sa = ia;
                        while (sa < a.Length && a[sa] == '0') sa++; // skip leading zeros for value
                        int ea = sa;
                        while (ea < a.Length && char.IsDigit(a[ea]))
                        {
                            va = (va * 10) + (a[ea] - '0');
                            ea++;
                        }

                        long vb = 0;
                        int sb = ib;
                        while (sb < b.Length && b[sb] == '0') sb++;
                        int eb = sb;
                        while (eb < b.Length && char.IsDigit(b[eb]))
                        {
                            vb = (vb * 10) + (b[eb] - '0');
                            eb++;
                        }

                        // compare numeric values first
                        int n = va.CompareTo(vb);
                        if (n != 0) return n;

                        // if equal values, shorter numeric run (including leading zeros) first
                        int lenA = ea - ia;
                        int lenB = eb - ib;
                        n = lenA.CompareTo(lenB);
                        if (n != 0) return n;

                        ia = ea;
                        ib = eb;
                        continue;
                    }

                    // text chunk (case-insensitive)
                    int ta = NextDigitIndex(a, ia);
                    int tb = NextDigitIndex(b, ib);

                    string saText = a.Substring(ia, ta - ia);
                    string sbText = b.Substring(ib, tb - ib);

                    int t = string.Compare(saText, sbText, ignoreCase: true, culture: CultureInfo.InvariantCulture);
                    if (t != 0) return t;

                    ia = ta;
                    ib = tb;
                }

                // one string ended
                return (a.Length - ia).CompareTo(b.Length - ib);
            }

            private static int NextDigitIndex(string s, int start)
            {
                int i = start;
                while (i < s.Length && !char.IsDigit(s[i]))
                    i++;
                return i;
            }
        }
    }
}
