using System;
using System.IO;
using BepInEx.Configuration;
using Xunit;

namespace ConfigSync.Tests;

public sealed class SyncedEntryTests : IDisposable
{
    private readonly DirectoryInfo dir = Directory.CreateTempSubdirectory("configsync-test-");
    private readonly ConfigFile file;

    public SyncedEntryTests() => file = new ConfigFile(Path.Combine(dir.FullName, "test.cfg"), false);

    public void Dispose()
    {
        SyncedConfig.FollowRecommendations = true;
        dir.Delete(true);
    }

    private SyncedEntry<T> Share<T>(string key, T own, Sync level, AcceptableValueBase? acceptable = null) =>
        new(file.Bind("General", key, own, new ConfigDescription("", acceptable)), level);

    [Fact]
    public void HostValueAppliesUntilCleared()
    {
        SyncedEntry<int> entry = Share("Distance", 5, Sync.HostEnforced);
        int changed = 0;
        entry.Changed += () => changed++;

        ((ISyncedEntry)entry).Apply("8");
        Assert.True(entry.FromHost);
        Assert.Equal(8, entry.Value);
        Assert.Equal(5, entry.Local.Value);

        // The host sending the same value again isn't a change.
        ((ISyncedEntry)entry).Apply("8");
        ((ISyncedEntry)entry).Clear();
        Assert.False(entry.FromHost);
        Assert.Equal(5, entry.Value);
        Assert.Equal(2, changed);
    }

    [Fact]
    public void HostValueEqualToOwnIsNoChange()
    {
        SyncedEntry<int> entry = Share("Distance", 5, Sync.HostEnforced);
        int changed = 0;
        entry.Changed += () => changed++;

        ((ISyncedEntry)entry).Apply("5");
        Assert.True(entry.FromHost);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void HostValueIsClampedToOurRange()
    {
        SyncedEntry<int> entry = Share("Distance", 5, Sync.HostEnforced, new AcceptableValueRange<int>(1, 10));

        ((ISyncedEntry)entry).Apply("50");
        Assert.Equal(10, entry.Value);
    }

    [Fact]
    public void ValuesTravelAsText()
    {
        SyncedEntry<string> path = Share("Path", @"C:\mods", Sync.HostEnforced);
        SyncedEntry<Sync> level = Share("Level", Sync.HostEnforced, Sync.HostEnforced);
        SyncedEntry<float> scale = Share("Scale", 1.5f, Sync.HostEnforced);

        Assert.Equal(@"C:\mods", ((ISyncedEntry)path).LocalText);
        ((ISyncedEntry)path).Apply(@"D:\x\y");
        Assert.Equal(@"D:\x\y", path.Value);
        ((ISyncedEntry)level).Apply(((ISyncedEntry)Share("Other", Sync.HostRecommended, Sync.HostEnforced)).LocalText);
        Assert.Equal(Sync.HostRecommended, level.Value);
        ((ISyncedEntry)scale).Apply(((ISyncedEntry)Share("Other2", 0.25f, Sync.HostEnforced)).LocalText);
        Assert.Equal(0.25f, scale.Value);
    }

    [Fact]
    public void RecommendationsApplyOnlyWhileFollowed()
    {
        SyncedEntry<int> entry = Share("Distance", 5, Sync.HostRecommended);
        ((ISyncedEntry)entry).Apply("8");
        Assert.Equal(8, entry.Value);

        SyncedConfig.FollowRecommendations = false;
        ((ISyncedEntry)entry).Refresh();
        Assert.False(entry.FromHost);
        Assert.Equal(5, entry.Value);
    }
}
