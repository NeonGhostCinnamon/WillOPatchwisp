using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Utility;

namespace WillOPatchwisp.Windows;

public class UpdatesWindow : Window
{
    private readonly Plugin plugin;
    private string search = string.Empty;
    private bool showSettings;

    // Cards the user has opened/closed by hand (otherwise the card's default applies).
    private readonly Dictionary<string, bool> expanded = new();

    private List<InstalledInfo>? installedCache;
    private DateTime installedCacheAt;

    private sealed record InstalledInfo(
        string InternalName, string Name, string Version, string? Changelog,
        DateTime? ReleasedAt, string? RepoUrl, bool IsTesting);

    private static float S(float v) => v * ImGuiHelpers.GlobalScale;

    public UpdatesWindow(Plugin plugin)
        // No standard title bar: the header draws its own round buttons (theme + close) and can be dragged.
        : base("Will-o-Patchwisp##WillOPatchwispMain", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove)
    {
        this.plugin = plugin;
        Size = new Vector2(600, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = NormalConstraints;
    }

    // ---- dragging (no title bar) and minimizing

    private static readonly WindowSizeConstraints NormalConstraints = new()
    {
        MinimumSize = new Vector2(300, 260),
        MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
    };

    private bool dragging;
    private Vector2 dragOffset;        // mouse position relative to the window's top-left when the drag started
    private bool minimized;
    private Vector2 restoreSize;       // window size (in pixels) before minimizing
    private Vector2? sizeOnce;         // apply this size for one frame (unscaled units)
    private bool resetSizeCondition;

    public override void PreDraw()
    {
        Theme.Dark = plugin.Configuration.DarkMode;

        // Move the window *before* it's drawn, straight to where the mouse is. Moving it halfway through
        // drawing (or by accumulating mouse deltas) is what made it twitch.
        if (dragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            dragging = false;
        if (dragging)
        {
            Position = ImGui.GetIO().MousePos - dragOffset;
            PositionCondition = ImGuiCond.Always;
        }
        else
        {
            Position = null;
        }

        // Dalamud applies Size every frame, so "Always" may only be used for single frames (or while minimized).
        if (resetSizeCondition)
        {
            SizeCondition = ImGuiCond.FirstUseEver;
            resetSizeCondition = false;
        }
        if (sizeOnce is { } once)
        {
            Size = once;
            SizeCondition = ImGuiCond.Always;
            sizeOnce = null;
            resetSizeCondition = !minimized;
        }

        Theme.Push();

        if (minimized)
        {
            // Compact, pill-shaped bar.
            var padding = new Vector2(S(10), S(6));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, padding);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, (ImGui.GetFrameHeight() + padding.Y * 2) / 2f);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(1, 1));
            pushedMiniStyle = true;
        }
    }

    public override void PostDraw()
    {
        if (pushedMiniStyle)
        {
            ImGui.PopStyleVar(3);
            pushedMiniStyle = false;
        }
        Theme.Pop();
    }

    private bool pushedMiniStyle;
    private Vector2 miniSize = new(200, 30);   // pixels, measured each frame while minimized

    private void SetMinimized(bool value)
    {
        if (minimized == value)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        if (value)
        {
            restoreSize = ImGui.GetWindowSize();
            minimized = true;
            Flags |= ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(1, 1), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
            sizeOnce = miniSize / scale;
        }
        else
        {
            minimized = false;
            Flags &= ~(ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
            SizeConstraints = NormalConstraints;
            sizeOnce = restoreSize / scale;
        }
    }

    public override void Draw()
    {
        if (minimized)
        {
            DrawMiniBar();
            // Window = exactly the bar's contents.
            Size = miniSize / ImGuiHelpers.GlobalScale;
            SizeCondition = ImGuiCond.Always;
            return;
        }

        DrawHeader();
        Theme.CenteredText("Patch notes for your Dalamud plugins", Theme.TextSoft);
        Theme.AccentBar();
        ImGuiHelpers.ScaledDummy(2f);

        DrawUpdateBanner();
        DrawToolbar();
        if (showSettings)
            DrawSettings();

        ImGuiHelpers.ScaledDummy(2f);

        // Leave room at the bottom for the Ko-fi / GitHub footer.
        footerReserve = ImGui.GetFrameHeight() + S(4) + ImGui.GetStyle().ItemSpacing.Y * 2 + S(2);

        using (var bar = ImRaii.TabBar("##tabs", ImGuiTabBarFlags.FittingPolicyResizeDown))
        {
            if (bar.Success)
            {
                var unseen = plugin.Configuration.History.Count(e => !e.Seen);
                using (var tab = ImRaii.TabItem(unseen > 0 ? $"Recent ({unseen})###recent" : "Recent###recent"))
                {
                    if (tab.Success)
                        DrawRecentTab();
                }

                var available = plugin.VisibleUpdates.Count;
                using (var tab = ImRaii.TabItem(available > 0 ? $"Available ({available})###available" : "Available###available"))
                {
                    if (tab.Success)
                        DrawAvailableTab();
                }

                using (var tab = ImRaii.TabItem("All plugins###all"))
                {
                    if (tab.Success)
                        DrawAllTab();
                }
            }
        }

        DrawFooter();
    }

    private float footerReserve;
    private const string GitHubUrl = "https://github.com/NeonGhostCinnamon/WillOPatchwisp";

    /// <summary>Ko-fi and GitHub buttons, centred at the bottom of the window.</summary>
    private void DrawFooter()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var w = S(110);
        var total = w * 2 + spacing;
        ImGui.SetCursorPosY(Math.Max(ImGui.GetCursorPosY(), ImGui.GetWindowHeight() - ImGui.GetStyle().WindowPadding.Y - (ImGui.GetFrameHeight() + S(4))));
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - total) / 2f));
        if (Theme.LinkButton("##kofi", FontAwesomeIcon.Coffee, "Ko-fi", rainbow: true, width: w))
            Util.OpenLink(Theme.KofiUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support me on Ko-fi");
        ImGui.SameLine();
        if (Theme.LinkButton("##github", FontAwesomeIcon.CodeBranch, "GitHub", width: w))
            Util.OpenLink(GitHubUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(GitHubUrl);
    }

    // ---------------------------------------------------------------- light/dark toggle

    /// <summary>Title with the sun/moon theme button vertically centred on its right.</summary>
    /// <summary>
    /// The minimized window: a small pill-shaped bar with the name, warning badges for new things,
    /// and expand/close buttons. Drag it anywhere; double-click (or click a badge) to expand.
    /// </summary>
    private void DrawMiniBar()
    {
        var style = ImGui.GetStyle();
        var d = ImGui.GetFrameHeight();
        var start = ImGui.GetCursorScreenPos();
        var winPos = ImGui.GetWindowPos();

        // Name, with the pink swinging "o"
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.Text, "Will-");
        ImGui.SameLine(0, 0);
        Theme.DanglingText("o", Theme.Pink);
        ImGui.SameLine(0, 0);
        ImGui.TextColored(Theme.Text, "-Patchwisp");
        var nameEnd = ImGui.GetItemRectMax().X;

        // Warnings
        var available = plugin.VisibleUpdates.Count;
        var unseen = plugin.Configuration.History.Count(e => !e.Seen);
        var pillY = start.Y + (d - (ImGui.GetTextLineHeight() + S(2))) / 2f;

        if (available > 0)
        {
            ImGui.SameLine(0, S(8));
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, pillY));
            Theme.Pill($"{available} update{(available == 1 ? "" : "s")}", Theme.Amber);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"{available} plugin update{(available == 1 ? " is" : "s are")} available. Click to open.");
            }
            if (ImGui.IsItemClicked())
                SetMinimized(false);
        }

        if (unseen > 0)
        {
            ImGui.SameLine(0, S(6));
            ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, pillY));
            Theme.Pill($"{unseen} new", Theme.Green);
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip($"{unseen} plugin{(unseen == 1 ? " was" : "s were")} updated or installed. Click to see the changelogs.");
            }
            if (ImGui.IsItemClicked())
                SetMinimized(false);
        }

        // Expand + close (no theme switch here)
        ImGui.SameLine(0, S(10));
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, start.Y));
        if (Theme.IconCircle("##expand", FontAwesomeIcon.ChevronDown, Theme.Text, d))
            SetMinimized(false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Expand");

        ImGui.SameLine(0, S(4));
        if (Theme.IconCircle("##close", FontAwesomeIcon.Times, Theme.Text, d))
            IsOpen = false;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Close");

        var end = ImGui.GetItemRectMax();
        miniSize = new Vector2(end.X - winPos.X, end.Y - winPos.Y) + style.WindowPadding;

        // Drag the bar from anywhere that isn't a button/badge; double-click to expand.
        var overBar = ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered();
        if (overBar)
        {
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                SetMinimized(false);
                dragging = false;
            }
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                dragging = true;
                dragOffset = ImGui.GetIO().MousePos - winPos;
            }
        }
    }

    private void DrawHeader()
    {
        var config = plugin.Configuration;
        var d = S(28);
        var gap = S(6);
        var buttonsW = d * 3 + gap * 2;

        var top = ImGui.GetCursorPos();
        var topScreen = ImGui.GetCursorScreenPos();
        Theme.Heading(plugin.TitleFont, buttonsW + S(8), ("Will-", false), ("o", true), ("-Patchwisp", false));
        var headingH = ImGui.GetItemRectSize().Y;
        var after = ImGui.GetCursorPos();

        // Round buttons, top-right: minimize, theme switch, close.
        var y = top.Y + Math.Max(0, (headingH - d) / 2f);
        var right = ImGui.GetWindowContentRegionMax().X;

        ImGui.SetCursorPos(new Vector2(right - buttonsW, y));
        if (Theme.IconCircle("##min", minimized ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.Minus, Theme.Text, d))
            SetMinimized(!minimized);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(minimized ? "Expand" : "Minimize");

        ImGui.SetCursorPos(new Vector2(right - d * 2 - gap, y));
        var icon = config.DarkMode ? FontAwesomeIcon.Moon : FontAwesomeIcon.Sun;
        if (Theme.IconCircle("##mode", icon, config.DarkMode ? Theme.Moon : Theme.Amber, d))
        {
            config.DarkMode = !config.DarkMode;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(config.DarkMode ? "Switch to light mode" : "Switch to dark mode");

        ImGui.SetCursorPos(new Vector2(right - d, y));
        if (Theme.IconCircle("##close", FontAwesomeIcon.Times, Theme.Text, d))
            IsOpen = false;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Close");

        // The header (everything left of the buttons) works like a title bar:
        // drag to move, double-click to minimize/expand.
        var winPos = ImGui.GetWindowPos();
        var headerMin = new Vector2(winPos.X, winPos.Y);
        var headerMax = new Vector2(winPos.X + right - buttonsW - gap, topScreen.Y + Math.Max(headingH, d));
        var overHeader = ImGui.IsWindowHovered() && !ImGui.IsAnyItemHovered() && ImGui.IsMouseHoveringRect(headerMin, headerMax);
        if (overHeader)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                SetMinimized(!minimized);
                dragging = false;
            }
            else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                dragging = true;
                dragOffset = ImGui.GetIO().MousePos - winPos;
            }
        }

        ImGui.SetCursorPos(new Vector2(after.X, Math.Max(after.Y, top.Y + d)));
        ImGui.Dummy(Vector2.Zero);
    }

    // ---------------------------------------------------------------- update banner

    /// <summary>
    /// Dalamud doesn't allow plugins to install updates for other plugins, so this opens
    /// Dalamud's own plugin installer on its "updateable plugins" page (which has "Update all").
    /// </summary>
    private static void OpenUpdater(string? search = null)
        => Plugin.PluginInterface.OpenPluginInstallerTo(PluginInstallerOpenKind.UpdateablePlugins, search);

    private void DrawUpdateBanner()
    {
        var count = plugin.VisibleUpdates.Count;
        var updater = plugin.Updater;
        if (count == 0 && !updater.IsBusy)
            return;

        Theme.Card("banner", () =>
        {
            var style = ImGui.GetStyle();
            var label = updater.IsBusy ? "Updating..." : "Update all";
            var btnW = ImGui.CalcTextSize(label).X + style.FramePadding.X * 2;
            var left = Theme.CardInnerLeft;
            var right = Theme.CardInnerRight;
            var text = updater.IsBusy
                ? $"Updating plugin {Math.Min(updater.BatchDone + 1, updater.BatchTotal)} of {updater.BatchTotal}..."
                : $"{count} plugin update{(count == 1 ? " is" : "s are")} available";
            var fits = ImGui.CalcTextSize(text).X + style.ItemSpacing.X + btnW <= right - left;

            var rowTop = ImGui.GetCursorScreenPos();
            var wrapAt = ImGui.GetCursorPosX() + (fits ? right - left - btnW - style.ItemSpacing.X : right - left);

            ImGui.AlignTextToFramePadding();
            using (ImRaii.TextWrapPos(wrapAt))
                ImGui.TextColored(Theme.Text, text);

            // Button sits inside the card's padding: on the right of the text, or under it when narrow.
            if (fits)
            {
                // Vertically centred next to the text, even if the text wrapped.
                var textBottom = ImGui.GetItemRectMax().Y;
                var btnY = rowTop.Y + Math.Max(0, (textBottom - rowTop.Y - ImGui.GetFrameHeight()) / 2f);
                ImGui.SetCursorScreenPos(new Vector2(right - btnW, btnY));
            }
            using (ImRaii.Disabled(updater.IsBusy))
            {
                if (Theme.Button(label))
                    UpdateAll();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(updater.IsAvailable
                    ? "Installs all available updates, one after another."
                    : "Opens Dalamud's plugin installer on the updates page.");
        });
    }

    /// <summary>Updates everything directly, or opens the installer if direct updating isn't available.</summary>
    private void UpdateAll()
    {
        if (plugin.Updater.IsAvailable)
            plugin.Updater.UpdateMany(plugin.VisibleUpdates.Select(u => u.InternalName).ToList());
        else
            OpenUpdater();
    }

    private void UpdateOne(string internalName, string name)
    {
        if (plugin.Updater.IsAvailable)
            plugin.Updater.Update(internalName);
        else
            OpenUpdater(name);
    }

    // ---------------------------------------------------------------- toolbar / settings

    private void DrawToolbar()
    {
        var style = ImGui.GetStyle();
        var checkLabel = plugin.Repos.IsRunning ? "Checking..." : "Check for updates";
        var settingsLabel = showSettings ? "Hide settings" : "Settings";
        var checkW = ImGui.CalcTextSize(checkLabel).X + style.FramePadding.X * 2;
        var settingsW = ImGui.CalcTextSize(settingsLabel).X + style.FramePadding.X * 2;

        var avail = ImGui.GetContentRegionAvail().X;
        var searchW = avail - checkW - settingsW - style.ItemSpacing.X * 2;
        var oneRow = searchW >= S(150);

        ImGui.SetNextItemWidth(oneRow ? searchW : avail);
        ImGui.InputTextWithHint("##search", "Search plugins or changelogs...", ref search, 100);

        if (oneRow)
            ImGui.SameLine();

        using (ImRaii.Disabled(plugin.Repos.IsRunning))
        {
            if (Theme.Button(checkLabel))
            {
                plugin.Tracker.CheckNow();
                plugin.Repos.StartCheck();
                installedCache = null;
            }
        }

        ImGui.SameLine();
        if (Theme.Button(settingsLabel))
            showSettings = !showSettings;
    }

    private void DrawSettings()
    {
        var config = plugin.Configuration;
        var changed = false;

        Theme.Card("settings", () =>
        {
            changed |= Checkbox("autoopen", "Open Will-o-Patchwisp when a plugin gets updated.", config.AutoOpenOnUpdate, v => config.AutoOpenOnUpdate = v);
            changed |= Checkbox("chat", "Print summary in chat.", config.PrintToChat, v => config.PrintToChat = v);
            changed |= Checkbox("installs", "List newly installed Plugins", config.TrackNewInstalls, v => config.TrackNewInstalls = v);
            changed |= Checkbox("repos", "Check plugin repos for newer versions on startup.", config.CheckReposOnStartup, v => config.CheckReposOnStartup = v);

            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(Theme.TextSoft, "Theme");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(160));
            using (var combo = ImRaii.Combo("##theme", config.DarkMode ? "Dark mode" : "Light mode"))
            {
                if (combo.Success)
                {
                    if (ImGui.Selectable("Light mode", !config.DarkMode) && config.DarkMode)
                    {
                        config.DarkMode = false;
                        changed = true;
                    }
                    if (ImGui.Selectable("Dark mode", config.DarkMode) && !config.DarkMode)
                    {
                        config.DarkMode = true;
                        changed = true;
                    }
                }
            }

            ImGui.TextColored(Theme.TextSoft, "History size");
            var max = config.MaxHistory;
            ImGui.SetNextItemWidth(Math.Min(Theme.CardInnerWidth, S(260)));
            if (ImGui.SliderInt("##history", ref max, 20, 1000))
            {
                config.MaxHistory = max;
                changed = true;
            }

            if (Theme.Button("Clear history"))
            {
                config.History.Clear();
                changed = true;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Removes all recorded updates. Installed versions are still remembered.");
        });

        // Ignored plugins get a card of their own.
        Theme.Card("ignored", DrawIgnoreList);

        if (changed)
            config.Save();
    }

    private string ignoreFilter = string.Empty;

    /// <summary>Settings section to add plugins to, or remove them from, the ignore list.</summary>
    private void DrawIgnoreList()
    {
        var config = plugin.Configuration;
        ImGui.TextColored(Theme.Text, "Ignored plugins");
        Theme.Wrapped("No update alerts for these, and they're left out of \"Available\" and \"Update all\".", Theme.Muted);

        var names = Plugin.PluginInterface.InstalledPlugins
            .Where(p => !p.IsDev)
            .GroupBy(p => p.InternalName)
            .ToDictionary(g => g.Key, g => g.First().Name);

        if (config.IgnoredPlugins.Count == 0)
        {
            Theme.Wrapped("Nothing ignored.", Theme.Muted);
        }
        else
        {
            foreach (var internalName in config.IgnoredPlugins.OrderBy(n => names.GetValueOrDefault(n, n), StringComparer.OrdinalIgnoreCase).ToList())
            {
                ImGui.PushID(internalName);
                if (Theme.SmallButton("Remove"))
                    plugin.SetIgnored(internalName, false);
                ImGui.SameLine();
                ImGui.TextColored(Theme.TextSoft, names.GetValueOrDefault(internalName, internalName));
                ImGui.PopID();
            }
        }

        // Add: dropdown of installed plugins that aren't ignored yet (with a search box).
        ImGui.SetNextItemWidth(Math.Min(Theme.CardInnerWidth, S(260)));
        using var combo = ImRaii.Combo("##addignore", "Add a plugin to ignore...");
        if (!combo.Success)
            return;

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##ignorefilter", "Search...", ref ignoreFilter, 100);
        foreach (var (internalName, name) in names
                     .Where(kv => !config.IgnoredPlugins.Contains(kv.Key))
                     .Where(kv => ignoreFilter.Length == 0 || kv.Value.Contains(ignoreFilter, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase))
        {
            if (ImGui.Selectable($"{name}##{internalName}"))
            {
                plugin.SetIgnored(internalName, true);
                ignoreFilter = string.Empty;
            }
        }
    }

    /// <summary>Checkbox whose label wraps onto extra lines when the window is narrow. Clicking the text toggles it too.</summary>
    private static bool Checkbox(string id, string label, bool value, Action<bool> set)
    {
        var toggled = ImGui.Checkbox($"##{id}", ref value);
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.BeginGroup();
        Theme.BrandText(label, Theme.Text, Theme.CardInnerRight);
        ImGui.EndGroup();
        if (ImGui.IsItemClicked())
        {
            value = !value;
            toggled = true;
        }

        if (toggled)
            set(value);
        return toggled;
    }

    private bool Matches(string name, string? changelog)
        => search.Length == 0
           || name.Contains(search, StringComparison.OrdinalIgnoreCase)
           || (changelog?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    // ---------------------------------------------------------------- tab: recent updates

    private void DrawRecentTab()
    {
        var config = plugin.Configuration;

        using var child = ImRaii.Child("##recent", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;

        var entries = config.History.Where(e => Matches(e.Name, e.Changelog)).ToList();
        if (entries.Count == 0)
        {
            Theme.Wrapped(config.History.Count == 0
                ? "No updates recorded yet. Updates that happen from now on will show up here.\n\n" +
                  "Meanwhile, \"All plugins\" shows the current changelog of every plugin you have, " +
                  "and \"Available\" shows updates you haven't received yet."
                : "Nothing matches your search.", Theme.Muted);
            return;
        }

        foreach (var batch in entries.GroupBy(e => e.DetectedAt))
        {
            ImGuiHelpers.ScaledDummy(4f);
            Theme.CenteredText(
                $"{batch.Key:dddd d MMMM yyyy, HH:mm}  -  {batch.Count()} plugin{(batch.Count() == 1 ? "" : "s")}",
                Theme.Pink);

            foreach (var e in batch)
            {
                var versions = e.OldVersion == null ? $"installed {e.NewVersion}" : $"{e.OldVersion} -> {e.NewVersion}";
                DrawCard(
                    $"r{e.InternalName}{e.DetectedAt.Ticks}", e.Name, versions,
                    defaultOpen: !e.Seen,
                    tag: e.Seen ? null : ("NEW", Theme.Green),
                    subtitle: null,
                    e.Changelog, e.RepoUrl, version: e.NewVersion, internalName: e.InternalName);
            }
        }
    }

    // ---------------------------------------------------------------- tab: available updates

    private void DrawAvailableTab()
    {
        var repos = plugin.Repos;

        if (repos.IsRunning)
            Theme.Wrapped("Checking your plugin repositories...", Theme.Muted);
        else if (repos.LastChecked is { } at)
            Theme.Wrapped($"Last checked {at:HH:mm}. Dalamud installs these when you restart the game or press \"Update plugins\" in /xlplugins.", Theme.Muted);
        else
            Theme.Wrapped("Not checked yet. Press \"Check for updates\".", Theme.Muted);

        if (repos.Errors.Count > 0)
        {
            Theme.Wrapped($"Couldn't reach {repos.Errors.Count} repositor{(repos.Errors.Count == 1 ? "y" : "ies")}: {string.Join(", ", repos.Errors)}", Theme.Amber);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Usually a third-party repository that moved or is temporarily down. Plugins from it can't be checked.");
        }

        var visible = plugin.VisibleUpdates;
        if (visible.Count > 0)
        {
            ImGuiHelpers.ScaledDummy(2f);
            var busy = plugin.Updater.IsBusy;
            using (ImRaii.Disabled(busy))
            {
                if (Theme.Button(busy ? "Updating..." : $"Update all plugins ({visible.Count})", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                    UpdateAll();
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !plugin.Updater.IsAvailable)
                ImGui.SetTooltip("Opens Dalamud's plugin installer on the updates page.");
            ImGuiHelpers.ScaledDummy(2f);
        }

        using var child = ImRaii.Child("##available", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;

        var list = visible.Where(u => Matches(u.Name, u.Changelog)).ToList();
        var ignoredCount = repos.Updates.Count - visible.Count;
        if (ignoredCount > 0)
            Theme.Wrapped($"{ignoredCount} update{(ignoredCount == 1 ? " is" : "s are")} hidden because the plugin is on your ignore list (see Settings).", Theme.Muted);
        if (list.Count == 0)
        {
            if (repos.LastChecked != null && !repos.IsRunning)
                Theme.Wrapped(search.Length == 0 ? "All your plugins are up to date." : "Nothing matches your search.",
                              search.Length == 0 ? Theme.Green : Theme.Muted);
            return;
        }

        foreach (var u in list)
        {
            DrawCard(
                $"a{u.InternalName}", u.Name, $"{u.InstalledVersion} -> {u.LatestVersion}",
                defaultOpen: true,
                tag: u.IsTesting ? ("TESTING", Theme.Amber) : ("UPDATE", Theme.Pink),
                subtitle: u.ReleasedAt is { } r ? $"Released {Ago(r)}" : null,
                u.Changelog, u.RepoUrl, update: (u.InternalName, u.Name), version: u.LatestVersion, internalName: u.InternalName);
        }
    }

    // ---------------------------------------------------------------- tab: all plugins

    private void DrawAllTab()
    {
        if (installedCache == null || DateTime.UtcNow - installedCacheAt > TimeSpan.FromSeconds(10))
            RefreshInstalled();

        Theme.Wrapped("The changelog of the version you have installed right now, newest release first.", Theme.Muted);

        using var child = ImRaii.Child("##all", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;

        foreach (var p in installedCache!.Where(p => Matches(p.Name, p.Changelog)))
        {
            DrawCard(
                $"i{p.InternalName}", p.Name, p.Version,
                defaultOpen: false,
                tag: plugin.IsIgnored(p.InternalName) ? ("IGNORED", new Vector4(0.55f, 0.55f, 0.62f, 1f)) : p.IsTesting ? ("TESTING", Theme.Amber) : null,
                subtitle: p.ReleasedAt is { } r ? $"Released {Ago(r)}" : null,
                p.Changelog, p.RepoUrl, version: p.Version, internalName: p.InternalName);
        }
    }

    private void RefreshInstalled()
    {
        installedCacheAt = DateTime.UtcNow;
        var list = new List<InstalledInfo>();
        foreach (var p in Plugin.PluginInterface.InstalledPlugins)
        {
            if (p.IsDev)
                continue;

            string? changelog = null, repo = null;
            DateTime? released = null;
            try
            {
                changelog = p.Manifest.Changelog;
                repo = p.Manifest.RepoUrl;
                if (p.Manifest.LastUpdate > 0)
                    released = DateTimeOffset.FromUnixTimeSeconds(p.Manifest.LastUpdate).LocalDateTime;
            }
            catch
            {
                // Manifest not readable; show what we have.
            }

            list.Add(new InstalledInfo(
                p.InternalName, p.Name, p.Version.ToString(),
                string.IsNullOrWhiteSpace(changelog) ? null : changelog.Trim(),
                released, string.IsNullOrWhiteSpace(repo) ? null : repo, p.IsTesting));
        }

        installedCache = list
            .OrderByDescending(p => p.ReleasedAt ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ---------------------------------------------------------------- shared drawing

    private void DrawCard(
        string id, string name, string versions, bool defaultOpen, (string Text, Vector4 Color)? tag,
        string? subtitle, string? changelog, string? repoUrl, (string InternalName, string Name)? update = null, string? version = null, string? internalName = null)
    {
        var open = expanded.TryGetValue(id, out var o) ? o : defaultOpen;

        Theme.Card(id, () =>
        {
            var style = ImGui.GetStyle();
            var inner = Theme.CardInnerWidth;
            var leftLocal = ImGui.GetCursorPosX();
            var rowTop = ImGui.GetCursorScreenPos();

            var toggleText = open ? "Hide" : "Show";
            var pillsW = Theme.PillWidth(toggleText)
                         + (tag is { } tw ? Theme.PillWidth(tw.Text) + style.ItemSpacing.X : 0);

            // Space for the title, leaving room for the pills on the right.
            var titleW = Math.Max(S(60), inner - pillsW - style.ItemSpacing.X);
            var nameW = ImGui.CalcTextSize(name).X;
            var verW = ImGui.CalcTextSize(versions).X;
            var versionsOnSameLine = nameW + style.ItemSpacing.X + verW <= titleW;

            ImGui.BeginGroup();

            // Left column: name, version and "Released ..." line, all wrapping before the pills.
            using (ImRaii.TextWrapPos(leftLocal + titleW))
            {
                ImGui.TextColored(Theme.Text, name);
                if (versions.Length > 0)
                {
                    if (versionsOnSameLine)
                        ImGui.SameLine();
                    ImGui.TextColored(Theme.Pink, versions);
                }

                if (subtitle != null)
                    ImGui.TextColored(Theme.Muted, subtitle);
            }

            var blockBottom = ImGui.GetItemRectMax().Y;
            var afterBlock = ImGui.GetCursorScreenPos();

            // Pills on the right, vertically centred on that block (= the middle of a closed card).
            var pillH = ImGui.GetTextLineHeight() + S(2);
            var cardLeft = Theme.CardInnerLeft;
            var pillY = rowTop.Y + Math.Max(0, (blockBottom - rowTop.Y - pillH) / 2f);
            ImGui.SetCursorScreenPos(new Vector2(cardLeft + inner - pillsW, pillY));
            if (tag is { } t)
            {
                Theme.Pill(t.Text, t.Color with { W = 0.85f });
                ImGui.SameLine();
            }
            Theme.Pill(toggleText, Theme.Pink);

            // Continue below the block. For a closed card, stop exactly at the text's bottom edge:
            // moving the cursor further (line spacing) would make ImGui count it as content and the card
            // would get extra space at the bottom, so the pills would no longer look centred.
            var pillBottom = ImGui.GetItemRectMax().Y;
            var contentBottom = Math.Max(blockBottom, pillBottom);
            ImGui.SetCursorScreenPos(new Vector2(cardLeft, open ? Math.Max(afterBlock.Y, pillBottom + style.ItemSpacing.Y) : contentBottom));
            ImGui.EndGroup();

            if (ImGui.IsItemHovered())
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (ImGui.IsItemClicked())
            {
                open = !open;
                expanded[id] = open;
            }

            if (!open)
                return;

            ImGuiHelpers.ScaledDummy(2f);
            if (changelog != null)
                Theme.Wrapped(changelog, Theme.TextSoft);
            else
                DrawGitHubFallback(repoUrl, version);

            if (repoUrl != null || update != null)
                ImGuiHelpers.ScaledDummy(2f);

            if (update is { } up)
            {
                plugin.Updater.States.TryGetValue(up.InternalName, out var state);
                switch (state?.Kind)
                {
                    case UpdateStateKind.Updating:
                        using (ImRaii.Disabled())
                            Theme.SmallButton("Updating...");
                        break;
                    case UpdateStateKind.Updated:
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(Theme.Green, "Updated!");
                        break;
                    default:
                        if (Theme.SmallButton(state?.Kind is UpdateStateKind.Failed or UpdateStateKind.NotFound ? "Try again" : "Update"))
                            UpdateOne(up.InternalName, up.Name);
                        if (ImGui.IsItemHovered() && !plugin.Updater.IsAvailable)
                            ImGui.SetTooltip("Opens this plugin in Dalamud's installer so you can update it.");
                        break;
                }

                if (state?.Kind is UpdateStateKind.Failed or UpdateStateKind.NotFound)
                {
                    ImGui.SameLine();
                    if (Theme.SmallButton("Open installer"))
                        OpenUpdater(up.Name);
                }

                if (repoUrl != null)
                    ImGui.SameLine();

                if (state?.Kind is UpdateStateKind.Failed or UpdateStateKind.NotFound && state.Message != null)
                {
                    if (repoUrl != null)
                        ImGui.NewLine();
                    Theme.Wrapped(state.Message, Theme.Amber);
                }
            }

            if (repoUrl != null)
            {
                if (Theme.SmallButton("Open repository"))
                    Util.OpenLink(repoUrl);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(repoUrl);
            }

            if (internalName != null)
            {
                if (repoUrl != null || update != null)
                    ImGui.SameLine();
                else
                    ImGuiHelpers.ScaledDummy(2f);

                var ignored = plugin.IsIgnored(internalName);
                if (Theme.SmallButton(ignored ? "Stop ignoring" : "Ignore"))
                    plugin.SetIgnored(internalName, !ignored);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(ignored
                        ? "Show update alerts for this plugin again."
                        : "Hide this plugin's updates: no alerts, not in \"Available\" or \"Update all\". You can undo this in Settings.");
            }
        });
    }

    /// <summary>No changelog in the manifest: try the plugin's GitHub releases / commits instead.</summary>
    private void DrawGitHubFallback(string? repoUrl, string? version)
    {
        if (repoUrl == null || version == null || GitHubChangelogs.ParseRepo(repoUrl) == null)
        {
            Theme.Wrapped("No changelog was included with this version.", Theme.Muted);
            return;
        }

        var result = plugin.GitHub.Get(repoUrl, version);
        switch (result.Status)
        {
            case GitHubLookupStatus.Loading:
                Theme.Wrapped("No changelog was included. Looking on GitHub...", Theme.Muted);
                break;

            case GitHubLookupStatus.Found:
                Theme.Pill("GitHub", Theme.Pink);
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                Theme.Wrapped(result.Source ?? "From GitHub", Theme.Muted);
                Theme.Wrapped(result.Text ?? string.Empty, Theme.TextSoft);
                if (result.Url != null)
                {
                    if (Theme.SmallButton("Open on GitHub"))
                        Util.OpenLink(result.Url);
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(result.Url);
                }
                break;

            case GitHubLookupStatus.Failed:
                Theme.Wrapped($"No changelog was included, and GitHub couldn't be checked. {result.Text}", Theme.Amber);
                if (Theme.SmallButton("Try GitHub again"))
                    plugin.GitHub.Retry(repoUrl, version);
                break;

            default:
                Theme.Wrapped("No changelog was included, and there are no release notes or commits on GitHub.", Theme.Muted);
                break;
        }
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalDays >= 365) return when.ToString("d MMM yyyy");
        if (span.TotalDays >= 2) return $"{(int)span.TotalDays} days ago";
        if (span.TotalHours >= 2) return $"{(int)span.TotalHours} hours ago";
        if (span.TotalMinutes >= 2) return $"{(int)span.TotalMinutes} minutes ago";
        return "just now";
    }
}
