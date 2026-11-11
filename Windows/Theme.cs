using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace WillOPatchwisp.Windows;

/// <summary>
/// Colours and helpers matching the Will o' Cinnamon website:
/// grey-violet background, glassy cards with soft white borders, pink accent bar,
/// pink buttons with a white outline, and headings where one word is pink.
/// </summary>
internal static class Theme
{
    /// <summary>Which look the window uses: the website's dark or light colours, or Dalamud's own style.</summary>
    public static ThemeMode Mode { get; set; } = ThemeMode.Dark;

    /// <summary>True for the dark website colours and for Dalamud's style (which is dark too).</summary>
    public static bool Dark => Mode != ThemeMode.Light;

    /// <summary>Use the colours of the Dalamud style the player has selected.</summary>
    public static bool UseDalamud => Mode == ThemeMode.Dalamud;

    // Dalamud's style colours, read at the start of every frame (so a style change shows right away).
    private static Vector4[] dalamudColors = Array.Empty<Vector4>();
    private static Vector4 D(ImGuiCol col) => (int)col < dalamudColors.Length ? dalamudColors[(int)col] : new Vector4(1f, 1f, 1f, 1f);

    // Light: the site's default grey-violet. Dark: the site's body.dark colours.
    public static Vector4 Background => UseDalamud ? D(ImGuiCol.WindowBg) : Dark ? Rgb(43, 43, 52) : Rgb(107, 107, 121);
    public static Vector4 TitleBg    => Dark ? Rgb(34, 34, 42) : Rgb(88, 88, 102);
    public static Vector4 TitleBgOn  => Dark ? Rgb(40, 38, 50) : Rgb(96, 92, 112);
    public static Vector4 Text       => UseDalamud ? D(ImGuiCol.Text) : Dark ? Rgb(220, 216, 216) : Rgb(235, 231, 231);
    public static Vector4 TextSoft   => Text with { W = 0.92f };
    // Secondary text: lighter than before (was 0.55 / 0.60) so it's easier to read.
    public static Vector4 Muted      => Text with { W = Dark ? 0.72f : 0.76f };
    public static Vector4 Glass      => UseDalamud ? D(ImGuiCol.FrameBg) with { W = D(ImGuiCol.FrameBg).W * 0.6f } : new(1f, 1f, 1f, Dark ? 0.05f : 0.08f);
    public static Vector4 GlassHover => UseDalamud ? D(ImGuiCol.FrameBgHovered) : new(1f, 1f, 1f, Dark ? 0.10f : 0.14f);
    public static Vector4 Border     => UseDalamud ? D(ImGuiCol.Border) with { W = Math.Max(D(ImGuiCol.Border).W, 0.5f) } : new(1f, 1f, 1f, Dark ? 0.18f : 0.25f);
    public static Vector4 Field      => UseDalamud ? D(ImGuiCol.FrameBg) : new(1f, 1f, 1f, Dark ? 0.07f : 0.10f);

    /// <summary>The website pink. Always used for the swinging "o", whatever the theme.</summary>
    public static readonly Vector4 BrandPink = Rgb(255, 126, 182);

    /// <summary>Accent fill (buttons, pills, bars): pink, or Dalamud's button colour in the Dalamud theme.</summary>
    public static Vector4 Pink     => UseDalamud ? D(ImGuiCol.Button) : BrandPink;
    public static Vector4 PinkDark => UseDalamud ? D(ImGuiCol.ButtonActive) : Rgb(211, 20, 103);

    /// <summary>Accent for text (day headings, changed version numbers): pink, or plain text colour in the Dalamud theme.</summary>
    public static Vector4 AccentText => UseDalamud ? D(ImGuiCol.Text) : BrandPink;

    /// <summary>Outline of filled pills and badges: white, or Dalamud's border colour.</summary>
    public static Vector4 Outline => UseDalamud ? Border : White;
    public static readonly Vector4 White    = new(1f, 1f, 1f, 1f);
    public static readonly Vector4 Green    = Rgb(120, 220, 150);
    public static readonly Vector4 Amber    = Rgb(242, 185, 95);
    public static readonly Vector4 Moon     = Rgb(179, 168, 204);

