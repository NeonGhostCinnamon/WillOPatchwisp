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
    private bool onSettingsTab;   // the Settings tab was showing last frame (the search box and check button hide there)

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

        if (found == null)
            DrawUpdateBanner();
        if (!onSettingsTab)
            DrawToolbar();
        DrawFoundCard();

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
                    {
                        currentTab = "recent";
                        DrawRecentTab();
                    }
                }

                var available = plugin.VisibleUpdates.Count;
                var availableFlags = selectAvailableTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                selectAvailableTab = false;
                using (var tab = ImRaii.TabItem(available > 0 ? $"Available ({available})###available" : "Available###available", availableFlags))
                {
                    if (tab.Success)
                    {
                        currentTab = "available";
                        DrawAvailableTab();
                    }
                }

                var allFlags = selectAllTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                selectAllTab = false;
                using (var tab = ImRaii.TabItem("All plugins###all", allFlags))
                {
                    if (tab.Success)
                    {
                        currentTab = "all";
                        DrawAllTab();
                    }
                }

                var settingsOpen = false;
                using (var tab = ImRaii.TabItem("Settings###settings"))
                {
                    if (tab.Success)
                    {
                        settingsOpen = true;
                        currentTab = "settings";
                        DrawSettingsTab();
                    }
                }
                onSettingsTab = settingsOpen;
            }
        }

        DrawFooter();
        FitToContent();
    }

    private float footerReserve;
    private float childTopY;       // where the tab's list starts (window space)
    private float childContentH;   // how tall the list is

    /// <summary>Remembers the list's content height when the tab is done drawing (while its child is still open).</summary>
    private readonly struct ContentMeasure(UpdatesWindow window) : IDisposable
    {
        public void Dispose() => window.childContentH = ImGui.GetCursorPosY();
    }

    private string? currentTab;
    private string? lastTab;

    /// <summary>
    /// After a card was collapsed or expanded, and whenever another tab opens: make the window as tall as the list
    /// needs, but at most 60% of the screen (the lists can be very long).
    /// </summary>
    private void FitToContent()
    {
        var tabChanged = currentTab != lastTab;
        var first = lastTab == null;   // the first frame keeps the size the window had when it was closed
        lastTab = currentTab;
        var toggled = Theme.CardToggled;
        Theme.CardToggled = false;
        if (!toggled && (!tabChanged || first))
            return;
        if (childTopY <= 0 || childContentH <= 0)
            return;
        var max = ImGui.GetMainViewport().WorkSize.Y * 0.6f;
        var minH = NormalConstraints.MinimumSize.Y * ImGuiHelpers.GlobalScale;
        var h = childTopY + childContentH + footerReserve + ImGui.GetStyle().WindowPadding.Y;
        h = Math.Clamp(h, minH, Math.Max(minH, max));
        sizeOnce = new Vector2(ImGui.GetWindowSize().X, h) / ImGuiHelpers.GlobalScale;
    }
    private const string GitHubUrl = "https://github.com/NeonGhostCinnamon/WillOPatchwisp";

    /// <summary>Ko-fi and GitHub buttons, centred at the bottom of the window.</summary>
    private void DrawFooter()
    {
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var w = Math.Min(S(110), (ImGui.GetContentRegionAvail().X - spacing * 2) / 3f);
        var total = w * 3 + spacing * 2;
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
        ImGui.SameLine();
        if (Theme.DiscordButton("##discord", plugin.DiscordLogo, w))
            Util.OpenLink(Theme.DiscordUrl);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Join the Will-o Discord");
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
            if (updater.IsBusy)
                Theme.BusyBorderAroundItem();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(updater.IsAvailable
                    ? "Installs all available updates, one after another."
                    : "Opens Dalamud's plugin installer on the updates page.");
        });
    }

    /// <summary>Updates everything directly, or opens the installer if direct updating isn't available.</summary>
    private void UpdateAll()
    {
        if (!plugin.Updater.IsAvailable)
        {
            OpenUpdater();
            return;
        }
        var names = plugin.VisibleUpdates.Select(u => u.InternalName).Where(n => n != DirectUpdater.SelfName).ToList();
        if (names.Count > 0)
            plugin.Updater.UpdateMany(names);
        else
            OpenUpdater(SelfDisplayName);
    }

    private void UpdateOne(string internalName, string name)
    {
        // Will-o-Patchwisp can't update itself safely: send the player to Dalamud's installer for it.
        if (internalName == DirectUpdater.SelfName || !plugin.Updater.IsAvailable)
            OpenUpdater(name);
        else
            plugin.Updater.Update(internalName);
    }

    private const string SelfDisplayName = "Will-o-Patchwisp";
    private const string SelfUpdateNote =
        "Will-o-Patchwisp can't update itself: Dalamud has to unload it while it's still running the update, which can crash the game. Update it through Dalamud's plugin installer instead.";

    private bool SelfUpdateWaiting => plugin.VisibleUpdates.Any(u => u.InternalName == DirectUpdater.SelfName);

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

            // Found something: list it, so the player can pick what to update.
            if (checkResultCount > 0 && !plugin.Updater.IsBusy)
            {
                found = plugin.VisibleUpdates;
                foundAt = DateTime.Now;
                picked = found.Select(u => u.InternalName).Where(n => n != DirectUpdater.SelfName).ToHashSet();
                batch = null;
                closeFoundAt = null;
            }
        }

        var (checkLabel, look, icon) = plugin.Repos.IsRunning
            ? ("Checking...", Theme.CheckLook.Busy, (FontAwesomeIcon?)null)
            : DateTime.UtcNow >= checkResultUntil
                ? ("Check for updates", Theme.CheckLook.Normal, null)
                : checkResultCount == 0
                    ? ("No updates found", Theme.CheckLook.Nothing, FontAwesomeIcon.BoxOpen)
                    : ($"{checkResultCount} update{(checkResultCount == 1 ? "" : "s")} found", Theme.CheckLook.Found, null);
        var checkW = Theme.CheckButtonWidth(checkLabel, look == Theme.CheckLook.Busy || icon != null);

        var avail = ImGui.GetContentRegionAvail().X;
        var searchW = avail - checkW - style.ItemSpacing.X;
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
    }

    // ---------------------------------------------------------------- "N updates found" card

    private List<AvailableUpdate>? found;
    private DateTime foundAt;
    private HashSet<string> picked = new();
    private List<string>? batch;
    private DateTime? closeFoundAt;

    // Set by a "changelog" link: select the Available tab and jump to that plugin's card.
    private bool selectAvailableTab;

    /// <summary>
    /// After "Check for updates" found something: every update with a tick box, "Update selected" and "Update all".
    /// While updating it shows how far it got per plugin, and closes by itself a few seconds after it's done.
    /// </summary>
    private void DrawFoundCard()
    {
        if (found == null)
            return;

        var updater = plugin.Updater;
        if (closeFoundAt is { } at && DateTime.UtcNow >= at)
        {
            found = null;
            return;
        }

        var start = ImGui.GetCursorScreenPos();
        var busy = batch != null && updater.IsBusy;
        Theme.Card("found", () =>
        {
            var left = Theme.CardInnerLeft;
            var right = Theme.CardInnerRight;

            // Header
            string title;
            if (batch == null)
                title = $"{found.Count} update{(found.Count == 1 ? "" : "s")} found";
            else if (busy)
                title = $"Updating {Math.Min(updater.BatchDone + 1, Math.Max(1, updater.BatchTotal))} of {updater.BatchTotal}...";
            else
                title = "Done";
            using (plugin.TitleFont.Available ? plugin.TitleFont.Push() : null)
            {
                ImGui.SetWindowFontScale(0.55f);
                ImGui.TextColored(Theme.AccentText, title);
                ImGui.SetWindowFontScale(1f);
            }
            var headY = ImGui.GetItemRectMin().Y;
            var headH = ImGui.GetItemRectSize().Y;

            // Round button at the far right: collapses the card to just this row.
            var foldD = ImGui.GetFrameHeight();
            var foldClosed = plugin.Configuration.CollapsedCards.Contains(Theme.CardId);
            var headAfter = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(new Vector2(right - foldD, headY + (headH - foldD) / 2f));
            foldClosed = Theme.FoldButton(plugin.Configuration.CollapsedCards, plugin.Configuration.Save, foldClosed);
            ImGui.SetCursorScreenPos(headAfter);
            right -= foldD + S(8);
            var close = "x";
            var closeW = ImGui.CalcTextSize(close).X;
            var checkedText = batch == null ? $"checked {foundAt:HH:mm}" : string.Empty;
            var after = ImGui.GetCursorScreenPos();
            var ty = headY + (headH - ImGui.GetTextLineHeight()) / 2f;
            if (checkedText.Length > 0)
                ImGui.GetWindowDrawList().AddText(new Vector2(right - closeW - S(10) - ImGui.CalcTextSize(checkedText).X, ty), ImGui.GetColorU32(Theme.Muted), checkedText);
            if (!busy)
            {
                ImGui.SetCursorScreenPos(new Vector2(right - closeW, ty));
                if (Link("x##closefound", close))
                    found = null;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Close");
            }
            ImGui.SetCursorScreenPos(after);
            if (found == null || foldClosed)
                return;
            right = Theme.CardInnerRight;

            if (batch == null)
            {
                ImGui.TextColored(Theme.Muted, "Tick the ones to update.");
                var allW = ImGui.CalcTextSize("All").X + S(10) + ImGui.CalcTextSize("None").X;
                // Screen position, like the other right-side links: SameLine(x) would count from the card's
                // inner group and land past the edge.
                ImGui.SameLine();
                ImGui.SetCursorScreenPos(new Vector2(Math.Max(ImGui.GetCursorScreenPos().X, right - allW), ImGui.GetCursorScreenPos().Y));
                if (Link("All##pickall", "All"))
                    picked = found.Select(u => u.InternalName).Where(n => n != DirectUpdater.SelfName).ToHashSet();
                ImGui.SameLine(0, S(10));
                if (Link("None##picknone", "None"))
                    picked.Clear();
            }

            foreach (var u in found)
                DrawFoundRow(u, left, right);

            if (batch == null)
            {
                ImGuiHelpers.ScaledDummy(3f);
                var selected = found.Where(u => picked.Contains(u.InternalName)).Select(u => u.InternalName).ToList();
                using (ImRaii.Disabled(selected.Count == 0))
                {
                    if (Theme.Button($"Update selected ({selected.Count})"))
                        StartBatch(selected);
                }
                ImGui.SameLine();
                var allCount = found.Count(u => u.InternalName != DirectUpdater.SelfName);
                using (ImRaii.Disabled(allCount == 0))
                {
                    if (Theme.Button($"Update all ({allCount})"))
                        StartBatch(found.Select(u => u.InternalName).ToList());
                }
                if (ImGui.IsItemHovered() && !updater.IsAvailable)
                    ImGui.SetTooltip("Opens Dalamud's plugin installer on the updates page.");
                ImGui.SameLine();
                var notNow = "Not now";
                ImGui.SetCursorScreenPos(new Vector2(Math.Max(ImGui.GetCursorScreenPos().X, right - ImGui.CalcTextSize(notNow).X), ImGui.GetCursorScreenPos().Y));
                ImGui.AlignTextToFramePadding();
                if (Link("Not now##notnow", notNow))
                    found = null;
            }

            if (found != null && found.Any(u => u.InternalName == DirectUpdater.SelfName))
                SelfUpdateWarning();
        });

        // Rotating border while it's updating.
        if (busy)
            Theme.BusyBorder(start, new Vector2(start.X + ImGui.GetItemRectSize().X, ImGui.GetItemRectMin().Y), Theme.R(16));

        // Finished: close a few seconds later, unless something failed (then the player closes it).
        if (found != null && batch != null && !updater.IsBusy && closeFoundAt == null)
        {
            var failed = batch.Any(n => updater.States.TryGetValue(n, out var st) && st.Kind is UpdateStateKind.Failed or UpdateStateKind.NotFound);
            if (!failed)
                closeFoundAt = DateTime.UtcNow.AddSeconds(5);
        }
    }

    /// <summary>Amber note: Will-o-Patchwisp has an update, but it has to be installed through Dalamud.</summary>
    private void SelfUpdateWarning()
    {
        ImGuiHelpers.ScaledDummy(2f);
        Theme.Wrapped("Will-o-Patchwisp has an update too, but it can't update itself without risking a crash. Update it through Dalamud's plugin installer.", Theme.Amber);
        if (Link("Open Dalamud's installer##selfupdate", "Open Dalamud's installer"))
            OpenUpdater(SelfDisplayName);
        ImGuiHelpers.ScaledDummy(2f);
    }

    private void StartBatch(List<string> names)
    {
        if (!plugin.Updater.IsAvailable)
        {
            OpenUpdater();
            return;
        }
        batch = names.Where(n => n != DirectUpdater.SelfName).ToList();
        if (batch.Count == 0)
        {
            batch = null;
            OpenUpdater(SelfDisplayName);
            return;
        }
        plugin.Updater.UpdateMany(batch);
    }

    private void DrawFoundRow(AvailableUpdate u, float left, float right)
    {
        var dl = ImGui.GetWindowDrawList();
        ImGuiHelpers.ScaledDummy(1f);
        var top = ImGui.GetCursorScreenPos().Y;
        dl.AddLine(new Vector2(left, top), new Vector2(right, top), ImGui.GetColorU32(Theme.Border with { W = Theme.Border.W * 0.5f }));
        ImGuiHelpers.ScaledDummy(2f);

        using var id = ImRaii.PushId(u.InternalName);
        ImGui.AlignTextToFramePadding();
        if (batch == null && u.InternalName == DirectUpdater.SelfName)
        {
            var off = false;
            using (ImRaii.Disabled())
                ImGui.Checkbox("##pick", ref off);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(SelfUpdateNote);
        }
        else if (batch == null)
        {
            var on = picked.Contains(u.InternalName);
            if (ImGui.Checkbox("##pick", ref on))
            {
                if (on) picked.Add(u.InternalName);
                else picked.Remove(u.InternalName);
            }
        }
        else
        {
            var (text, color, spin) = BatchState(u.InternalName);
            Theme.Pill(text, color);
            if (spin)
                Theme.BusyBorderAroundItem(1.5f, Theme.R(8) + S(1.5f));
            if (ImGui.IsItemHovered() && plugin.Updater.States.TryGetValue(u.InternalName, out var st) && st.Message != null)
                ImGui.SetTooltip(st.Message);
        }
        ImGui.SameLine();
        var skipped = batch != null && !batch.Contains(u.InternalName);
        ImGui.TextColored(skipped ? Theme.Muted : Theme.Text, u.Name);
        ImGui.SameLine();
        if (skipped)
            ImGui.TextColored(Theme.Muted, $"stays on {u.InstalledVersion}");
        else
        {
            ImGui.TextColored(Theme.Muted, $"{u.InstalledVersion} ->");
            ImGui.SameLine(0, S(4));
            ImGui.TextColored(Theme.AccentText, u.LatestVersion);
        }

        // Right: TESTING tag and a "changelog" link.
        if (batch == null)
        {
            const string cl = "changelog";
            var clW = ImGui.CalcTextSize(cl).X;
            var tagW = u.IsTesting ? Theme.PillWidth("TESTING") + S(8) : 0f;
            var x = right - clW - tagW;
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(Math.Max(ImGui.GetCursorScreenPos().X, x), ImGui.GetCursorScreenPos().Y));
            if (u.IsTesting)
            {
                Theme.Pill("TESTING", Theme.Amber);
                ImGui.SameLine(0, S(8));
            }
            if (Link($"{cl}##cl", cl))
            {
                selectAvailableTab = true;
                search = string.Empty;
                expanded["a" + u.InternalName] = true;
                jumpToCard = "a" + u.InternalName;
            }
        }
    }

    /// <summary>The label of a plugin in the running (or finished) batch.</summary>
    private (string Text, Vector4 Color, bool Spin) BatchState(string name)
    {
        var grey = new Vector4(0.55f, 0.55f, 0.62f, 1f);
        if (batch == null || !batch.Contains(name))
            return ("Skipped", grey, false);
        plugin.Updater.States.TryGetValue(name, out var st);
        switch (st?.Kind)
        {
            case UpdateStateKind.Updated:
                return ("Updated", Theme.Green, false);
            case UpdateStateKind.Failed:
            case UpdateStateKind.NotFound:
                return ("Failed", Theme.Amber, false);
            case UpdateStateKind.Updating:
                // All waiting ones are marked "updating"; only the first of them is really being installed.
                var first = batch.FirstOrDefault(n => plugin.Updater.States.TryGetValue(n, out var s2) && s2.Kind == UpdateStateKind.Updating);
                return first == name ? ("Updating...", Theme.Pink, true) : ("Waiting", grey, false);
            default:
                return ("Waiting", grey, false);
        }
    }

    /// <summary>The Settings tab: the settings card and the ignored plugins, in a list that scrolls like the other tabs.</summary>
    private void DrawSettingsTab()
    {
        childTopY = ImGui.GetCursorPosY();
        using var child = ImRaii.Child("##settings", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;
        using var measure = new ContentMeasure(this);
        DrawSettings();
    }

    private void DrawSettings()
    {
        var config = plugin.Configuration;
        var changed = false;

        Theme.Card("settings", () =>
        {
            if (!Theme.CardHeader(plugin.TitleFont, "Settings", config.CollapsedCards, config.Save))
                return;
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
    private Dictionary<string, string>? ignoreNames;
    private DateTime ignoreNamesAt;

    /// <summary>Settings section to add plugins to, or remove them from, the ignore list.</summary>
    private void DrawIgnoreList()
    {
        var config = plugin.Configuration;
        if (!Theme.CardHeader(plugin.TitleFont, "Ignored plugins", config.CollapsedCards, config.Save,
                config.IgnoredPlugins.Count == 0 ? "nothing ignored" : $"{config.IgnoredPlugins.Count} ignored"))
            return;
        Theme.Wrapped("No update alerts for these, and they're left out of \"Available\" and \"Update all\".", Theme.Muted);

        // The installed plugins, looked up every few seconds instead of every frame.
        if (ignoreNames == null || DateTime.UtcNow - ignoreNamesAt > TimeSpan.FromSeconds(10))
        {
            ignoreNames = Plugin.PluginInterface.InstalledPlugins
                .Where(p => !p.IsDev)
                .GroupBy(p => p.InternalName)
                .ToDictionary(g => g.Key, g => g.First().Name);
            ignoreNamesAt = DateTime.UtcNow;
        }
        var names = ignoreNames;

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

    // Set by "See older updates": select the All plugins tab and jump to that plugin's card.
    private bool selectAllTab;
    private string? jumpToCard;

    /// <summary>Open this plugin's card on the All plugins tab, with its older updates.</summary>
    public void ShowOlder(string internalName)
    {
        selectAllTab = true;
        search = string.Empty;
        var id = "i" + internalName;
        expanded[id] = true;
        jumpToCard = id;
    }
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

        childTopY = ImGui.GetCursorPosY();
        using var child = ImRaii.Child("##recent", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;
        using var measure = new ContentMeasure(this);

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
            float titleH;
            using (plugin.TitleFont.Available ? plugin.TitleFont.Push() : null)
            {
                ImGui.SetWindowFontScale(0.55f);
                titleH = ImGui.GetTextLineHeight();
                ImGui.SetWindowFontScale(1f);
            }
            lineH = Math.Max(lineH, titleH);

            // Header: chevron, date, "N plugins" on the right. Click anywhere on it to open/minimize.
            var headTop = ImGui.GetCursorScreenPos();
            var countText = $"{rows.Count} plugin{(rows.Count == 1 ? "" : "s")}";
            if (ImGui.InvisibleButton("##head", new Vector2(right - left, lineH)))
            {
                dayOpen[day] = open = !open;
                Theme.CardToggled = true;
            }
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
            using (plugin.TitleFont.Available ? plugin.TitleFont.Push() : null)
            {
                ImGui.SetWindowFontScale(0.55f);
                dl.AddText(headTop + new Vector2(lineH + S(4), (lineH - ImGui.GetTextLineHeight()) / 2f), ImGui.GetColorU32(Theme.AccentText), $"{day:dddd d MMMM yyyy}");
                ImGui.SetWindowFontScale(1f);
            }
            dl.AddText(new Vector2(right - ImGui.CalcTextSize(countText).X, headTop.Y + (lineH - ImGui.GetTextLineHeight()) / 2f), ImGui.GetColorU32(Theme.Muted), countText);

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
        Theme.Pill(toggleText, open ? HideGrey : Theme.Pink);
        var pillHovered = ImGui.IsItemHovered();
        ImGui.SetCursorScreenPos(new Vector2(left, Math.Max(after.Y, ImGui.GetItemRectMax().Y + style.ItemSpacing.Y)));

        if (rowHovered || pillHovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if ((rowHovered || pillHovered) && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            open = !open;
            expanded[id] = open;
            Theme.CardToggled = true;
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

        // The plugin itself may have shipped without a changelog or repository link: the repository listing often has them.
        var changelog = e.Changelog ?? plugin.Repos.ChangelogFor(e.InternalName, e.NewVersion);
        var repoUrl = RepoFor(e.InternalName, e.RepoUrl);

        // Changelog, indented under the name with a thin accent line on its left.
        var indent = S(44);
        var noteTop = ImGui.GetCursorScreenPos();
        ImGui.Indent(indent);
        using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (right - left - indent)))
        {
            if (changelog != null)
                DrawChangelog(changelog);
            else
                DrawGitHubFallback(repoUrl, e.NewVersion);

            ImGuiHelpers.ScaledDummy(1f);
            DrawSourceButtons(key, e.InternalName, repoUrl, changelog == null);

            var ignored = plugin.IsIgnored(e.InternalName);
            if (IgnoreButton($"{(ignored ? "Stop ignoring" : "Ignore")}##{key}", ignored))
                plugin.SetIgnored(e.InternalName, !ignored);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(ignored
                    ? "Show update alerts for this plugin again."
                    : "Hide this plugin's updates: no alerts, not in \"Available\" or \"Update all\". You can undo this in Settings.");

            // "See older updates ->" on the right (under the buttons when there's no room).
            const string olderText = "See older updates ->";
            var linkW = ImGui.CalcTextSize(olderText).X;
            ImGui.SameLine();
            var linkX = right - linkW;
            if (ImGui.GetCursorScreenPos().X + S(8) > linkX)
                ImGui.NewLine();
            ImGui.SetCursorScreenPos(new Vector2(Math.Max(ImGui.GetCursorScreenPos().X, linkX), ImGui.GetCursorScreenPos().Y));
            ImGui.AlignTextToFramePadding();
            if (Link($"{olderText}##older{key}", olderText))
                ShowOlder(e.InternalName);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Earlier versions of {e.Name} and what changed in them (All plugins tab).");
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
            if (SelfUpdateWaiting)
                SelfUpdateWarning();
            var busy = plugin.Updater.IsBusy;
            using (ImRaii.Disabled(busy))
            {
                if (Theme.Button(busy ? "Updating..." : $"Update all plugins ({visible.Count})", new Vector2(ImGui.GetContentRegionAvail().X, 0)))
                    UpdateAll();
            }
            if (busy)
                Theme.BusyBorderAroundItem();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !plugin.Updater.IsAvailable)
                ImGui.SetTooltip("Opens Dalamud's plugin installer on the updates page.");
            ImGuiHelpers.ScaledDummy(2f);
        }

        childTopY = ImGui.GetCursorPosY();
        using var child = ImRaii.Child("##available", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;
        using var measure = new ContentMeasure(this);

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

        childTopY = ImGui.GetCursorPosY();
        using var child = ImRaii.Child("##all", new Vector2(0, -footerReserve), false);
        if (!child.Success)
            return;
        using var measure = new ContentMeasure(this);

        foreach (var p in installedCache!.Where(p => Matches(p.Name, p.Changelog)))
        {
            DrawCard(
                $"i{p.InternalName}", p.Name, p.Version,
                defaultOpen: false,
                tag: plugin.IsIgnored(p.InternalName) ? ("IGNORED", new Vector4(0.55f, 0.55f, 0.62f, 1f)) : p.IsTesting ? ("TESTING", Theme.Amber) : null,
                subtitle: p.ReleasedAt is { } r ? $"Released {Ago(r)}" : null,
                p.Changelog, p.RepoUrl, version: p.Version, internalName: p.InternalName, older: true);
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
        string? subtitle, string? changelog, string? repoUrl, (string InternalName, string Name)? update = null, string? version = null, string? internalName = null, bool older = false)
    {
        var open = expanded.TryGetValue(id, out var o) ? o : defaultOpen;

        repoUrl = RepoFor(internalName, repoUrl);
        if (changelog == null && internalName != null && version != null)
            changelog = plugin.Repos.ChangelogFor(internalName, version);

        // "See older updates" jumped here: scroll this card into view.
        if (jumpToCard == id)
        {
            ImGui.SetScrollHereY(0.1f);
            jumpToCard = null;
        }

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
            Theme.Pill(toggleText, open ? HideGrey : Theme.Pink);

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
                Theme.CardToggled = true;
            }

            if (!open)
                return;

            ImGuiHelpers.ScaledDummy(2f);
            if (changelog != null)
                DrawChangelog(changelog);
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
                        Theme.BusyBorderAroundItem();
                        break;
                    case UpdateStateKind.Updated:
                        ImGui.AlignTextToFramePadding();
                        ImGui.TextColored(Theme.Green, "Updated!");
                        break;
                    case var _ when up.InternalName == DirectUpdater.SelfName:
                        if (Theme.SmallButton("Update in Dalamud"))
                            OpenUpdater(up.Name);
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip(SelfUpdateNote);
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

                ImGui.SameLine();

                if (state?.Kind is UpdateStateKind.Failed or UpdateStateKind.NotFound && state.Message != null)
                {
                    ImGui.NewLine();
                    Theme.Wrapped(state.Message, Theme.Amber);
                }
            }

            if (update == null)
                ImGuiHelpers.ScaledDummy(2f);
            DrawSourceButtons(id, internalName, repoUrl, changelog == null);

            if (internalName != null)
            {

                var ignored = plugin.IsIgnored(internalName);
                if (IgnoreButton(ignored ? "Stop ignoring" : "Ignore", ignored))
                    plugin.SetIgnored(internalName, !ignored);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(ignored
                        ? "Show update alerts for this plugin again."
                        : "Hide this plugin's updates: no alerts, not in \"Available\" or \"Update all\". You can undo this in Settings.");
            }

            if (older && internalName != null)
                DrawOlderUpdates(internalName, repoUrl, version);
        });
    }

    // ---------------------------------------------------------------- older updates (All plugins tab)

    private const int OlderFirst = 10;
    private const int OlderStep = 5;
    private readonly Dictionary<string, int> olderShown = new();
    private readonly Dictionary<string, bool> olderOpen = new();

    private sealed record OlderEntry(string Version, DateTime? Date, string Body, string? Url);

    /// <summary>
    /// Earlier versions of a plugin and their notes: from its GitHub releases, or from the updates
    /// Will-o-Patchwisp recorded itself when the plugin isn't on GitHub. Up to 10 at first, then 5 more per click.
    /// </summary>
    private void DrawOlderUpdates(string internalName, string? repoUrl, string? version)
    {
        var left = Theme.CardInnerLeft;
        var right = Theme.CardInnerRight;
        var dl = ImGui.GetWindowDrawList();

        ImGuiHelpers.ScaledDummy(3f);
        var lineY = ImGui.GetCursorScreenPos().Y;
        dl.AddLine(new Vector2(left, lineY), new Vector2(right, lineY), ImGui.GetColorU32(Theme.Border), S(1));
        ImGuiHelpers.ScaledDummy(3f);

        // Where the list comes from.
        var current = GitHubChangelogs.Normalize(version);
        List<OlderEntry> entries;
        string source;
        string? releasesUrl = null;
        var gh = repoUrl != null ? plugin.GitHub.GetReleases(repoUrl) : null;
        if (gh is { Status: GitHubLookupStatus.Loading })
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(4));
            ImGui.BeginGroup();
            ImGui.TextColored(Theme.AccentText, "Older updates");
            ImGui.SameLine();
            ImGui.TextColored(Theme.Muted, "looking on GitHub...");
            ImGui.EndGroup();
            Theme.BusyBorderAroundItem(4f, S(8));
            ImGuiHelpers.ScaledDummy(2f);
            return;
        }

        var fromGitHub = gh is { Status: GitHubLookupStatus.Found } ? OlderFromGitHub(gh.Items, current) : new List<OlderEntry>();
        if (fromGitHub.Count > 0)
        {
            entries = fromGitHub;
            source = "from GitHub releases";
            releasesUrl = gh!.Url;
        }
        else
        {
            entries = OlderFromHistory(internalName, current);
            source = "from your update history";
        }

        var shown = olderShown.TryGetValue(internalName, out var n) ? n : OlderFirst;
        var countText = entries.Count == 0 ? string.Empty : $"{Math.Min(shown, entries.Count)} of {entries.Count}";

        ImGui.TextColored(Theme.AccentText, "Older updates");
        ImGui.SameLine();
        ImGui.TextColored(Theme.Muted, source);
        if (countText.Length > 0)
        {
            ImGui.SameLine();
            ImGui.SetCursorScreenPos(new Vector2(Math.Max(ImGui.GetCursorScreenPos().X, right - ImGui.CalcTextSize(countText).X), ImGui.GetCursorScreenPos().Y));
            ImGui.TextColored(Theme.Muted, countText);
        }

        if (entries.Count == 0)
        {
            Theme.Wrapped(gh is { Status: GitHubLookupStatus.Failed }
                ? $"GitHub couldn't be checked. {gh.Text}"
                : "No older updates found yet. Updates Will-o-Patchwisp sees from now on are kept here too.", Theme.Muted);
            if (gh is { Status: GitHubLookupStatus.Failed } && repoUrl != null && Theme.SmallButton($"Try GitHub again##older{internalName}"))
                plugin.GitHub.RetryReleases(repoUrl);
            return;
        }

        for (var i = 0; i < Math.Min(shown, entries.Count); i++)
            DrawOlderEntry(internalName, entries[i], i == 0, left, right);

        ImGuiHelpers.ScaledDummy(2f);
        if (shown < entries.Count)
        {
            var more = Math.Min(OlderStep, entries.Count - shown);
            if (Link($"Show {more} more##more{internalName}", $"Show {more} more"))
                olderShown[internalName] = shown + OlderStep;
            ImGui.SameLine();
        }
        if (releasesUrl != null)
        {
            const string all = "Open all releases on GitHub";
            var x = right - ImGui.CalcTextSize(all).X;
            if (ImGui.GetCursorScreenPos().X > x - S(8))
                ImGui.NewLine();
            ImGui.SetCursorScreenPos(new Vector2(Math.Max(ImGui.GetCursorScreenPos().X, x), ImGui.GetCursorScreenPos().Y));
            if (Link($"{all}##rel{internalName}", all))
                Util.OpenLink(releasesUrl);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(releasesUrl);
        }
        else if (shown < entries.Count)
        {
            ImGui.NewLine();
        }
    }

    /// <summary>One older version: a folding line (version, date, first change) with the notes under it when open.</summary>
    private void DrawOlderEntry(string internalName, OlderEntry e, bool first, float left, float right)
    {
        var key = $"{internalName}|{e.Version}";
        var open = olderOpen.TryGetValue(key, out var o) ? o : first;
        var dl = ImGui.GetWindowDrawList();
        var lineH = ImGui.GetTextLineHeight();
        var barX = left + S(2);
        var x0 = left + S(12);

        var top = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(new Vector2(left, top.Y));
        if (ImGui.InvisibleButton($"##o{key}", new Vector2(right - left, lineH + S(2))))
        {
            open = !open;
            olderOpen[key] = open;
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

        var x = x0;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var chev = (open ? FontAwesomeIcon.CaretDown : FontAwesomeIcon.CaretRight).ToIconString();
            var ink = Theme.InkBounds(chev);
            dl.AddText(new Vector2(x - ink.X, top.Y + (lineH - (ink.Y + ink.W)) / 2f), ImGui.GetColorU32(hovered ? Theme.AccentText : Theme.Muted), chev);
            x += S(12);
        }
        dl.AddText(new Vector2(x, top.Y), ImGui.GetColorU32(hovered ? Theme.AccentText : Theme.Text), e.Version);
        x += ImGui.CalcTextSize(e.Version).X + S(8);
        if (e.Date is { } d)
        {
            var date = d.ToString("d MMM yyyy");
            dl.AddText(new Vector2(x, top.Y), ImGui.GetColorU32(Theme.Muted), date);
            x += ImGui.CalcTextSize(date).X + S(8);
        }
        if (!open)
        {
            // First change, cut off at the card edge.
            var firstLine = e.Body.Split('\n').Select(l => l.Trim().TrimStart('-', '*', ' ')).FirstOrDefault(l => l.Length > 0);
            if (firstLine != null)
            {
                dl.PushClipRect(new Vector2(x, top.Y), new Vector2(right, top.Y + lineH + S(2)), true);
                dl.AddText(new Vector2(x, top.Y), ImGui.GetColorU32(Theme.Muted), "· " + firstLine);
                dl.PopClipRect();
            }
        }

        ImGui.SetCursorScreenPos(new Vector2(left, top.Y + lineH + S(4)));
        if (open)
        {
            ImGui.SetCursorScreenPos(new Vector2(x0, ImGui.GetCursorScreenPos().Y));
            ImGui.BeginGroup();
            using (ImRaii.TextWrapPos(ImGui.GetCursorPosX() + (right - x0)))
                if (e.Body.Length > 0)
                    DrawChangelog(e.Body);
                else
                    Theme.Wrapped("No notes for this version.", Theme.Muted);
            ImGui.EndGroup();
            ImGuiHelpers.ScaledDummy(2f);
        }

        // Thin accent line on the left of each version.
        var bottom = ImGui.GetCursorScreenPos().Y - S(2);
        dl.AddLine(new Vector2(barX, top.Y), new Vector2(barX, bottom), ImGui.GetColorU32(Theme.Pink with { W = open ? 0.8f : 0.4f }), S(2));
    }

    /// <summary>Releases older than the installed version (newest first).</summary>
    private static List<OlderEntry> OlderFromGitHub(IReadOnlyList<GitHubRelease> releases, string? current)
    {
        var list = releases.ToList();
        var index = current == null ? -1 : list.FindIndex(r => r.Version == current);
        IEnumerable<GitHubRelease> older;
        if (index >= 0)
            older = list.Skip(index + 1);
        else if (current != null && Version.TryParse(Pad(current), out var cur))
            older = list.Where(r => r.Version == null || !Version.TryParse(Pad(r.Version), out var v) || v < cur);
        else
            older = list;
        return older.Select(r => new OlderEntry(r.Version ?? r.Tag, r.Date, r.Body, r.Url)).ToList();
    }

    /// <summary>Updates Will-o-Patchwisp recorded for this plugin, minus the installed version (newest first).</summary>
    private List<OlderEntry> OlderFromHistory(string internalName, string? current)
        => plugin.Configuration.History
            .Where(h => h.InternalName == internalName && GitHubChangelogs.Normalize(h.NewVersion) != current)
            .GroupBy(h => h.NewVersion)
            .Select(g => g.OrderByDescending(h => h.DetectedAt).First())
            .OrderByDescending(h => h.DetectedAt)
            .Select(h => new OlderEntry(h.NewVersion, h.DetectedAt, h.Changelog ?? string.Empty, null))
            .ToList();

    private static string Pad(string v)
    {
        var parts = v.Split('.').ToList();
        while (parts.Count < 2) parts.Add("0");
        return string.Join('.', parts);
    }

    // "Hide" (an open card) is grey, so the pink "Show" stands out on closed ones.
    private static readonly Vector4 HideGrey = new(0.55f, 0.55f, 0.62f, 1f);
    private static readonly Vector4 IgnoreRed = new(0.90f, 0.42f, 0.42f, 1f);

    /// <summary>"Ignore" in a soft red; "Stop ignoring" keeps the normal pink.</summary>
    private static bool IgnoreButton(string label, bool ignored)
    {
        if (ignored || Theme.UseDalamud)
            return Theme.SmallButton(label);
        ImGui.PushStyleColor(ImGuiCol.Button, IgnoreRed);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.96f, 0.55f, 0.55f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(0.80f, 0.32f, 0.32f, 1f));
        var clicked = Theme.SmallButton(label);
        ImGui.PopStyleColor(3);
        return clicked;
    }

    // ---------------------------------------------------------------- changelog text with clickable links

    private static readonly System.Text.RegularExpressions.Regex MdLink = new(@"\[([^\]]+)\]\((https?://[^)\s]+)\)");
    private static readonly System.Text.RegularExpressions.Regex BareUrl = new(@"https?://[^\s<>()\[\]]+");

    /// <summary>
    /// A changelog as wrapped text: headings ("## ...") in the accent colour, "*" bullets as "-", bold/code marks
    /// removed, and every link clickable (pink, underlined, full address in the tooltip). Long GitHub links get a
    /// short name: "#1393" for a pull request or issue, "7.5.7.5...7.5.8.1" for a compare link.
    /// </summary>
    private static void DrawChangelog(string text)
    {
        var left = ImGui.GetCursorScreenPos().X;
        var right = Math.Min(Theme.CardInnerRight, ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X);
        if (right - left < S(40))
            right = left + S(40);
        var lineH = ImGui.GetTextLineHeight();
        var spacing = ImGui.GetStyle().ItemSpacing.Y;
        var dl = ImGui.GetWindowDrawList();
        var startY = ImGui.GetCursorScreenPos().Y;
        var y = startY;
        var blank = false;
        var n = 0;
        using var id = ImRaii.PushId(text.GetHashCode());

        foreach (var parsed in ParseChangelog(text))
        {
            if (parsed == null)
            {
                // Several empty lines in a row: one small gap.
                if (!blank && n > 0)
                    y += lineH * 0.5f;
                blank = true;
                continue;
            }
            blank = false;
            var (heading, bullet, pieces) = parsed;
            var x = left;
            var indent = bullet ? ImGui.CalcTextSize("- ").X : 0f;
            foreach (var (piece, url) in pieces)
            {
                var label = piece;
                var w = ImGui.CalcTextSize(label.TrimEnd()).X;
                if (x > left + indent && x + w > right)
                {
                    x = left + indent;
                    y += lineH + spacing * 0.5f;
                }
                // A link wider than the whole line: cut it short.
                if (url != null && w > right - x)
                {
                    while (label.Length > 4 && ImGui.CalcTextSize(label + "...").X > right - x)
                        label = label[..^1];
                    label += "...";
                    w = ImGui.CalcTextSize(label).X;
                }

                var p = new Vector2(x, y);
                if (url != null)
                {
                    ImGui.SetCursorScreenPos(p);
                    var clicked = ImGui.InvisibleButton($"##l{n}", new Vector2(Math.Max(1, w), lineH));
                    var hovered = ImGui.IsItemHovered();
                    var col = ImGui.GetColorU32(hovered ? Theme.Text : Theme.AccentText);
                    dl.AddText(p, col, label);
                    dl.AddLine(new Vector2(x, y + lineH), new Vector2(x + w, y + lineH), col, S(1));
                    if (hovered)
                    {
                        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        ImGui.SetTooltip(url);
                    }
                    if (clicked)
                        Util.OpenLink(url);
                    x += w + ImGui.CalcTextSize(" ").X;
                }
                else
                {
                    dl.AddText(p, ImGui.GetColorU32(heading ? Theme.AccentText : Theme.TextSoft), label);
                    x += ImGui.CalcTextSize(label).X;
                }
                n++;
            }
            y += lineH + spacing * 0.5f;
        }

        // Reserve the space that was drawn on.
        ImGui.SetCursorScreenPos(new Vector2(left, startY));
        ImGui.Dummy(new Vector2(right - left, Math.Max(lineH, y - startY - spacing * 0.5f)));
    }

    private sealed record ChangelogLine(bool Heading, bool Bullet, List<(string Text, string? Url)> Pieces);

    // Changelogs already split into words and links (null = an empty line), so that isn't redone every frame.
    private static readonly Dictionary<string, List<ChangelogLine?>> ParsedChangelogs = new();

    private static List<ChangelogLine?> ParseChangelog(string text)
    {
        if (ParsedChangelogs.TryGetValue(text, out var cached))
            return cached;
        if (ParsedChangelogs.Count > 200)
            ParsedChangelogs.Clear();

        var result = new List<ChangelogLine?>();
        foreach (var raw in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                result.Add(null);
                continue;
            }

            var heading = System.Text.RegularExpressions.Regex.IsMatch(line, @"^\s*#{1,6}\s");
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*#{1,6}\s*", string.Empty);
            line = System.Text.RegularExpressions.Regex.Replace(line, @"^(\s*)[*+]\s+", "$1- ");
            line = line.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);

            // Pieces: plain words and links.
            var pieces = new List<(string Text, string? Url)>();
            var pos = 0;
            while (pos < line.Length)
            {
                var md = MdLink.Match(line, pos);
                var bare = BareUrl.Match(line, pos);
                System.Text.RegularExpressions.Match? next = md.Success && (!bare.Success || md.Index <= bare.Index) ? md : bare.Success ? bare : null;
                if (next == null)
                {
                    AddWords(pieces, line[pos..]);
                    break;
                }
                AddWords(pieces, line[pos..next.Index]);
                if (next == md)
                {
                    pieces.Add((md.Groups[1].Value, md.Groups[2].Value));
                }
                else
                {
                    var url = bare.Value.TrimEnd('.', ',', ';', ':', '!', '?', '\'', '"');
                    pieces.Add((ShortLink(url), url));
                    if (url.Length < bare.Value.Length)
                        AddWords(pieces, bare.Value[url.Length..]);
                }
                pos = next.Index + next.Length;
            }
            result.Add(new ChangelogLine(heading, line.StartsWith("- "), pieces));
        }
        ParsedChangelogs[text] = result;
        return result;
    }

    private static void AddWords(List<(string, string?)> pieces, string text)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"\S+\s*|\s+"))
            pieces.Add((m.Value, null));
    }

    /// <summary>Short name for a link: "#1393", "7.5.7.5...7.5.8.1", "abc1234", or the address without "https://".</summary>
    private static string ShortLink(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"^https?://github\.com/[^/]+/[^/]+/(pull|issues)/(\d+)");
        if (m.Success)
            return "#" + m.Groups[2].Value;
        m = System.Text.RegularExpressions.Regex.Match(url, @"^https?://github\.com/[^/]+/[^/]+/compare/([^?#]+)");
        if (m.Success)
            return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(url, @"^https?://github\.com/[^/]+/[^/]+/commit/([0-9a-f]{7})");
        if (m.Success)
            return m.Groups[1].Value;
        m = System.Text.RegularExpressions.Regex.Match(url, @"^https?://github\.com/[^/]+/[^/]+/releases/tag/([^?#]+)");
        if (m.Success)
            return m.Groups[1].Value;
        var shortUrl = System.Text.RegularExpressions.Regex.Replace(url, @"^https?://(www\.)?", string.Empty).TrimEnd('/');
        return shortUrl.Length > 48 ? shortUrl[..45] + "..." : shortUrl;
    }

    /// <summary>Pink underlined text that works like a link.</summary>
    private static bool Link(string id, string text)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(text);
        var clicked = ImGui.InvisibleButton(id, size);
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        var col = ImGui.GetColorU32(hovered ? Theme.Text : Theme.AccentText);
        dl.AddText(pos, col, text);
        dl.AddLine(new Vector2(pos.X, pos.Y + size.Y), new Vector2(pos.X + size.X, pos.Y + size.Y), col, S(1));
        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    /// <summary>The plugin's repository link: its own, or the one its repository listing gives.</summary>
    private string? RepoFor(string? internalName, string? repoUrl)
    {
        if (!string.IsNullOrWhiteSpace(repoUrl))
            return repoUrl;
        return internalName != null && plugin.Repos.Known.TryGetValue(internalName, out var k) ? k.RepoUrl : null;
    }

    /// <summary>
    /// "Open repository", and "Open on GitHub" (its releases) when there's no changelog. When the plugin doesn't say
    /// where its code lives: a button to the plugin list it was installed from (e.g. "Open puni.sh").
    /// Ends with SameLine so more buttons can follow.
    /// </summary>
    private void DrawSourceButtons(string key, string? internalName, string? repoUrl, bool noChangelog)
    {
        if (repoUrl != null)
        {
            if (Theme.SmallButton($"Open repository##repo{key}"))
                Util.OpenLink(repoUrl);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(repoUrl);
            ImGui.SameLine();

            if (noChangelog && GitHubChangelogs.ParseRepo(repoUrl) is { } gh)
            {
                var releases = $"https://github.com/{gh.Owner}/{gh.Repo}/releases";
                if (Theme.SmallButton($"Open on GitHub##gh{key}"))
                    Util.OpenLink(releases);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(releases);
                ImGui.SameLine();
            }
            return;
        }

        if (internalName != null && plugin.Repos.Known.TryGetValue(internalName, out var k) && ListSite(k.ListUrl) is { } site)
        {
            if (Theme.SmallButton($"{site.Label}##list{key}"))
                Util.OpenLink(site.Url);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"This plugin doesn't say where its code lives.\nOpens the plugin list it was installed from:\n{k.ListUrl}");
            ImGui.SameLine();
        }
    }

    /// <summary>
    /// A page to open for a plugin list: the GitHub repository it lives in, or the website it's hosted on
    /// (e.g. https://puni.sh/api/repository/veyn -> "Open puni.sh", https://puni.sh).
    /// </summary>
    private static (string Label, string Url)? ListSite(string? listUrl)
    {
        if (string.IsNullOrWhiteSpace(listUrl) || !Uri.TryCreate(listUrl, UriKind.Absolute, out var uri))
            return null;
        if (uri.Host.Equals("kamori.goats.dev", StringComparison.OrdinalIgnoreCase))
            return null; // Dalamud's main list: no page of its own to show

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if ((uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
             || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) && parts.Length >= 2)
            return ("Open plugin list on GitHub", $"https://github.com/{parts[0]}/{parts[1]}");
        if (uri.Host.EndsWith(".github.io", StringComparison.OrdinalIgnoreCase) && parts.Length >= 1)
            return ("Open plugin list on GitHub", $"https://github.com/{uri.Host[..^".github.io".Length]}/{parts[0]}");

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return ($"Open {host}", $"{uri.Scheme}://{uri.Host}/");
    }

    /// <summary>Grey text that wraps word by word, with every "GitHub" in pink.</summary>
    private static void SourceLine(string text)
    {
        var startX = ImGui.GetCursorScreenPos().X;
        var maxX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var first = true;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"GitHub|[^\sG]+\s*|G\s*|\s+"))
        {
            var piece = m.Value;
            if (!first)
            {
                if (ImGui.GetItemRectMax().X + ImGui.CalcTextSize(piece.TrimEnd()).X <= maxX)
                    ImGui.SameLine(0, 0);
                else
                    ImGui.SetCursorScreenPos(new Vector2(startX, ImGui.GetCursorScreenPos().Y));
            }
            ImGui.TextColored(piece == "GitHub" ? Theme.AccentText : Theme.Muted, piece);
            first = false;
        }
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
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + S(4));
                Theme.Wrapped("No changelog was included. Looking on GitHub...", Theme.Muted);
                Theme.BusyBorderAroundItem(4f, S(8));
                break;

            case GitHubLookupStatus.Found:
                // Where it came from, with the word "GitHub" in the accent colour.
                SourceLine(result.Source ?? "From GitHub");
                DrawChangelog(result.Text ?? string.Empty);
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
