using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace WillOPatchwisp;

/// <summary>A plugin whose repository has a newer version than the one installed.</summary>
public sealed record AvailableUpdate(
    string InternalName,
    string Name,
    string InstalledVersion,
    string LatestVersion,
    string? Changelog,
    DateTime? ReleasedAt,
    string? RepoUrl,
    bool IsTesting);

/// <summary>
/// Downloads the plugin lists (pluginmaster JSON) of every repository your plugins were installed from
/// and compares them with what is installed.
/// </summary>
public sealed class RepoChecker : IDisposable
{
    private const string MainRepoUrl = "https://kamori.goats.dev/Plugin/PluginMaster";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly CancellationTokenSource cts = new();
    private int running;

    public IReadOnlyList<AvailableUpdate> Updates { get; private set; } = Array.Empty<AvailableUpdate>();
    public IReadOnlyList<string> Errors { get; private set; } = Array.Empty<string>();
    public DateTime? LastChecked { get; private set; }
    public bool IsRunning => running == 1;

    public void Dispose()
    {
        cts.Cancel();
        http.Dispose();
        cts.Dispose();
    }

    /// <summary>Removes a plugin from the "available" list right after it was updated.</summary>
    public void MarkUpdated(string internalName)
        => Updates = Updates.Where(u => u.InternalName != internalName).ToList();

    public void StartCheck()
    {
        if (Interlocked.Exchange(ref running, 1) == 1)
            return;

        // Snapshot installed plugins on the calling (framework/UI) thread.
        var installed = Plugin.PluginInterface.InstalledPlugins
            .Where(p => !p.IsDev)
            .Select(p => new Installed(
                p.InternalName,
                p.Name,
                p.Version,
                p.IsTesting,
                SafeGet(() => p.Manifest.InstalledFromUrl),
                SafeGet(() => p.Manifest.RepoUrl),
                SafeGet(() => p.Manifest.DalamudApiLevel)))
            .ToList();

        Task.Run(async () =>
        {
            try
            {
                await RunAsync(installed, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, "Update check failed");
                Errors = new[] { ex.Message };
            }
            finally
            {
                LastChecked = DateTime.Now;
                Interlocked.Exchange(ref running, 0);
            }
        });
    }

    private async Task RunAsync(List<Installed> installed, CancellationToken token)
    {
        var updates = new List<AvailableUpdate>();
        var errors = new List<string>();

        var byRepo = installed
            .Where(p => !string.IsNullOrEmpty(p.Source) && p.Source != "DEVPLUGIN")
            .GroupBy(p => p.Source == "OFFICIAL" ? MainRepoUrl : p.Source!);

        foreach (var group in byRepo)
        {
            token.ThrowIfCancellationRequested();

            List<RemoteManifest>? remote;
            try
            {
                var json = await http.GetStringAsync(group.Key, token).ConfigureAwait(false);
                remote = JsonSerializer.Deserialize<List<RemoteManifest>>(json, JsonOptions);
            }
            catch (HttpRequestException ex)
            {
                errors.Add(ex.StatusCode is { } code ? $"{ShortUrl(group.Key)} ({(int)code})" : ShortUrl(group.Key));
                Plugin.Log.Warning($"Repository {group.Key} failed: {ex.Message}");
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                errors.Add(ex is OperationCanceledException ? $"{ShortUrl(group.Key)} (timed out)" : $"{ShortUrl(group.Key)} (unreadable)");
                Plugin.Log.Warning(ex, $"Repository {group.Key} failed");
                continue;
            }

            if (remote == null)
                continue;

            var lookup = remote
                .Where(r => !string.IsNullOrEmpty(r.InternalName))
                .GroupBy(r => r.InternalName!)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var p in group)
            {
                if (!lookup.TryGetValue(p.InternalName, out var r))
                    continue;

                // Stable channel
                var candidate = Parse(r.AssemblyVersion);
                var changelog = r.Changelog;
                var testing = false;

                // Testing channel, if this plugin is installed as a testing build
                var testVersion = Parse(r.TestingAssemblyVersion);
                if (p.IsTesting && testVersion != null && (candidate == null || testVersion > candidate))
                {
                    candidate = testVersion;
                    changelog = r.TestingChangelog ?? r.Changelog;
                    testing = true;
                }

                if (candidate == null || candidate <= p.Version)
                    continue;

                // Dalamud won't install a build made for an older API level.
                var apiLevel = testing ? r.TestingDalamudApiLevel ?? r.DalamudApiLevel : r.DalamudApiLevel;
                if (apiLevel is { } lvl && p.ApiLevel is { } mine && lvl < mine)
                    continue;

                updates.Add(new AvailableUpdate(
                    p.InternalName,
                    p.Name,
                    p.Version.ToString(),
                    candidate.ToString(),
                    string.IsNullOrWhiteSpace(changelog) ? null : changelog.Trim(),
                    r.LastUpdate > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.LastUpdate).LocalDateTime : null,
                    p.RepoUrl ?? r.RepoUrl,
                    testing));
            }
        }

        Updates = updates.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Errors = errors;
    }

    private static Version? Parse(string? v) => Version.TryParse(v, out var parsed) ? parsed : null;

    private static T? SafeGet<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default; }
    }

    private static string ShortUrl(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    private sealed record Installed(
        string InternalName, string Name, Version Version, bool IsTesting,
        string? Source, string? RepoUrl, int? ApiLevel);

    private sealed class RemoteManifest
    {
        public string? InternalName { get; set; }
        public string? AssemblyVersion { get; set; }
        public string? TestingAssemblyVersion { get; set; }
        public string? Changelog { get; set; }
        public string? TestingChangelog { get; set; }
        public long LastUpdate { get; set; }
        public int? DalamudApiLevel { get; set; }
        public int? TestingDalamudApiLevel { get; set; }
        public string? RepoUrl { get; set; }
    }
}
