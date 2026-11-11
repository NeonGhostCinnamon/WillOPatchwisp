using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
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
        Theme.Mode = plugin.Configuration.ThemeChoice;

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
                var recentFlags = selectRecentTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                selectRecentTab = false;
                using (var tab = ImRaii.TabItem(unseen > 0 ? $"Recent ({unseen})###recent" : "Recent###recent", recentFlags))
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
        Theme.DanglingText("o", Theme.BrandPink);
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

        // Round buttons, top-right: theme, minimize, close.
        var y = top.Y + Math.Max(0, (headingH - d) / 2f);
        var right = ImGui.GetWindowContentRegionMax().X;

        ImGui.SetCursorPos(new Vector2(right - d * 2 - gap, y));
        if (Theme.IconCircle("##min", minimized ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.Minus, Theme.Text, d))
            SetMinimized(!minimized);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(minimized ? "Expand" : "Minimize");

        ImGui.SetCursorPos(new Vector2(right - buttonsW, y));
        var mode = config.ThemeChoice;
        if (Theme.ThemeButton("##theme", ref mode, d))
        {
            config.ThemeChoice = mode;
            config.Save();
        }

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

    // "Check for updates" pressed: show what it found on the button for a few seconds once it's done.
    private bool manualCheck;
    private DateTime checkResultUntil;
    private int checkResultCount;

    private void DrawToolbar()
    {
        var style = ImGui.GetStyle();

        if (manualCheck && !plugin.Repos.IsRunning)
        {
            manualCheck = false;
            checkResultCount = plugin.VisibleUpdates.Count;
            checkResultUntil = DateTime.UtcNow.AddSeconds(4);
        }

        var (checkLabel, look, icon) = plugin.Repos.IsRunning
            ? ("Checking...", Theme.CheckLook.Busy, (FontAwesomeIcon?)null)
            : DateTime.UtcNow >= checkResultUntil
                ? ("Check for updates", Theme.CheckLook.Normal, null)
                : checkResultCount == 0
                    ? ("No updates found", Theme.CheckLook.Nothing, FontAwesomeIcon.BoxOpen)
                    : ($"{checkResultCount} update{(checkResultCount == 1 ? "" : "s")} found", Theme.CheckLook.Found, null);
        var settingsLabel = showSettings ? "Hide settings" : "Settings";
        var checkW = Theme.CheckButtonWidth(checkLabel, look == Theme.CheckLook.Busy || icon != null);
        var settingsW = ImGui.CalcTextSize(settingsLabel).X + style.FramePadding.X * 2;

        var avail = ImGui.GetContentRegionAvail().X;
        var searchW = avail - checkW - settingsW - style.ItemSpacing.X * 2;
        var oneRow = searchW >= S(150);

        ImGui.SetNextItemWidth(oneRow ? searchW : avail);
        ImGui.InputTextWithHint("##search", "Search plugins or changelogs...", ref search, 100);

        if (oneRow)
            ImGui.SameLine();

        if (Theme.CheckButton("##check", checkLabel, look, icon))
        {
            plugin.Tracker.CheckNow();
            plugin.Repos.StartCheck();
            installedCache = null;
            manualCheck = true;
            checkResultUntil = default;
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
            changed |= DrawChatChannel(config);
            changed |= Checkbox("installs", "List newly installed Plugins", config.TrackNewInstalls, v => config.TrackNewInstalls = v);
            changed |= Checkbox("repos", "Check plugin repos for newer versions on startup.", config.CheckReposOnStartup, v => config.CheckReposOnStartup = v);

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

    private enum Period
    {
        Today,
        Yesterday,
        Last7Days,
        Last30Days,
        AllTime,
        OneDay,
    }

    private Period period = Period.Last7Days;
    private DateTime pickedDay;

    // Day cards opened/minimized by hand. Days not in here: only the newest day is open.
    private readonly Dictionary<DateTime, bool> dayOpen = new();

    // Set by a "(changelog)" link in chat: select the Recent tab and jump to this entry.
    private bool selectRecentTab;
    private string? jumpTo;
    private string? flashKey;
    private DateTime flashUntil;

    private static string EntryKey(UpdateEntry e) => $"{e.InternalName}|{e.DetectedAt.Ticks}";

    /// <summary>Open the window on the Recent tab with this entry's changelog shown (used by chat links).</summary>
    public void ShowEntry(string internalName, long detectedTicks)
    {
        var e = plugin.Configuration.History.FirstOrDefault(x => x.InternalName == internalName && x.DetectedAt.Ticks == detectedTicks);
        OpenOnRecent();
        if (e == null)
            return;

        period = Period.AllTime;
        search = string.Empty;
        dayOpen[e.DetectedAt.Date] = true;
        var key = EntryKey(e);
        expanded["r" + key] = true;
        jumpTo = key;
        flashKey = key;
        flashUntil = DateTime.Now.AddSeconds(1.6);
    }

    /// <summary>Open the window on the Recent tab (used by the "and N more" chat link).</summary>
    public void OpenOnRecent()
    {
        IsOpen = true;
        if (minimized)
            SetMinimized(false);
        selectRecentTab = true;
    }

    private static string PeriodName(Period p, DateTime day) => p switch
    {
        Period.Today => "Today",
        Period.Yesterday => "Yesterday",
        Period.Last7Days => "Last 7 days",
        Period.Last30Days => "Last 30 days",
        Period.AllTime => "All time",
        _ => day.ToString("ddd d MMM"),
    };

    private bool InPeriod(DateTime when)
    {
        var today = DateTime.Today;
        return period switch
        {
            Period.Today => when.Date == today,
            Period.Yesterday => when.Date == today.AddDays(-1),
            Period.Last7Days => when.Date >= today.AddDays(-6),
            Period.Last30Days => when.Date >= today.AddDays(-29),
            Period.OneDay => when.Date == pickedDay,
            _ => true,
        };
    }

    private void DrawRecentTab()
    {
        var config = plugin.Configuration;

        // "Show [period]" filter
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextSoft, "Show");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(S(150));
        using (var combo = ImRaii.Combo("##period", PeriodName(period, pickedDay)))
        {
            if (combo.Success)
            {
                foreach (var p in new[] { Period.Today, Period.Yesterday, Period.Last7Days, Period.Last30Days, Period.AllTime })
                {
                    if (ImGui.Selectable(PeriodName(p, default), period == p))
                        period = p;
                }

                var days = config.History.GroupBy(e => e.DetectedAt.Date).OrderByDescending(g => g.Key).ToList();
                if (days.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.TextColored(Theme.Muted, "Pick a day");
                    foreach (var g in days)
                    {
                        if (ImGui.Selectable($"{g.Key:ddd d MMM}  ({g.Count()})##day{g.Key.Ticks}", period == Period.OneDay && pickedDay == g.Key))
                        {
                            period = Period.OneDay;
                            pickedDay = g.Key;
                        }
                    }
                }
            }
        }

        var entries = config.History.Where(e => InPeriod(e.DetectedAt) && Matches(e.Name, e.Changelog)).ToList();
        var countText = $"{entries.Count} update{(entries.Count == 1 ? "" : "s")}";
        ImGui.SameLine(ImGui.GetContentRegionMax().X - ImGui.CalcTextSize(countText).X);
        ImGui.TextColored(Theme.Muted, countText);

        using var child = ImRaii.Child("##recent", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;

        if (entries.Count == 0)
        {
            Theme.Wrapped(config.History.Count == 0
                ? "No updates recorded yet. Updates that happen from now on will show up here.\n\n" +
                  "Meanwhile, \"All plugins\" shows the current changelog of every plugin you have, " +
                  "and \"Available\" shows updates you haven't received yet."
                : search.Length > 0
                    ? "Nothing matches your search."
                    : "No updates in this period. Pick a longer one under \"Show\".", Theme.Muted);
            return;
        }

        var first = true;
        foreach (var day in entries.GroupBy(e => e.DetectedAt.Date).OrderByDescending(g => g.Key))
        {
            var open = dayOpen.TryGetValue(day.Key, out var o) ? o : first;
            first = false;
            DrawDayCard(day.Key, day.OrderByDescending(e => e.DetectedAt).ToList(), open);
        }
    }

    /// <summary>One card per day: a header you can click to minimize, then one row per updated plugin.</summary>
    private void DrawDayCard(DateTime day, List<UpdateEntry> rows, bool open)
    {
        Theme.Card($"day{day.Ticks}", () =>
        {
            var left = Theme.CardInnerLeft;
            var right = Theme.CardInnerRight;
            var lineH = ImGui.GetTextLineHeight();

            // Header: chevron, date, "N plugins" on the right. Click anywhere on it to open/minimize.
            var headTop = ImGui.GetCursorScreenPos();
            var countText = $"{rows.Count} plugin{(rows.Count == 1 ? "" : "s")}";
            if (ImGui.InvisibleButton("##head", new Vector2(right - left, lineH)))
                dayOpen[day] = open = !open;
            var hovered = ImGui.IsItemHovered();
            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var dl = ImGui.GetWindowDrawList();
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                var chev = (open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString();
                var ink = Theme.InkBounds(chev);
                dl.AddText(headTop + new Vector2((lineH - (ink.Z - ink.X)) / 2f - ink.X, (lineH - (ink.Y + ink.W)) / 2f), ImGui.GetColorU32(Theme.AccentText), chev);
            }
            dl.AddText(headTop + new Vector2(lineH + S(4), 0), ImGui.GetColorU32(open || hovered ? Theme.AccentText : Theme.TextSoft), $"{day:dddd d MMMM yyyy}");
            dl.AddText(new Vector2(right - ImGui.CalcTextSize(countText).X, headTop.Y), ImGui.GetColorU32(Theme.Muted), countText);

            if (!open)
                return;

            foreach (var e in rows)
                DrawRecentRow(e, left, right);
        });
    }

    private void DrawRecentRow(UpdateEntry e, float left, float right)
    {
        var style = ImGui.GetStyle();
        var key = EntryKey(e);
        var id = "r" + key;
        var open = expanded.TryGetValue(id, out var o) && o;
        var dl = ImGui.GetWindowDrawList();

        // thin divider above each row
        ImGuiHelpers.ScaledDummy(1f);
        var top = ImGui.GetCursorScreenPos().Y;
        dl.AddLine(new Vector2(left - S(12), top), new Vector2(right + S(12), top), ImGui.GetColorU32(Theme.Border with { W = Theme.Border.W * 0.5f }));
        ImGuiHelpers.ScaledDummy(2f);

        var rowTop = ImGui.GetCursorScreenPos();
        var pillH = ImGui.GetTextLineHeight() + S(2);

        // Lit up for a moment after a "(changelog)" link in chat jumped here.
        if (flashKey == key && DateTime.Now < flashUntil)
        {
            var a = (float)((flashUntil - DateTime.Now).TotalSeconds / 1.6);
            dl.AddRectFilled(new Vector2(left - S(12), rowTop.Y - S(3)), new Vector2(right + S(12), rowTop.Y + pillH + S(3)),
                ImGui.GetColorU32(Theme.BrandPink with { W = 0.3f * Math.Clamp(a * 1.5f, 0f, 1f) }));
        }

        // Pills on the right: NEW (until you've opened it) and Show/Hide.
        var toggleText = open ? "Hide" : "Show";
        var isNew = !e.Seen;
        var pillsW = Theme.PillWidth(toggleText) + (isNew ? Theme.PillWidth("NEW") + style.ItemSpacing.X : 0);

        // Left: time, name, versions (wrapping before the pills)
        ImGui.BeginGroup();
        ImGui.TextColored(Theme.Muted, e.DetectedAt.ToString("HH:mm"));
        ImGui.SameLine(0, S(10));
        var textRight = right - pillsW - style.ItemSpacing.X;
        ImGui.TextColored(Theme.Text, e.Name);
        DrawVersions(e.OldVersion, e.NewVersion, textRight);
        ImGui.EndGroup();
        var blockBottom = ImGui.GetItemRectMax().Y;
        var rowHovered = ImGui.IsItemHovered();

        var after = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(right - pillsW, rowTop.Y + Math.Max(0, (blockBottom - rowTop.Y - pillH) / 2f)));
        if (isNew)
        {
            Theme.Pill("NEW", Theme.Green with { W = 0.85f });
            ImGui.SameLine();
        }
        Theme.Pill(toggleText, Theme.Pink);
        var pillHovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(new Vector2(left, Math.Max(after.Y, ImGui.GetItemRectMax().Y + style.ItemSpacing.Y)));

        if (rowHovered || pillHovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if ((rowHovered || pillHovered) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            open = !open;
            expanded[id] = open;
            if (open && !e.Seen)
            {
                e.Seen = true;
                plugin.Configuration.Save();
            }
        }

        if (jumpTo == key)
        {
            ImGui.SetScrollHereY(0.25f);
            jumpTo = null;
            if (!e.Seen)
            {
                e.Seen = true;
                plugin.Configuration.Save();
            }
        }

        if (!open)
            return;

        // Changelog, indented under the name with a thin accent line on its left.
        var indent = S(44);
        var noteTop = ImGui.GetCursorScreenPos();
        ImGui.Indent(indent);
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (right - left - indent)))
        {
            if (e.Changelog != null)
                Theme.Wrapped(e.Changelog, Theme.TextSoft);
            else
                DrawGitHubFallback(e.RepoUrl, e.NewVersion);

            ImGuiHelpers.ScaledDummy(1f);
            if (e.RepoUrl != null)
            {
                if (Theme.SmallButton($"Open repository##{key}"))
                    Util.OpenLink(e.RepoUrl);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(e.RepoUrl);
                ImGui.SameLine();
            }

            var ignored = plugin.IsIgnored(e.InternalName);
            if (Theme.SmallButton($"{(ignored ? "Stop ignoring" : "Ignore")}##{key}"))
                plugin.SetIgnored(e.InternalName, !ignored);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(ignored
                    ? "Show update alerts for this plugin again."
                    : "Hide this plugin's updates: no alerts, not in \"Available\" or \"Update all\". You can undo this in Settings.");
        }
        ImGui.Unindent(indent);
        var noteBottom = ImGui.GetItemRectMax().Y;
        dl.AddLine(new Vector2(left + indent - S(10), noteTop.Y), new Vector2(left + indent - S(10), noteBottom),
            ImGui.GetColorU32(Theme.Pink), S(2));
        ImGuiHelpers.ScaledDummy(2f);
    }

    /// <summary>
    /// "1.0.0.0 -> 1.0.0.1" where only the numbers that changed are in the accent colour; the rest is grey.
    /// New installs show "installed 1.0.0.0". Wraps to the next line when it doesn't fit before <paramref name="rightEdge"/>.
    /// </summary>
    private void DrawVersions(string? oldVersion, string newVersion, float rightEdge)
    {
        var pieces = new List<(string Text, Vector4 Color)>();
        if (oldVersion == null)
        {
            pieces.Add(($"installed {newVersion}", Theme.Muted));
        }
        else
        {
            pieces.Add(($"{oldVersion} -> ", Theme.Muted));
            var a = oldVersion.Split('.');
            var b = newVersion.Split('.');
            for (var i = 0; i < b.Length; i++)
            {
                if (i > 0)
                    pieces.Add((".", Theme.Muted));
                var changed = i >= a.Length || a[i] != b[i];
                pieces.Add((b[i], changed ? Theme.AccentText : Theme.Muted));
            }
        }

        var total = pieces.Sum(p => ImGui.CalcTextSize(p.Text).X);
        var x = ImGui.GetItemRectMax().X + S(8);
        if (x + total <= rightEdge)
            ImGui.SameLine(0, S(8));
        else
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(44));   // under the name

        for (var i = 0; i < pieces.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 0);
            ImGui.TextColored(pieces[i].Color, pieces[i].Text);
        }
    }

    /// <summary>Settings: which chat channel the update lines go to.</summary>
    private static readonly (XivChatType Type, string Name)[] ChatChannels =
    {
        (XivChatType.None, "Dalamud default"),
        (XivChatType.Echo, "Echo"),
        (XivChatType.SystemMessage, "System Message"),
        (XivChatType.Notice, "Notice"),
        (XivChatType.Urgent, "Urgent"),
        (XivChatType.Debug, "Debug"),
    };

    private bool DrawChatChannel(Configuration config)
    {
        var changed = false;
        using var disabled = ImRaii.Disabled(!config.PrintToChat);
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetFrameHeight() + ImGui.GetStyle().ItemInnerSpacing.X);
        ImGui.AlignTextToFramePadding();
        ImGui.TextColored(Theme.TextSoft, "Chat channel");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(S(160));
        var current = ChatChannels.FirstOrDefault(c => c.Type == config.ChatChannel);
        using (var combo = ImRaii.Combo("##chatchannel", current.Name ?? config.ChatChannel.ToString()))
        {
            if (combo.Success)
            {
                foreach (var (type, name) in ChatChannels)
                {
                    if (ImGui.Selectable(name, type == config.ChatChannel) && type != config.ChatChannel)
                    {
                        config.ChatChannel = type;
                        changed = true;
                    }
                }
            }
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Where the \"Updated: ...\" lines show up. Pick a channel your chat tabs display.");
        return changed;
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
                    ImGui.TextColored(Theme.AccentText, versions);
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
                // Text next to the pill sits on the same line as the pill's own letters.
                var pillTop = ImGui.GetCursorPosY();
                Theme.Pill("GitHub", Theme.Pink);
                ImGui.SameLine();
                ImGui.SetCursorPosY(pillTop + Theme.PillTextOffset("GitHub"));
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
