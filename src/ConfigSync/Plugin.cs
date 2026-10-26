using BepInEx;
using BepInEx.Configuration;
using Unity.Netcode;

namespace ConfigSync;

[BepInPlugin(MyPluginInfo.Id, MyPluginInfo.Name, MyPluginInfo.Version)]
[BepInProcess("Lethal Company.exe")]
[BepInDependency("ainavt.lc.lethalconfig", BepInDependency.DependencyFlags.SoftDependency)]
public class Plugin : BaseUnityPlugin
{
    private void Awake()
    {
        Log.Source = Logger;
        ConfigEntry<bool> followRecommendations = Config.Bind(
            "General",
            "Follow host recommendations",
            true,
            "Use the host's value for settings a mod shares as a recommendation. Settings the host enforces apply either way."
        );
        SyncedConfig.FollowRecommendations = followRecommendations.Value;
        followRecommendations.SettingChanged += (_, _) =>
        {
            SyncedConfig.FollowRecommendations = followRecommendations.Value;
            SyncedConfig.RefreshAll();
        };
        // The game creates its NetworkManager once, after plugins load.
        NetworkManager.OnInstantiated += Network.Attach;
        if (NetworkManager.Singleton != null)
        {
            Network.Attach(NetworkManager.Singleton);
        }
        LethalConfigNotes.Attach();
        Logger.LogInfo($"{MyPluginInfo.Name} {MyPluginInfo.Version} loaded");
    }
}
