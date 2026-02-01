using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DMMusic.Framework
{
    internal static class GmcmIntegration
    {
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

            api.Register(
                mod: manifest,
                reset: () => setConfig(new ModConfig()),
                save: () => helper.WriteConfig(getConfig()),
                titleScreenOnly: false
            );

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

            api.AddSectionTitle(
                mod: manifest,
                text: () => T("gmcm.page.replacements.title"),
                tooltip: () => T("gmcm.page.replacements.tooltip")
            );

            bool ready = false;
            try
            {
                ready = ensureReplacementsLoaded?.Invoke() ?? true;
            }
            catch (Exception ex)
            {
                monitor.Log($"Failed while ensuring replacement entries are loaded: {ex}", LogLevel.Warn);
            }

            var allEntries = MusicManager.GetAllReplacementEntries().ToList();

            var groups = allEntries
                .GroupBy(e => e.Path.SourceModId, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var first = g.First();
                    string name = string.IsNullOrWhiteSpace(first.Path.SourceModName) ? g.Key : first.Path.SourceModName;
                    return new PackGroup(g.Key, name, g.ToList());
                })
                .OrderBy(g => g.SourceModName, StringComparer.OrdinalIgnoreCase)
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
                string packId = packGroup.SourceModId;
                string packName = string.IsNullOrWhiteSpace(packGroup.SourceModName) ? "Unknown Mod" : packGroup.SourceModName;

                api.AddSectionTitle(
                    mod: manifest,
                    text: () => packName,
                    tooltip: () => $"Toggle replacement files provided by {packName}."
                );

                api.AddBoolOption(
                    mod: manifest,
                    getValue: () =>
                    {
                        var cfg = getConfig();
                        return !cfg.DisabledModIds.Contains(packId);
                    },
                    setValue: enabled =>
                    {
                        var cfg = getConfig();
                        if (enabled)
                            cfg.DisabledModIds.Remove(packId);
                        else
                            cfg.DisabledModIds.Add(packId);

                        setConfig(cfg);
                    },
                    name: () => T("gmcm.replacements.packToggle.name"),
                    tooltip: () => string.Format(T("gmcm.replacements.packToggle.tooltip"), packName, packId),
                    fieldId: $"pack::{packId}"
                );

                bool packDisabled = false;
                try
                {
                    packDisabled = getConfig().DisabledModIds.Contains(packId);
                }
                catch { }

                foreach (var entry in packGroup.Entries
                    .OrderBy(e => GetTrackName(e.Key), NaturalStringComparer.Instance)
                    .ThenBy(e => e.Path.RelativePath ?? "", NaturalStringComparer.Instance)
                    .ThenBy(e => e.Key ?? "", NaturalStringComparer.Instance))
                {
                    string key = entry.Key;
                    var path = entry.Path;

                    string id = MusicManager.BuildReplacementId(key, path);
                    string label = $"{key}  →  {path.RelativePath}";

                    api.AddBoolOption(
                        mod: manifest,
                        // IMPORTANT: Individual checkbox reflects ONLY its own saved state.
                        // We do NOT force it off when the pack is disabled.
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
                        tooltip: () =>
                        {
                            string baseTip = $"Mod: {path.SourceModName}\nKey: {key}\nFile: {path.RelativePath}";
                            if (packDisabled)
                                baseTip += "\n\nNote: This mod is currently DISABLED above, so none of its songs will play until re-enabled.";
                            return baseTip;
                        },
                        fieldId: $"rep::{id}"
                    );
                }
            }

            monitor.Log($"Registered GMCM options (replacements: {groups.Sum(g => g.Entries.Count)}).", LogLevel.Info);
        }

        private static string GetTrackName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return "";

            int pipe = key.IndexOf('|');
            return pipe >= 0 ? key.Substring(0, pipe) : key;
        }

        private sealed record PackGroup(string SourceModId, string SourceModName, List<(string Key, MusicPath Path)> Entries);

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

                    if (char.IsDigit(ca) && char.IsDigit(cb))
                    {
                        long va = 0;
                        int sa = ia;
                        while (sa < a.Length && a[sa] == '0') sa++;
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

                        int n = va.CompareTo(vb);
                        if (n != 0) return n;

                        int lenA = ea - ia;
                        int lenB = eb - ib;
                        n = lenA.CompareTo(lenB);
                        if (n != 0) return n;

                        ia = ea;
                        ib = eb;
                        continue;
                    }

                    int ta = NextDigitIndex(a, ia);
                    int tb = NextDigitIndex(b, ib);

                    string saText = a.Substring(ia, ta - ia);
                    string sbText = b.Substring(ib, tb - ib);

                    int t = string.Compare(saText, sbText, ignoreCase: true, culture: CultureInfo.InvariantCulture);
                    if (t != 0) return t;

                    ia = ta;
                    ib = tb;
                }

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
