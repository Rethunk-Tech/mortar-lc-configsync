using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Unity.Netcode;

namespace ConfigSync;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
[BepInDependency("ainavt.lc.lethalconfig", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    internal static ManualLogSource Log { get; private set; } = null!;

    private static ConfigEntry<bool> followRecommendations = null!;

    internal static bool FollowRecommendations => followRecommendations.Value;

    private void Awake()
    {
        Log = Logger;
        followRecommendations = Config.Bind(
            "General",
            "Follow host recommendations",
            true,
            "Use the host's value for settings a mod shares as a recommendation. Settings the host enforces apply either way."
        );
        followRecommendations.SettingChanged += (_, _) => SyncedConfig.RefreshAll();
        // The game creates its NetworkManager once, after plugins load.
        NetworkManager.OnInstantiated += Network.Attach;
        if (NetworkManager.Singleton != null)
        {
            Network.Attach(NetworkManager.Singleton);
        }
        LethalConfigNotes.Attach();
        Log.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded");
    }
}
