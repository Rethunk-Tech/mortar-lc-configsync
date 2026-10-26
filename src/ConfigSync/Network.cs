using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Netcode;

namespace ConfigSync;

// Each mod gets one named message. The host sends a mod's shared values to each player as they start syncing, and to
// everyone on the next network tick after a value changes. Players apply the keys they know and ignore the rest.
// Nothing goes back to the host.
internal static class Network
{
    private const string Prefix = "tech.rethunk.mortar.ConfigSync/";
    private const byte ProtocolVersion = 1;
    private const int MaxMessageBytes = 64 * 1024;

    private static NetworkManager? manager;
    private static readonly HashSet<string> warned = new();

    internal static void Attach(NetworkManager networkManager)
    {
        if (manager == networkManager)
        {
            return;
        }
        manager = networkManager;
        networkManager.OnServerStarted += ServerStarted;
        networkManager.OnClientStarted += ClientStarted;
        networkManager.OnClientStopped += _ => SyncedConfig.ClearAll();
    }

    // The scene manager and tick system are recreated every session.
    private static void ServerStarted()
    {
        if (manager == null)
        {
            return;
        }
        manager.NetworkTickSystem.Tick += Flush;
        if (manager.SceneManager != null)
        {
            manager.SceneManager.OnSceneEvent += SceneEvent;
        }
        else
        {
            manager.OnClientConnectedCallback += SendTo;
        }
    }

    // Players register a handler for every mod before the host can send anything.
    private static void ClientStarted()
    {
        if (manager == null || manager.IsServer)
        {
            return;
        }
        foreach (SyncedConfig config in SyncedConfig.All)
        {
            Listen(config);
        }
    }

    // Covers a mod that shares its settings after the session has already started.
    internal static void Created(SyncedConfig config)
    {
        if (manager != null && manager.IsListening && manager.IsClient && !manager.IsServer)
        {
            Listen(config);
        }
    }

    private static void Listen(SyncedConfig config)
    {
        SyncedConfig target = config;
        manager!.CustomMessagingManager.RegisterNamedMessageHandler(Prefix + config.Id, (_, reader) => Receive(target, reader));
    }

    // Sent when the host starts syncing a player, before that player's scene objects spawn, so a mod reading a shared
    // value during scene load already gets the host's.
    private static void SceneEvent(SceneEvent sceneEvent)
    {
        if (sceneEvent.SceneEventType == SceneEventType.Synchronize)
        {
            SendTo(sceneEvent.ClientId);
        }
    }

    private static void SendTo(ulong clientId)
    {
        if (manager == null || !manager.IsServer || clientId == NetworkManager.ServerClientId)
        {
            return;
        }
        foreach (SyncedConfig config in SyncedConfig.All)
        {
            Send(config, new[] { clientId });
        }
    }

    private static void Flush()
    {
        if (manager == null || !manager.IsServer)
        {
            return;
        }
        ulong[] players = manager.ConnectedClientsIds.Where(id => id != NetworkManager.ServerClientId).ToArray();
        foreach (SyncedConfig config in SyncedConfig.All.Where(c => c.Dirty))
        {
            config.Dirty = false;
            if (players.Length > 0)
            {
                Send(config, players);
            }
        }
    }

    // These run inside the game's network callbacks, so catch everything here instead of breaking them.
    private static void Send(SyncedConfig config, IReadOnlyList<ulong> clientIds)
    {
        if (config.Entries.Count == 0 || manager == null)
        {
            return;
        }
        try
        {
            using var writer = new FastBufferWriter(256, Allocator.Temp, MaxMessageBytes);
            writer.WriteValueSafe(ProtocolVersion);
            writer.WriteValueSafe(config.Version);
            writer.WriteValueSafe(config.Entries.Count);
            foreach (KeyValuePair<string, ISyncedEntry> entry in config.Entries)
            {
                writer.WriteValueSafe(entry.Key);
                writer.WriteValueSafe(entry.Value.LocalText);
            }
            manager.CustomMessagingManager.SendNamedMessage(
                Prefix + config.Id,
                clientIds,
                writer,
                NetworkDelivery.ReliableFragmentedSequenced
            );
        }
        catch (Exception e)
        {
            WarnOnce(config.Id + "/send", $"{config.Id}: couldn't send its shared settings: {e.Message}");
        }
    }

    private static void Receive(SyncedConfig config, FastBufferReader reader)
    {
        reader.ReadValueSafe(out byte protocol);
        if (protocol != ProtocolVersion)
        {
            WarnOnce(config.Id + "/protocol", $"{config.Id}: the host runs a different ConfigSync version, so its settings aren't used");
            return;
        }
        reader.ReadValueSafe(out string version);
        if (version != config.Version)
        {
            WarnOnce(config.Id + "/" + version, $"{config.Id}: the host has version {version} and you have {config.Version}");
        }
        reader.ReadValueSafe(out int count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out string key);
            reader.ReadValueSafe(out string text);
            if (!config.Entries.TryGetValue(key, out ISyncedEntry entry))
            {
                continue;
            }
            try
            {
                entry.Apply(text);
            }
            catch (Exception)
            {
                WarnOnce(config.Id + "/" + key, $"{config.Id}: couldn't read the host's value for {key.Replace('\n', '.')}");
            }
        }
    }

    private static void WarnOnce(string key, string message)
    {
        if (warned.Add(key))
        {
            Plugin.Log.LogWarning(message);
        }
    }
}