    // White on pink is only 2.4:1 contrast. In dark mode, pink controls use dark plum text instead (6.9:1),
    // and turn lighter pink on hover (8.6:1). Light mode keeps the website's white-on-pink look.
    public static Vector4 OnPink        => UseDalamud ? D(ImGuiCol.Text) : Dark ? Rgb(38, 30, 42) : White;
    public static Vector4 ButtonHover   => UseDalamud ? D(ImGuiCol.ButtonHovered) : Dark ? Rgb(255, 160, 205) : PinkDark;
    public static Vector4 ButtonPressed => UseDalamud ? D(ImGuiCol.ButtonActive) : Dark ? Rgb(240, 105, 165) : PinkDark;

    private static int colorCount;
    private static int varCount;

    private static Vector4 Rgb(int r, int g, int b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    private static float S(float v) => v * ImGuiHelpers.GlobalScale;

    /// <summary>Corner radius: the website's soft corners, or Dalamud's own frame rounding in the Dalamud theme.</summary>
    public static float R(float v) => UseDalamud ? ImGui.GetStyle().FrameRounding : S(v);

    public static void Push()
    {
        colorCount = 0;
        varCount = 0;

        if (UseDalamud)
        {
            // Keep Dalamud's style as it is; only remember its colours for the custom-drawn parts.
            dalamudColors = ImGui.GetStyle().Colors.ToArray();
            Color(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
            return;
        }

        Color(ImGuiCol.WindowBg, Background with { W = 0.97f });
        Color(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        Color(ImGuiCol.PopupBg, Background);
        Color(ImGuiCol.Border, Border);
        Color(ImGuiCol.Text, Text);
        Color(ImGuiCol.TextDisabled, Muted);
        Color(ImGuiCol.TitleBg, TitleBg);
        Color(ImGuiCol.TitleBgActive, TitleBgOn);
        Color(ImGuiCol.TitleBgCollapsed, TitleBg);

        Color(ImGuiCol.FrameBg, Field);
        Color(ImGuiCol.FrameBgHovered, Field with { W = Field.W + 0.06f });
        Color(ImGuiCol.FrameBgActive, Field with { W = Field.W + 0.10f });

        Color(ImGuiCol.Button, Pink);
        Color(ImGuiCol.ButtonHovered, ButtonHover);
        Color(ImGuiCol.ButtonActive, ButtonPressed);

        Color(ImGuiCol.Header, Glass);
        Color(ImGuiCol.HeaderHovered, GlassHover);
        Color(ImGuiCol.HeaderActive, GlassHover);

        Color(ImGuiCol.Tab, Glass);
        // Dark mode: translucent pink tabs so the light tab text stays readable (5.2:1).
        Color(ImGuiCol.TabHovered, Dark ? Pink with { W = 0.25f } : PinkDark);
        Color(ImGuiCol.TabActive, Dark ? Pink with { W = 0.38f } : Pink);
        Color(ImGuiCol.TabUnfocused, Glass);
        Color(ImGuiCol.TabUnfocusedActive, Dark ? Pink with { W = 0.28f } : Pink with { W = 0.7f });

        Color(ImGuiCol.CheckMark, Pink);
        Color(ImGuiCol.SliderGrab, Pink);
        Color(ImGuiCol.SliderGrabActive, PinkDark);
        Color(ImGuiCol.Separator, Border);
        Color(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0));
        Color(ImGuiCol.ScrollbarGrab, new Vector4(1, 1, 1, 0.20f));
        Color(ImGuiCol.ScrollbarGrabHovered, Pink with { W = 0.7f });
        Color(ImGuiCol.ScrollbarGrabActive, Pink);
        Color(ImGuiCol.ResizeGrip, Pink with { W = 0.3f });
        Color(ImGuiCol.ResizeGripHovered, Pink with { W = 0.7f });
        Color(ImGuiCol.ResizeGripActive, Pink);

        Var(ImGuiStyleVar.WindowRounding, S(16));
        Var(ImGuiStyleVar.WindowBorderSize, 1f);
        Var(ImGuiStyleVar.WindowPadding, new Vector2(S(16), S(14)));
        Var(ImGuiStyleVar.FrameRounding, S(8));
        Var(ImGuiStyleVar.FrameBorderSize, 1f);
        Var(ImGuiStyleVar.FramePadding, new Vector2(S(10), S(5)));
        Var(ImGuiStyleVar.ItemSpacing, new Vector2(S(8), S(7)));
        Var(ImGuiStyleVar.TabRounding, S(8));
        Var(ImGuiStyleVar.GrabRounding, S(8));
        Var(ImGuiStyleVar.ScrollbarRounding, S(8));
        Var(ImGuiStyleVar.PopupRounding, S(8));
    }

    public static void Pop()
    {
        ImGui.PopStyleVar(varCount);
        ImGui.PopStyleColor(colorCount);
        varCount = 0;
        colorCount = 0;
    }

    private static void Color(ImGuiCol col, Vector4 value)
    {
        ImGui.PushStyleColor(col, value);
        colorCount++;
    }

    private static void Var(ImGuiStyleVar v, float value)
    {
        ImGui.PushStyleVar(v, value);
        varCount++;
    }

    private static void Var(ImGuiStyleVar v, Vector2 value)
    {
        ImGui.PushStyleVar(v, value);
        varCount++;
    }

    // ------------------------------------------------------------ widgets

    public const string KofiUrl = "https://ko-fi.com/willocinnamon";

    /// <summary>
    /// Glass button with an icon and a label (used for Ko-fi / GitHub). The border turns pink on hover;
    /// <paramref name="rainbow"/> makes the label's letters cycle through soft rainbow colours.
    /// </summary>
    public static bool LinkButton(string id, FontAwesomeIcon icon, string label, bool rainbow = false, float width = 0)
    {
        var h = ImGui.GetFrameHeight() + S(4);
        var gap = S(7);
        rainbow &= !UseDalamud;
        string iconStr = icon.ToIconString();
        Vector4 iconInk;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            iconInk = InkBounds(iconStr);
        var iconW = iconInk.Z - iconInk.X;
        var labelW = ImGui.CalcTextSize(label).X;
        var w = width > 0 ? width : iconW + gap + labelW + S(26);

        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(w, h));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + new Vector2(w, h), ImGui.GetColorU32(active ? GlassHover with { W = GlassHover.W + 0.06f } : hovered ? GlassHover : Glass), R(10));
        dl.AddRect(pos, pos + new Vector2(w, h), ImGui.GetColorU32(hovered ? (UseDalamud ? Text : Pink) : Border), R(10), ImDrawFlags.None, hovered ? S(1.6f) : S(1.2f));

        var startX = pos.X + (w - (iconW + gap + labelW)) / 2f;
        var cy = pos.Y + h / 2f;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            dl.AddText(new Vector2(startX - iconInk.X, cy - (iconInk.Y + iconInk.W) / 2f), ImGui.GetColorU32(rainbow ? Pink : Text), iconStr);

        var ink = InkBounds(label);
        var tx = startX + iconW + gap;
        var ty = cy - (ink.Y + ink.W) / 2f;
        if (rainbow)
        {
            var t = (float)ImGui.GetTime();
            for (var i = 0; i < label.Length; i++)
            {
                var ch = label[i].ToString();
                var hue = ((t * 0.4f - i * 0.12f) % 1f + 1f) % 1f;
                dl.AddText(new Vector2(tx, ty), ImGui.GetColorU32(Hsv(hue, Dark ? 0.55f : 0.65f, 1f)), ch);
                tx += ImGui.CalcTextSize(ch).X;
            }
        }
        else
        {
            dl.AddText(new Vector2(tx, ty), ImGui.GetColorU32(Text), label);
        }

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    /// <summary>Big status badge with an icon, e.g. "Muted" / "Unmuted". Filled pink when <paramref name="on"/>.</summary>
    public static void StatusBadge(FontAwesomeIcon icon, string label, bool on, float height)
    {
        var gap = S(8);
        string iconStr = icon.ToIconString();
        Vector4 iconInk;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            iconInk = InkBounds(iconStr);
        var iconW = iconInk.Z - iconInk.X;
        var labelW = ImGui.CalcTextSize(label).X;
        var w = iconW + gap + labelW + S(32);

        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var fill = on ? Pink : Glass;
        var textCol = on ? OnPink : Muted;
        var round = UseDalamud ? R(0) : height / 2f;
        dl.AddRectFilled(pos, pos + new Vector2(w, height), ImGui.GetColorU32(fill), round);
        dl.AddRect(pos, pos + new Vector2(w, height), ImGui.GetColorU32(on ? Outline : Border), round, ImDrawFlags.None, S(1.5f));

        var startX = pos.X + (w - (iconW + gap + labelW)) / 2f;
        var cy = pos.Y + height / 2f;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            dl.AddText(new Vector2(startX - iconInk.X, cy - (iconInk.Y + iconInk.W) / 2f), ImGui.GetColorU32(textCol), iconStr);
        var ink = InkBounds(label);
        dl.AddText(new Vector2(startX + iconW + gap, cy - (ink.Y + ink.W) / 2f), ImGui.GetColorU32(textCol), label);
        ImGui.Dummy(new Vector2(w, height));
    }

    public static float StatusBadgeWidth(FontAwesomeIcon icon, string label)
    {
        float iconW;
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            var ink = InkBounds(icon.ToIconString());
            iconW = ink.Z - ink.X;
        }
        return iconW + S(8) + ImGui.CalcTextSize(label).X + S(32);
    }

