using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
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
    private bool recheckAfterUpdates;

    private const string Command = "/patchwisp";

    public Configuration Configuration { get; }
    public UpdateTracker Tracker { get; }
    public RepoChecker Repos { get; } = new();
    public DirectUpdater Updater { get; }
    public GitHubChangelogs GitHub { get; } = new();

    /// <summary>Nunito Black at heading size, so the title is sharp instead of a scaled-up small font.</summary>
    public IFontHandle TitleFont { get; }
    [PluginService] private static ITextureProvider TextureProvider { get; set; } = null!;

    /// <summary>The Discord logo for the footer button (null when the file is missing).</summary>
    public Dalamud.Interface.Textures.ISharedImmediateTexture? DiscordLogo { get; private set; }

    private void LoadDiscordLogo(ITextureProvider textures)
    {
        try
        {
            DiscordLogo = textures.GetFromFile(Path.Combine(PluginInterface.AssemblyLocation.DirectoryName ?? string.Empty, "images", "discord.png"));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Couldn't load the Discord logo; the footer button shows without it.");
        }
    }


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

        // v2: the dark/light switch became a theme choice (Dark, Light or Dalamud).
        if (Configuration.Version < 2)
        {
            Configuration.ThemeChoice = Configuration.DarkMode ? ThemeMode.Dark : ThemeMode.Light;
            Configuration.Version = 2;
            Configuration.Save();
        }

        var fontPath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName ?? string.Empty, "Nunito-Black.ttf");
        LoadDiscordLogo(TextureProvider);
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

        Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (recheckAfterUpdates && !Updater.IsBusy)
        {
            recheckAfterUpdates = false;
            Repos.StartCheck();
        }
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        Tracker.UpdatesDetected -= OnUpdatesDetected;
        Tracker.Dispose();
        Repos.Dispose();
        GitHub.Dispose();

        PluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleWindow;

        Chat.RemoveChatLinkHandler();
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

    // ---- chat lines with a clickable "(changelog)" link

    private uint nextLinkId = 1;
    private readonly Dictionary<uint, (string InternalName, long Ticks)?> chatLinks = new();

    /// <summary>One line per updated plugin: "Updated: name - (changelog)". More than five: the rest share one line.</summary>
    private void PrintUpdates(IReadOnlyList<UpdateEntry> updates)
    {
        foreach (var u in updates.Take(5))
            PrintLine($"Updated: {u.Name} - ", "(changelog)", (u.InternalName, u.DetectedAt.Ticks));

        if (updates.Count > 5)
            PrintLine($"and {updates.Count - 5} more - ", "(changelogs)", null);
    }

    private void PrintLine(string text, string linkText, (string InternalName, long Ticks)? target)
    {
        var id = nextLinkId++;
        chatLinks[id] = target;
        var link = Chat.AddChatLinkHandler(id, OnChatLink);

        var message = new SeStringBuilder()
            .AddText("[Will-")
            .AddUiForeground("o", PinkChatColor())
            .AddText($"-Patchwisp] {text}")
            .Add(link)
            .AddUiForeground(linkText, PinkChatColor())
            .Add(RawPayload.LinkTerminator)
            .Build();

        if (Configuration.ChatChannel == XivChatType.None)
            Chat.Print(message);
        else
            Chat.Print(new XivChatEntry { Type = Configuration.ChatChannel, Message = message });
    }

    private void OnChatLink(uint id, SeString _)
    {
        if (!chatLinks.TryGetValue(id, out var target))
            return;

        if (target is { } t)
            window.ShowEntry(t.InternalName, t.Ticks);
        else
            window.OpenOnRecent();
    }

    private void OnUpdatesDetected(IReadOnlyList<UpdateEntry> updates)
    {
        // Installed versions changed, so refresh the "available" list too. While Will-o-Patchwisp is still
        // updating plugins, wait for the last one: a check started mid-way would list the rest as available again.
        if (Configuration.CheckReposOnStartup)
        {
            if (Updater.IsBusy)
                recheckAfterUpdates = true;
            else
                Repos.StartCheck();
        }

        if (Configuration.PrintToChat)
            PrintUpdates(updates);

        if (Configuration.AutoOpenOnUpdate)
            window.IsOpen = true;
    }
}
