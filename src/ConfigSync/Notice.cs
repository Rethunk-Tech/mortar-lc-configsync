using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace ConfigSync;

// Chat lines for a player who joined: which settings the host sets, and any mod whose version differs from the host's.
// They wait until the HUD is up, since the host's values arrive while the game is still loading.
internal sealed class Notice : MonoBehaviour
{
    private static readonly List<string> pending = new();
    private static bool joinShown;

    internal static void Add(string line) => pending.Add(line);

    internal static void Reset()
    {
        pending.Clear();
        joinShown = false;
    }

    private void Update()
    {
        HUDManager hud = HUDManager.Instance;
        NetworkManager network = NetworkManager.Singleton;
        if (hud == null || network == null || network.IsServer || !network.IsConnectedClient
            || GameNetworkManager.Instance?.localPlayerController == null)
        {
            return;
        }
        if (!joinShown)
        {
            joinShown = true;
            List<SyncedConfig> mods = SyncedConfig.All.Where(c => c.Entries.Values.Any(e => e.HostNote != null)).ToList();
            int count = mods.Sum(c => c.Entries.Values.Count(e => e.HostNote != null));
            if (count > 0)
            {
                pending.Insert(0, $"The host sets {count} of your settings ({string.Join(", ", mods.Select(c => c.Name))}).");
            }
        }
        foreach (string line in pending)
        {
            hud.AddChatMessage(line, "");
        }
        pending.Clear();
    }
}