    private static Vector4 Hsv(float h, float sat, float v)
    {
        var k = h * 6f;
        var i = (int)MathF.Floor(k) % 6;
        var f = k - MathF.Floor(k);
        var p = v * (1f - sat);
        var q = v * (1f - f * sat);
        var u = v * (1f - (1f - f) * sat);
        return i switch
        {
            0 => new Vector4(v, u, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, u, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(u, p, v, 1f),
            _ => new Vector4(v, p, q, 1f),
        };
    }

    /// <summary>Pink button with readable text in both themes.</summary>
    public static bool Button(string label, Vector2 size = default)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, OnPink);
        var clicked = ImGui.Button(label, size);
        ImGui.PopStyleColor();
        return clicked;
    }

    /// <summary>Soft pink → purple colour that slowly cycles, like Will-o-Bars' active cards.</summary>
    public static Vector4 Glow(float offset = 0f)
    {
        var t = (MathF.Sin((float)ImGui.GetTime() * 1.6f + offset) + 1f) / 2f;
        return Vector4.Lerp(Rgb(255, 126, 182), Rgb(176, 128, 255), t);
    }

    public enum CheckLook
    {
        /// <summary>Pink, ready to click.</summary>
        Normal,
        /// <summary>Faded pink with a spinning circle; can't be clicked.</summary>
        Busy,
        /// <summary>Grey: the check found nothing.</summary>
        Nothing,
        /// <summary>Pink with an animated pink/purple border: the check found updates.</summary>
        Found,
    }

    /// <summary>Width of a <see cref="CheckButton"/> with this label (and an icon or spinner in front of it).</summary>
    public static float CheckButtonWidth(string label, bool withIcon)
        => ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2 + (withIcon ? ImGui.GetFontSize() + S(6) : 0);

    /// <summary>The "Check for updates" button in its four looks.</summary>
    public static bool CheckButton(string id, string label, CheckLook look, FontAwesomeIcon? icon = null)
    {
        var busy = look == CheckLook.Busy;
        var withIcon = busy || icon != null;
        var size = new Vector2(CheckButtonWidth(label, withIcon), ImGui.GetFrameHeight());
        var pos = ImGui.GetCursorScreenPos();

        using var disabled = ImRaii.Disabled(busy);
        var clicked = ImGui.InvisibleButton(id, size) && !busy;
        var hovered = !busy && ImGui.IsItemHovered();
        var active = !busy && ImGui.IsItemActive();

        var dl = ImGui.GetWindowDrawList();
        var round = ImGui.GetStyle().FrameRounding;
        uint fill, text;
        if (look == CheckLook.Nothing)
        {
            fill = ImGui.GetColorU32(active ? GlassHover with { W = GlassHover.W + 0.06f } : hovered ? GlassHover : Field);
            text = ImGui.GetColorU32(Text);
        }
        else
        {
            fill = ImGui.GetColorU32(active ? ImGuiCol.ButtonActive : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button);
            text = ImGui.GetColorU32(OnPink);
        }
        dl.AddRectFilled(pos, pos + size, fill, round);

        if (look == CheckLook.Nothing)
        {
            dl.AddRect(pos, pos + size, ImGui.GetColorU32(hovered ? Text : Border), round, ImDrawFlags.None, S(1.2f));
        }
        else if (look == CheckLook.Found)
        {
            // Same animated border as Will-o-Bars' active cards: a faint wide halo plus a crisp line.
            var c = Glow();
            dl.AddRect(pos - new Vector2(S(1.5f)), pos + size + new Vector2(S(1.5f)), ImGui.GetColorU32(c with { W = 0.35f }), round + S(1.5f), ImDrawFlags.None, S(3f));
            dl.AddRect(pos, pos + size, ImGui.GetColorU32(c), round, ImDrawFlags.None, S(1.8f));
        }

        var x = pos.X + ImGui.GetStyle().FramePadding.X;
        var cy = pos.Y + size.Y / 2f;
        var slot = ImGui.GetFontSize();
        if (busy)
        {
            // Spinning circle: a faint ring with a bright arc going round.
            var center = new Vector2(x + slot / 2f, cy);
            var r = slot * 0.38f;
            dl.AddCircle(center, r, ImGui.GetColorU32(OnPink with { W = OnPink.W * 0.3f }), 24, S(2f));
            var a = (float)ImGui.GetTime() * 6f;
            dl.PathArcTo(center, r, a, a + MathF.PI * 1.3f, 24);
            dl.PathStroke(text, ImDrawFlags.None, S(2f));
        }
        else if (icon is { } ic)
        {
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                var s = ic.ToIconString();
                var ink = InkBounds(s);
                var inkCenter = new Vector2((ink.X + ink.Z) / 2f, (ink.Y + ink.W) / 2f);
                dl.AddText(new Vector2(x + slot / 2f, cy) - inkCenter, text, s);
            }
        }
        if (withIcon)
            x += slot + S(6);
        dl.AddText(new Vector2(x, cy - ImGui.GetTextLineHeight() / 2f), text, label);

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    /// <summary>
    /// Small pill button. Not ImGui's own SmallButton: that has no room above and below the text, so letters
    /// like "p" and "y" touched the rounded border.
    /// </summary>
    public static bool SmallButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, OnPink);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(S(9), S(2)));
        var clicked = ImGui.Button(label);
        ImGui.PopStyleVar();
        ImGui.PopStyleColor();
        return clicked;
    }

    /// <summary>Thin rounded pink bar, like the one at the top of the site's containers.</summary>
    public static void AccentBar(float widthFraction = 0.85f)
    {
        var avail = ImGui.GetContentRegionAvail().X;
        var width = avail * widthFraction;
        var pos = ImGui.GetCursorScreenPos() + new Vector2((avail - width) / 2f, 0);
        var h = S(4);
        ImGui.GetWindowDrawList().AddRectFilled(pos, pos + new Vector2(width, h), ImGui.GetColorU32(Pink), h);
        ImGui.Dummy(new Vector2(avail, h));
    }

    /// <summary>
    /// Centred heading made of white and pink pieces, e.g. "Will-" + pink "o" + "-Patchwisp", drawn with a real
    /// large font (no blurry scaling). Falls back to the normal font when the window is too narrow.
    /// <paramref name="reserveRight"/> keeps room free on the right (for the light/dark button).
    /// </summary>
    public static void Heading(IFontHandle? font, float reserveRight, params (string Text, bool Pink)[] parts)
    {
        var all = string.Concat(parts.Select(p => p.Text));
        var avail = ImGui.GetContentRegionAvail().X;

        IDisposable? pushed = null;
        if (font is { Available: true })
        {
            pushed = font.Push();
            if (ImGui.CalcTextSize(all).X > avail - reserveRight * 2)
            {
                pushed.Dispose();
                pushed = null;
            }
        }

        var full = ImGui.CalcTextSize(all).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (avail - full) / 2f));
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, 0);

            if (parts[i].Pink)
                DanglingText(parts[i].Text, BrandPink);
            else
                ImGui.TextColored(Text, parts[i].Text);
        }

        pushed?.Dispose();
    }

    /// <summary>
    /// Text that swings gently like it's hanging from a point just above it, copied from the website's
    /// <c>.title-o</c> "dangle" animation: rotate -8deg -> 8deg -> -8deg over 4 seconds, ease-in-out,
    /// transform-origin 50% -8px. Done by rotating the glyph's vertices after ImGui has drawn them.
    /// </summary>
    public static void DanglingText(string text, Vector4 color)
    {
        var dl = ImGui.GetWindowDrawList();
        var firstVertex = dl.VtxBuffer.Size;

        ImGui.TextColored(color, text);

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var height = max.Y - min.Y;

        // Pivot: horizontally centred, a little above the letter (site: 8px above a 33px letter).
        var pivot = new Vector2((min.X + max.X) / 2f, min.Y - height * 0.2f);

        // Cosine = smooth ease-in-out between the -8deg and +8deg keyframes, 4 second cycle.
        var phase = ImGui.GetTime() % 4.0 / 4.0;
        var angle = (float)(-8.0 * Math.Cos(phase * 2.0 * Math.PI) * Math.PI / 180.0);
        var (sin, cos) = MathF.SinCos(angle);

        var lastVertex = dl.VtxBuffer.Size;
        for (var i = firstVertex; i < lastVertex; i++)
        {
            ref var v = ref dl.VtxBuffer.Ref(i);
            var d = v.Pos - pivot;
            v.Pos = pivot + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
        }
    }

    /// <summary>
    /// Round glass button with a Font Awesome icon in the middle (used for the sun/moon theme switch).
    /// Border turns pink on hover, like the cards.
    /// </summary>
    public static bool IconCircle(string id, FontAwesomeIcon icon, Vector4 iconColor, float diameter, bool pinkStroke = true)
    {
        var pos = ImGui.GetCursorScreenPos();
        var clicked = ImGui.InvisibleButton(id, new Vector2(diameter, diameter));
        var hovered = ImGui.IsItemHovered();
        var active = ImGui.IsItemActive();

        var dl = ImGui.GetWindowDrawList();
        var center = pos + new Vector2(diameter / 2f);
        var radius = diameter / 2f;
        var fill = ImGui.GetColorU32(active ? GlassHover with { W = GlassHover.W + 0.06f } : hovered ? GlassHover : Glass);
        if (UseDalamud)
        {
            // Dalamud theme: a small rounded square like Dalamud's own buttons.
            dl.AddRectFilled(pos, pos + new Vector2(diameter), fill, R(0));
            dl.AddRect(pos, pos + new Vector2(diameter), ImGui.GetColorU32(hovered ? Text : Border), R(0), ImDrawFlags.None, S(1.2f));
        }
        else
        {
            dl.AddCircleFilled(center, radius, fill, 32);
            // Thin pink outline (brighter and a touch thicker while hovered).
            var stroke = pinkStroke ? (hovered ? ButtonHover : Pink with { W = 0.85f }) : (hovered ? Pink : Border);
            dl.AddCircle(center, radius - 1f, ImGui.GetColorU32(stroke), 32, hovered ? S(2f) : S(1.5f));
        }

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            // Centre the icon's visible shape (icons have uneven side bearings, so the text box isn't centred).
            var text = icon.ToIconString();
            var ink = InkBounds(text);
            var inkCenter = new Vector2((ink.X + ink.Z) / 2f, (ink.Y + ink.W) / 2f);
            dl.AddText(center - inkCenter, ImGui.GetColorU32(iconColor), text);
        }

        if (hovered)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return clicked;
    }

    /// <summary>
    /// Icon-only theme button for the header: moon (Dark), sun (Light) or puzzle piece (Dalamud).
    /// Click it to pick a theme from a small menu. It never changes size, so the title doesn't move.
    /// Returns true when the theme was changed.
    /// </summary>
    public static bool ThemeButton(string id, ref ThemeMode mode, float diameter)
    {
        var (icon, color) = Look(mode);
        if (IconCircle(id, icon, color, diameter))
            ImGui.OpenPopup(id + "pop");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip($"Theme: {Name(mode)}");

        var changed = false;
        using var popup = ImRaii.Popup(id + "pop");
        if (!popup.Success)
            return false;

        foreach (var m in new[] { ThemeMode.Dark, ThemeMode.Light, ThemeMode.Dalamud })
        {
            var (mi, _) = Look(m);
            var start = ImGui.GetCursorScreenPos();
            var iconW = ImGui.GetTextLineHeight() + S(6);
            if (ImGui.Selectable($"##{id}{m}", m == mode, ImGuiSelectableFlags.None, new Vector2(ImGui.CalcTextSize("Dalamud").X + iconW + S(8), 0)))
            {
                changed = m != mode;
                mode = m;
            }

            var dl = ImGui.GetWindowDrawList();
            using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
            {
                var s = mi.ToIconString();
                var ink = InkBounds(s);
                var lineH = ImGui.GetItemRectSize().Y;
                dl.AddText(start + new Vector2(S(2) + (ImGui.GetTextLineHeight() - (ink.Z - ink.X)) / 2f - ink.X, (lineH - (ink.Y + ink.W)) / 2f), ImGui.GetColorU32(Text), s);
            }
            dl.AddText(start + new Vector2(iconW, 0), ImGui.GetColorU32(Text), Name(m));
        }

        return changed;

        static (FontAwesomeIcon, Vector4) Look(ThemeMode m) => m switch
        {
            ThemeMode.Light => (FontAwesomeIcon.Sun, Amber),
            ThemeMode.Dalamud => (FontAwesomeIcon.PuzzlePiece, Text),
            _ => (FontAwesomeIcon.Moon, Moon),
        };

        static string Name(ThemeMode m) => m switch
        {
            ThemeMode.Light => "Light",
            ThemeMode.Dalamud => "Dalamud",
            _ => "Dark",
        };
    }

    public const string BrandName = "Will-o-Patchwisp";

    /// <summary>
    /// Wrapped text where every "Will-o-Patchwisp" gets its pink "o", like the website's title.
    /// <paramref name="rightEdge"/> is the screen X to wrap at (defaults to the window's content edge).
    /// </summary>
    public static void BrandText(string text, Vector4 color, float? rightEdge = null)
    {
        // Build pieces; the brand's pieces are glued together so a line never breaks inside the name.
        var pieces = new List<(string Text, Vector4 Color, bool Glue)>();
        var i = 0;
        while (true)
        {
            var k = text.IndexOf(BrandName, i, StringComparison.Ordinal);
            if (k < 0)
            {
                AddWords(pieces, text[i..], color);
                break;
            }

            AddWords(pieces, text[i..k], color);
            pieces.Add(("Will-", color, false));
            pieces.Add(("o", BrandPink, true));
            // keep whatever follows the name (e.g. a space) attached to it
            var rest = Regex.Match(text[(k + BrandName.Length)..], @"^\s*").Value;
            pieces.Add(("-Patchwisp" + rest, color, true));
            i = k + BrandName.Length + rest.Length;
        }

        var right = rightEdge ?? ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        var first = true;
        for (var n = 0; n < pieces.Count; n++)
        {
            var (piece, col, glue) = pieces[n];
            if (!first)
            {
                // A piece and everything glued after it (the whole name) must fit, or it all moves to the next line.
                var together = piece;
                for (var m = n + 1; m < pieces.Count && pieces[m].Glue; m++)
                    together += pieces[m].Text;
                var x = ImGui.GetItemRectMax().X;
                if (glue || x + ImGui.CalcTextSize(together.TrimEnd()).X <= right)
                    ImGui.SameLine(0, 0);
            }

            ImGui.TextColored(col, piece);
            first = false;
        }
    }

    private static void AddWords(List<(string, Vector4, bool)> pieces, string text, Vector4 color)
    {
        foreach (Match m in Regex.Matches(text, @"\S+\s*|\s+"))
            pieces.Add((m.Value, color, false));
    }

    /// <summary>Coloured text that wraps onto new lines at the window/card edge.</summary>
    public static void Wrapped(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    /// <summary>Centred text; falls back to wrapped text when it doesn't fit on one line.</summary>
    public static void CenteredText(string text, Vector4 color)
    {
        var w = ImGui.CalcTextSize(text).X;
        if (w > ImGui.GetContentRegionAvail().X)
        {
            Wrapped(text, color);
            return;
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0, (ImGui.GetContentRegionAvail().X - w) / 2f));
        ImGui.TextColored(color, text);
    }

    /// <summary>Small filled pill with a white outline, like the site's "Show" badge.</summary>
    public static void Pill(string text, Vector4 fill)
    {
        var pad = new Vector2(S(7), S(1));
        var textSize = ImGui.CalcTextSize(text);
        var size = textSize + pad * 2;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        dl.AddRectFilled(pos, pos + size, ImGui.GetColorU32(fill), R(8));
        dl.AddRect(pos, pos + size, ImGui.GetColorU32(Outline), R(8));

        // Centre the visible letters, not the font's line box (which has extra room below for descenders).
        var textPos = new Vector2(pos.X + (size.X - textSize.X) / 2f, pos.Y + PillTextOffset(text));
        dl.AddText(textPos, ImGui.GetColorU32(TextOn(fill)), text);
        ImGui.Dummy(size);
    }

    /// <summary>
    /// Visible (ink) bounds of a string in the current font, relative to where ImGui would draw it:
    /// (minX, minY, maxX, maxY). Used to centre text and icons exactly.
    /// </summary>
    public static unsafe Vector4 InkBounds(string text)
    {
        var font = ImGui.GetFont();
        var scale = ImGui.GetFontSize() / font.FontSize;
        float x = 0, minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var ch in text)
        {
            var g = font.FindGlyph(ch);
            if (g == null)
                continue;
            if (g->X1 > g->X0 && g->Y1 > g->Y0)
            {
                minX = Math.Min(minX, x + g->X0 * scale);
                maxX = Math.Max(maxX, x + g->X1 * scale);
                minY = Math.Min(minY, g->Y0 * scale);
                maxY = Math.Max(maxY, g->Y1 * scale);
            }
            x += g->AdvanceX * scale;
        }

        if (minX == float.MaxValue)
        {
            var sz = ImGui.CalcTextSize(text);
            return new Vector4(0, 0, sz.X, sz.Y);
        }
        return new Vector4(minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Readable text colour on a filled badge. Pink follows the theme (white like the site in light mode,
    /// dark plum in dark mode); other colours get whichever of dark/white has the higher contrast.
    /// </summary>
    public static Vector4 TextOn(Vector4 fill)
    {
        if (UseDalamud)
            return D(ImGuiCol.Text);
        if (fill.X == BrandPink.X && fill.Y == BrandPink.Y && fill.Z == BrandPink.Z)
            return OnPink;

        static float Lin(float c) => c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        var l = 0.2126f * Lin(fill.X) + 0.7152f * Lin(fill.Y) + 0.0722f * Lin(fill.Z);
        var vsWhite = 1.05f / (l + 0.05f);
        var vsDark = (l + 0.05f) / 0.0645f; // dark plum (38, 30, 42)
        return vsDark > vsWhite ? Rgb(38, 30, 42) : White;
    }

    /// <summary>
    /// How far below the top of a <see cref="Pill"/> its text is drawn. Put normal text next to a pill at this
    /// height and both sit on the same line.
    /// </summary>
    public static float PillTextOffset(string text)
    {
        var height = ImGui.CalcTextSize(text).Y + S(2);
        var ink = InkBounds(text);
        return height / 2f - (ink.Y + ink.W) / 2f;
    }

    public static float PillWidth(string text) => ImGui.CalcTextSize(text).X + S(14);

    /// <summary>
    /// Draws a glass card (rounded translucent box with a soft border) behind whatever <paramref name="body"/> draws.
    /// The border turns pink while hovered, like the cards on the site.
    /// </summary>
    /// <summary>Left/right screen X of the current card's inner area (valid while a card body is drawing).</summary>
    public static float CardInnerLeft { get; private set; }
    public static float CardInnerRight { get; private set; }
    public static float CardInnerWidth => CardInnerRight - CardInnerLeft;

    public static void Card(string id, Action body)
    {
        var dl = ImGui.GetWindowDrawList();
        var pad = new Vector2(S(12), S(9));
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.SetCursorScreenPos(start + pad);
        CardInnerLeft = start.X + pad.X;
        CardInnerRight = start.X + width - pad.X;
        ImGui.PushID(id);
        ImGui.BeginGroup();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + width - pad.X * 2);
        ImGui.PushItemWidth(width - pad.X * 2);
        body();
        ImGui.PopItemWidth();
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();
        ImGui.PopID();

        var end = new Vector2(start.X + width, ImGui.GetItemRectMax().Y + pad.Y);
        var hovered = ImGui.IsMouseHoveringRect(start, end);

        dl.ChannelsSetCurrent(0);
        dl.AddRectFilled(start, end, ImGui.GetColorU32(hovered && !UseDalamud ? GlassHover : Glass), R(16));
        dl.AddRect(start, end, ImGui.GetColorU32(hovered && !UseDalamud ? Pink : Border), R(16));
        dl.ChannelsMerge();

        ImGui.SetCursorScreenPos(new Vector2(start.X, end.Y));
        ImGui.Dummy(new Vector2(width, S(8)));
    }
}
