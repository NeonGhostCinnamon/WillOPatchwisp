using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace WillOPatchwisp;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    /// <summary>Open the window automatically when updates are detected.</summary>
    public bool AutoOpenOnUpdate { get; set; } = true;

    /// <summary>Print a short summary in chat when updates are detected.</summary>
    public bool PrintToChat { get; set; } = true;

    /// <summary>Also list plugins that were newly installed.</summary>
    public bool TrackNewInstalls { get; set; } = true;

    /// <summary>Look up the latest versions in your plugin repositories when the game starts.</summary>
    public bool CheckReposOnStartup { get; set; } = true;

    /// <summary>Use the dark version of the theme.</summary>
    public bool DarkMode { get; set; } = true;

    /// <summary>Maximum number of entries kept in history.</summary>
    public int MaxHistory { get; set; } = 200;

    /// <summary>Plugins (by InternalName) that are ignored: no update alerts, not in "Available" or "Update all".</summary>
    public HashSet<string> IgnoredPlugins { get; set; } = new();

    /// <summary>Last known version of every installed plugin (InternalName -> version).</summary>
    public Dictionary<string, string> KnownVersions { get; set; } = new();

    /// <summary>Detected updates, newest first.</summary>
    public List<UpdateEntry> History { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}

[Serializable]
public class UpdateEntry
{
    public string InternalName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OldVersion { get; set; }   // null = newly installed
    public string NewVersion { get; set; } = string.Empty;
    public string? Changelog { get; set; }
    public string? RepoUrl { get; set; }
    public DateTime DetectedAt { get; set; }
    public bool Seen { get; set; }
}
