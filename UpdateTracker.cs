using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace WillOPatchwisp;

/// <summary>
/// Compares the installed plugin list against the last known versions and records any changes.
/// Runs once on load (catches updates that happened while the game was closed / at boot)
/// and again shortly after Dalamud reports that the plugin list changed (catches in-game updates).
/// </summary>
public sealed class UpdateTracker : IDisposable
{
    // Wait a moment after a change event so a batch of updates is reported together.
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly Plugin plugin;
    private DateTime? pendingCheckAt;

    public event Action<IReadOnlyList<UpdateEntry>>? UpdatesDetected;

    public UpdateTracker(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.PluginInterface.ActivePluginsChanged += OnActivePluginsChanged;
        Plugin.Framework.Update += OnFrameworkUpdate;

        // Initial check a few seconds after load, so other plugins have finished loading.
        pendingCheckAt = DateTime.UtcNow + Debounce;
    }

    public void Dispose()
    {
        Plugin.PluginInterface.ActivePluginsChanged -= OnActivePluginsChanged;
        Plugin.Framework.Update -= OnFrameworkUpdate;
    }

    private void OnActivePluginsChanged(IActivePluginsChangedEventArgs args)
        => pendingCheckAt = DateTime.UtcNow + Debounce;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingCheckAt is { } at && DateTime.UtcNow >= at)
        {
            pendingCheckAt = null;
            CheckNow();
        }
    }

    public void CheckNow()
    {
        try
        {
            var config = plugin.Configuration;
            var firstRun = config.KnownVersions.Count == 0;
            var now = DateTime.Now;
            var found = new List<UpdateEntry>();
            var current = new Dictionary<string, string>();

            foreach (var p in Plugin.PluginInterface.InstalledPlugins)
            {
                if (p.IsDev)
                    continue;

                var version = p.Version.ToString();
                current[p.InternalName] = version;

                config.KnownVersions.TryGetValue(p.InternalName, out var oldVersion);
                if (firstRun || oldVersion == version)
                    continue;
                if (oldVersion == null && !config.TrackNewInstalls)
                    continue;
                if (config.IgnoredPlugins.Contains(p.InternalName))
                    continue;

                string? changelog = null, repo = null;
                try
                {
                    changelog = p.Manifest.Changelog;
                    repo = p.Manifest.RepoUrl;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Debug(ex, $"Could not read manifest for {p.InternalName}");
                }

                found.Add(new UpdateEntry
                {
                    InternalName = p.InternalName,
                    Name = p.Name,
                    OldVersion = oldVersion,
                    NewVersion = version,
                    Changelog = string.IsNullOrWhiteSpace(changelog) ? null : changelog.Trim(),
                    RepoUrl = string.IsNullOrWhiteSpace(repo) ? null : repo,
                    DetectedAt = now,
                });
            }

            // Keep versions of plugins that are temporarily missing (e.g. mid-update) instead of forgetting them.
            var changed = found.Count > 0;
            foreach (var (name, version) in current)
            {
                if (!config.KnownVersions.TryGetValue(name, out var known) || known != version)
                {
                    config.KnownVersions[name] = version;
                    changed = true;
                }
            }

            if (found.Count > 0)
            {
                config.History.InsertRange(0, found.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
                if (config.History.Count > config.MaxHistory)
                    config.History.RemoveRange(config.MaxHistory, config.History.Count - config.MaxHistory);
            }

            // Only write the settings file when something changed (it holds the whole history).
            if (changed)
                config.Save();

            if (found.Count > 0)
                UpdatesDetected?.Invoke(found);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to check for plugin updates");
        }
    }
}
