using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BepInEx.Configuration;

namespace ConfigSync;

/// <summary>How the host's value of a shared setting applies to the other players while they're connected.</summary>
public enum Sync
{
    /// <summary>Players use the host's value unless they turned "Follow host recommendations" off.</summary>
    HostRecommended,

    /// <summary>Players always use the host's value.</summary>
    HostEnforced,
}

/// <summary>
/// One mod's shared settings. A setting stays the player's own unless the mod shares it here. While connected to a
/// host, a shared setting reads the host's value, which is never written to the player's config file.
/// </summary>
public sealed class SyncedConfig
{
    private static readonly Dictionary<string, SyncedConfig> all = new();

    internal readonly string Id;
    internal readonly string Name;
    internal readonly string Version;
    internal readonly Dictionary<string, ISyncedEntry> Entries = new();
    private readonly string sidecarPath;

    // Set when a shared value changes on the host. It's sent once on the next network tick, so a config reload that
    // changes lots of settings sends one message.
    internal bool Dirty;

    private SyncedConfig(string id, string name, string version, string configFilePath)
    {
        Id = id;
        Name = name;
        Version = version;
        sidecarPath = Path.ChangeExtension(configFilePath, ".sync.json");
    }

    internal static IEnumerable<SyncedConfig> All => all.Values;

    // The player's "Follow host recommendations" setting. It lives here, not on the plugin, so the tests can run the
    // sharing logic without Unity.
    internal static bool FollowRecommendations { get; set; } = true;

    /// <summary>The shared settings of the plugin, created on first use.</summary>
    public static SyncedConfig For(BaseUnityPlugin plugin)
    {
        BepInPlugin info = plugin.Info.Metadata;
        if (!all.TryGetValue(info.GUID, out SyncedConfig config))
        {
            config = new SyncedConfig(info.GUID, info.Name, info.Version.ToString(), plugin.Config.ConfigFilePath);
            all[info.GUID] = config;
            Network.Created(config);
        }
        return config;
    }

    /// <summary>Shares a setting at the given level. Read it through the returned entry's Value.</summary>
    public SyncedEntry<T> Share<T>(ConfigEntry<T> entry, Sync level = Sync.HostEnforced)
    {
        string key = Key(entry.Definition);
        if (Entries.TryGetValue(key, out ISyncedEntry existing) && existing is SyncedEntry<T> same && same.Local == entry)
        {
            return same;
        }
        var synced = new SyncedEntry<T>(entry, level);
        Entries[key] = synced;
        entry.SettingChanged += (_, _) =>
        {
            synced.LocalChanged();
            Dirty = true;
        };
        WriteSidecar();
        Dirty = true;
        return synced;
    }

    internal static string Key(ConfigDefinition definition) => definition.Section + "\n" + definition.Key;

    // Raised when a shared setting starts using the host's value, stops, or the host's value changes, so a config menu
    // can show what's in use.
    internal static event Action<ISyncedEntry>? StateChanged;

    internal static void RaiseStateChanged(ISyncedEntry entry) => StateChanged?.Invoke(entry);

    internal static ISyncedEntry? Find(ConfigEntryBase entry) =>
        all.Values.SelectMany(c => c.Entries.Values).FirstOrDefault(e => e.LocalEntry == entry);

    internal static void ClearAll() => ForEachEntry(e => e.Clear());

    internal static void RefreshAll() => ForEachEntry(e => e.Refresh());

    private static void ForEachEntry(Action<ISyncedEntry> action)
    {
        foreach (SyncedConfig config in all.Values)
        {
            foreach (ISyncedEntry entry in config.Entries.Values)
            {
                action(entry);
            }
        }
    }

