using System.Collections.Generic;

namespace DMMusic
{
    internal sealed class ModConfig
    {
        public int VanillaInsteadPercent { get; set; } = 0;
        public bool EnableDebugLogging { get; set; } = false;
        public bool ShowSuggestionsAlways { get; set; } = false;
        public HashSet<string> DisabledReplacementIds { get; set; } = new();
        public HashSet<string> DisabledModIds { get; set; } = new();
    }
}
