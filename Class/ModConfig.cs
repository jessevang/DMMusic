using System.Collections.Generic;

namespace DMMusic
{
    internal sealed class ModConfig
    {
        public int VanillaInsteadPercent { get; set; } = 0;
        public bool EnableDebugLogging { get; set; } = false;
        public bool ShowSuggestionsAlways { get; set; } = false;

        public HashSet<string> DisabledReplacementIds { get; set; } = new();

        // NEW: Pack-level override. If a mod ID is in here, ALL its songs are treated as disabled,
        // but we DO NOT touch DisabledReplacementIds (so per-song choices are preserved).
        public HashSet<string> DisabledModIds { get; set; } = new();
    }
}
