using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using WillOPatchwisp.Windows;

namespace WillOPatchwisp;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;

    private ushort? pinkChatColor;

    private const string Command = "/patchwisp";

    public Configuration Configuration { get; }
    public UpdateTracker Tracker { get; }
    public RepoChecker Repos { get; } = new();
    public DirectUpdater Updater { get; }
    public GitHubChangelogs GitHub { get; } = new();

    /// <summary>Nunito Black at heading size, so the title is sharp instead of a scaled-up small font.</summary>
    public IFontHandle TitleFont { get; }

    private readonly WindowSystem windowSystem = new("WillOPatchwisp");
    private readonly UpdatesWindow window;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // v1: dark mode became the default theme; switch existing setups over once.
        if (Configuration.Version < 1)
        {
            Configuration.DarkMode = true;
            Configuration.Version = 1;
            Configuration.Save();
        }

        var fontPath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, "Nunito-Black.ttf");
        TitleFont = PluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            if (File.Exists(fontPath))
                tk.AddFontFromFile(fontPath, new SafeFontConfig { SizePx = 30 });
            else
                tk.AddDalamudDefaultFont(30);
        }));

        Updater = new DirectUpdater(this);
        window = new UpdatesWindow(this);
        windowSystem.AddWindow(window);

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Show the changelogs of recently updated plugins.",
        });

        PluginInterface.UiBuilder.Draw += windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleWindow;

        Tracker = new UpdateTracker(this);
        Tracker.UpdatesDetected += OnUpdatesDetected;

        if (Configuration.CheckReposOnStartup)
            Repos.StartCheck();
    }

    public void Dispose()
    {
        Tracker.UpdatesDetected -= OnUpdatesDetected;
        Tracker.Dispose();
        Repos.Dispose();
        GitHub.Dispose();

        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;

        windowSystem.RemoveAllWindows();
        TitleFont.Dispose();
        CommandManager.RemoveHandler(Command);
    }

    private void OnCommand(string command, string args) => ToggleWindow();

    public void ToggleWindow() => window.Toggle();

    public bool IsIgnored(string internalName) => Configuration.IgnoredPlugins.Contains(internalName);

    /// <summary>Available updates, minus ignored plugins.</summary>
    public List<AvailableUpdate> VisibleUpdates => Repos.Updates.Where(u => !IsIgnored(u.InternalName)).ToList();

    public void SetIgnored(string internalName, bool ignored)
    {
        if (ignored ? Configuration.IgnoredPlugins.Add(internalName) : Configuration.IgnoredPlugins.Remove(internalName))
            Configuration.Save();
    }

    /// <summary>The game's chat colour closest to the website pink (#FF7EB6), looked up once.</summary>
    private ushort PinkChatColor()
    {
        if (pinkChatColor is { } cached)
            return cached;

        ushort best = 0;
        var bestDist = double.MaxValue;
        try
        {
            foreach (var row in Data.GetExcelSheet<UIColor>())
            {
                var c = row.Dark; // 0xRRGGBBAA
                if (c == 0 || row.RowId > ushort.MaxValue)
                    continue;
                double r = (c >> 24) & 0xFF, g = (c >> 16) & 0xFF, b = (c >> 8) & 0xFF;
                var d = (r - 255) * (r - 255) + (g - 126) * (g - 126) + (b - 182) * (b - 182);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = (ushort)row.RowId;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't read UIColor sheet");
        }

        pinkChatColor = best;
        return best;
    }

    private void OnUpdatesDetected(IReadOnlyList<UpdateEntry> updates)
    {
        // Installed versions changed, so refresh the "available" list too.
        if (Configuration.CheckReposOnStartup)
            Repos.StartCheck();

        if (Configuration.PrintToChat)
        {
            var names = string.Join(", ", updates.Take(5).Select(u => u.Name));
            if (updates.Count > 5)
                names += $" and {updates.Count - 5} more";
            Chat.Print(new SeStringBuilder()
                .AddText("[Will-")
                .AddUiForeground("o", PinkChatColor())
                .AddText($"-Patchwisp] {updates.Count} plugin(s) updated: {names}. Type {Command} for changelogs.")
                .Build());
        }

        if (Configuration.AutoOpenOnUpdate)
            window.IsOpen = true;
    }
}
