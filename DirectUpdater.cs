using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;

namespace WillOPatchwisp;

public enum UpdateStateKind
{
    Updating,
    Updated,
    Failed,
    NotFound,
}

public sealed record UpdateState(UpdateStateKind Kind, string? Message = null);

/// <summary>
/// Installs plugin updates directly by calling Dalamud's own internal updater
/// (PluginManager.UpdateSinglePluginAsync) through reflection.
///
/// This is NOT public Dalamud API: Dalamud doesn't offer a supported way for one plugin to update another.
/// It's fine for a personal plugin, but it can break when Dalamud changes. If anything doesn't
/// resolve, <see cref="IsAvailable"/> is false and the UI falls back to opening Dalamud's installer.
/// </summary>
public sealed class DirectUpdater
{
    private readonly Plugin plugin;
    private readonly object? pluginManager;
    private readonly PropertyInfo? updatablePlugins;
    private readonly MethodInfo? updateSingle;
    private readonly MethodInfo? detectUpdates;
    private readonly MethodInfo? reloadRepos;
    private DateTime lastReload = DateTime.MinValue;
    private readonly PropertyInfo? installedPluginProp;
    private readonly PropertyInfo? internalNameProp;
    private readonly SemaphoreSlim gate = new(1, 1);

    private int queued;
    private int done;

    /// <summary>Per-plugin state (by InternalName) for the UI.</summary>
    public ConcurrentDictionary<string, UpdateState> States { get; } = new();

    public bool IsAvailable { get; }
    public bool IsBusy => queued > 0;
    public int BatchTotal => queued + done;
    public int BatchDone => done;

    public DirectUpdater(Plugin plugin)
    {
        this.plugin = plugin;
        try
        {
            var asm = typeof(IDalamudPluginInterface).Assembly;
            var pmType = asm.GetType("Dalamud.Plugin.Internal.PluginManager", true)!;
            var service = asm.GetType("Dalamud.Service`1", true)!.MakeGenericType(pmType);
            pluginManager = service.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);

            const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            updatablePlugins = pmType.GetProperty("UpdatablePlugins", inst);
            updateSingle = pmType.GetMethod("UpdateSinglePluginAsync", inst);
            detectUpdates = pmType.GetMethod("DetectAvailablePluginUpdates", inst);
            reloadRepos = pmType.GetMethod("ReloadAllReposAsync", inst, Type.EmptyTypes);

            var updType = asm.GetType("Dalamud.Plugin.Internal.Types.AvailablePluginUpdate", true)!;
            installedPluginProp = updType.GetProperty("InstalledPlugin", inst);
            internalNameProp = installedPluginProp?.PropertyType.GetProperty("InternalName", inst);

            IsAvailable = pluginManager != null && updatablePlugins != null && updateSingle != null
                          && updateSingle.GetParameters().Length == 3
                          && installedPluginProp != null && internalNameProp != null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Direct updating isn't available with this Dalamud version; falling back to the installer.");
            IsAvailable = false;
        }
    }

    public void Update(string internalName) => UpdateMany(new[] { internalName });

    public void UpdateMany(IReadOnlyCollection<string> internalNames)
    {
        var todo = internalNames
            .Where(n => !States.TryGetValue(n, out var s) || s.Kind != UpdateStateKind.Updating)
            .ToList();
        if (todo.Count == 0)
            return;

        if (queued == 0)
            done = 0;

        foreach (var n in todo)
            States[n] = new UpdateState(UpdateStateKind.Updating);
        Interlocked.Add(ref queued, todo.Count);

        Task.Run(async () =>
        {
            foreach (var name in todo)
            {
                // One at a time, like Dalamud's own "Update all".
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    States[name] = await UpdateOneAsync(name).ConfigureAwait(false);
                    if (States[name].Kind == UpdateStateKind.Updated)
                        plugin.Repos.MarkUpdated(name);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Error(ex, $"Updating {name} failed");
                    States[name] = new UpdateState(UpdateStateKind.Failed, ex.InnerException?.Message ?? ex.Message);
                }
                finally
                {
                    gate.Release();
                    Interlocked.Decrement(ref queued);
                    Interlocked.Increment(ref done);
                }
            }
        });
    }

    private async Task<UpdateState> UpdateOneAsync(string internalName)
    {
        var update = FindUpdate(internalName);
        if (update == null && reloadRepos != null && DateTime.UtcNow - lastReload > TimeSpan.FromMinutes(1))
        {
            // Our repo check usually spots an update before Dalamud does: Dalamud only knows the plugin lists it
            // downloaded earlier. Have it download them again (once per minute at most, so "Update all" doesn't
            // re-download them for every plugin), which also refreshes its list of available updates.
            lastReload = DateTime.UtcNow;
            await ((Task)reloadRepos.Invoke(pluginManager, null)!).ConfigureAwait(false);
            update = FindUpdate(internalName);
        }
        if (update == null && detectUpdates != null)
        {
            detectUpdates.Invoke(pluginManager, null);
            update = FindUpdate(internalName);
        }

        if (update == null)
            return new UpdateState(UpdateStateKind.NotFound, "Dalamud hasn't picked up this update yet.");

        // UpdateSinglePluginAsync(update, notify: true, dryRun: false)
        var task = (Task)updateSingle!.Invoke(pluginManager, new[] { update, true, (object)false })!;
        await task.ConfigureAwait(false);

        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        var status = result?.GetType().GetProperty("Status")?.GetValue(result)?.ToString() ?? "Unknown";

        return status switch
        {
            "Success" or "AlreadyUpToDate" => new UpdateState(UpdateStateKind.Updated),
            "FailedDownload" => new UpdateState(UpdateStateKind.Failed, "Download failed."),
            "FailedUnload" => new UpdateState(UpdateStateKind.Failed, "The old version couldn't be unloaded. Try again after a restart."),
            "FailedLoad" => new UpdateState(UpdateStateKind.Failed, "The new version was installed but failed to load. Check /xllog."),
            _ => new UpdateState(UpdateStateKind.Failed, $"Dalamud reported: {status}"),
        };
    }

    private object? FindUpdate(string internalName)
    {
        if (updatablePlugins?.GetValue(pluginManager) is not IEnumerable list)
            return null;

        foreach (var u in list)
        {
            var installed = installedPluginProp!.GetValue(u);
            if (installed != null && internalNameProp!.GetValue(installed) as string == internalName)
                return u;
        }

        return null;
    }
}
