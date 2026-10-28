using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

namespace ConfigSync;

// Tells a player who joined, in chat, how many settings the host sets and which mods' versions differ from the host's.
// It waits for the HUD, since the host's values arrive while the game is still loading.
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
            var mods = SyncedConfig.All.Where(c => c.Entries.Values.Any(e => e.HostNote != null)).ToList();
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
