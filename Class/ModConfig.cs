
namespace DMMusic
{
    internal sealed class ModConfig
    {
        // Logging for showing what track is found and what track is played
        public bool EnableDebugLogging { get; set; } = true;

        // Print suggestion keys every time a track is requested (noisy, good for setup)
        public bool ShowSuggestionsAlways { get; set; } = true;
    }
}
