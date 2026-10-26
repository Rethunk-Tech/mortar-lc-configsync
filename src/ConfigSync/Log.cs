using BepInEx.Logging;

namespace ConfigSync;

// The plugin's log lives here, not on the plugin class, because touching that class loads Unity and the tests run the
// sharing code without it.
internal static class Log
{
    internal static ManualLogSource? Source { get; set; }
}