    // Lets Mortar and other tools see which settings this mod shares. No values go in it.
    private void WriteSidecar()
    {
        var json = new StringBuilder("{\"version\":1,\"settings\":[");
        json.Append(string.Join(",", Entries.Select(e =>
        {
            string[] parts = e.Key.Split('\n');
            string level = e.Value.Level == Sync.HostEnforced ? "hostEnforced" : "hostRecommended";
            return $"{{\"section\":{Quote(parts[0])},\"key\":{Quote(parts[1])},\"level\":\"{level}\"}}";
        })));
        json.Append("]}\n");
        string text = json.ToString();
        try
        {
            if (!File.Exists(sidecarPath) || File.ReadAllText(sidecarPath) != text)
            {
                File.WriteAllText(sidecarPath, text);
            }
        }
        catch (IOException e)
        {
            Log.Source?.LogWarning($"{Id}: couldn't write {sidecarPath}: {e.Message}");
        }
    }

    private static string Quote(string value) => "\"" + string.Concat(value.Select(c =>
        c == '\\' ? "\\\\" : c == '"' ? "\\\"" : c < ' ' ? "\\u" + ((int)c).ToString("x4") : c.ToString())) + "\"";
}

internal interface ISyncedEntry
{
    Sync Level { get; }
    ConfigEntryBase LocalEntry { get; }
    // Why the player's own value isn't being used right now, or null if it is.
    string? HostNote { get; }
    // The host's value while it's the one in use, so a config menu can show it instead of the player's own.
    object? HostValue { get; }
    string LocalText { get; }
    void Apply(string text);
    void Clear();
    void Refresh();
}

/// <summary>A shared setting: the player's own value, or the host's while connected.</summary>
public sealed class SyncedEntry<T> : ISyncedEntry
{
    private string? hostText;
    private T hostValue = default!;

    internal SyncedEntry(ConfigEntry<T> local, Sync level)
    {
        Local = local;
        Level = level;
    }

    /// <summary>The player's own setting, as written in their config file.</summary>
    public ConfigEntry<T> Local { get; }

    /// <summary>How the host's value applies.</summary>
    public Sync Level { get; }

    /// <summary>Whether the host's value is in use now.</summary>
    public bool FromHost { get; private set; }

    /// <summary>The value to use: the host's while connected and the level allows it, else the player's own.</summary>
    public T Value => FromHost ? hostValue : Local.Value;

    /// <summary>Raised when Value changes, from either side.</summary>
    public event Action? Changed;

    ConfigEntryBase ISyncedEntry.LocalEntry => Local;

    object? ISyncedEntry.HostValue => FromHost ? hostValue : null;

    string? ISyncedEntry.HostNote => !FromHost ? null : Level == Sync.HostEnforced
        ? $"The host sets this to {hostText} while you're connected."
        : $"You follow the host's recommendation of {hostText} while you're connected. Turn off \"Follow host recommendations\" in ConfigSync to use your own value.";

    // Send strings raw. BepInEx's converter doesn't escape backslashes.
    string ISyncedEntry.LocalText => Local.Value is string text
        ? text
        : TomlTypeConverter.ConvertToString(Local.Value, typeof(T));

    void ISyncedEntry.Apply(string text)
    {
        T value = typeof(T) == typeof(string) ? (T)(object)text : (T)TomlTypeConverter.ConvertToValue(text, typeof(T));
        // The host might run a different version of the mod with a wider range, so clamp to ours.
        if (Local.Description.AcceptableValues is { } acceptable)
        {
            value = (T)acceptable.Clamp(value);
        }
        T before = Value;
        hostText = text;
        hostValue = value;
        Update(before);
    }

    void ISyncedEntry.Clear()
    {
        T before = Value;
        hostText = null;
        Update(before);
    }

    void ISyncedEntry.Refresh() => Update(Value);

    internal void LocalChanged()
    {
        if (!FromHost)
        {
            RaiseChanged();
        }
    }

    private void Update(T before)
    {
        bool wasFromHost = FromHost;
        FromHost = hostText != null && (Level == Sync.HostEnforced || SyncedConfig.FollowRecommendations);
        if (!EqualityComparer<T>.Default.Equals(before, Value))
        {
            RaiseChanged();
        }
        // While the player's own value is in use, a config menu may hold edits they haven't saved yet.
        if (wasFromHost || FromHost)
        {
            SyncedConfig.RaiseStateChanged(this);
        }
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Source?.LogError($"{Local.Definition}: a Changed handler threw: {e}");
        }
    }
}
