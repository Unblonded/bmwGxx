using System;
using System.Globalization;
using System.Numerics;
using ImGuiNET;

namespace GDiag.UI
{
    // small helpers so the tab code reads like a description of the screen
    public static class Widgets
    {
        // screen scale picked at startup (1.0 on a 1080p screen at 100%, about 2.0 on 4K), see Program.DetectUiScale
        public static float UiScale = 1f;

        // converts a size designed for 1080p into pixels for this screen and the chosen text size.
        // 0 and -1 keep their special ImGui meanings (auto / fill) and are not scaled.
        public static float S(float designPixels)
        {
            if (Math.Abs(designPixels) <= 1)
            {
                return designPixels;
            }
            return designPixels * UiScale * (AppSettings.Current.FontSize / 18f);
        }

        // a "(?)" that explains jargon on hover
        public static void Help(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(Icons.Info);
            Tooltip(text);
        }

        public static void Tooltip(string text)
        {
            if (!string.IsNullOrEmpty(text) && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled | ImGuiHoveredFlags.DelayShort))
            {
                ImGui.BeginTooltip();
                ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30);
                ImGui.TextUnformatted(text);
                ImGui.PopTextWrapPos();
                ImGui.EndTooltip();
            }
        }

        // tinted button: soft colored fill with colored text, the main actions stand out without shouting
        public static bool ColoredButton(string label, Vector4 color, Vector2 size = default, bool enabled = true)
        {
            ImGui.BeginDisabled(!enabled);
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.WithAlpha(color, 0.16f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.WithAlpha(color, 0.28f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.WithAlpha(color, 0.40f));
            ImGui.PushStyleColor(ImGuiCol.Text, Vector4.Lerp(color, Vector4.One, 0.15f));
            Fonts.Push(Fonts.Semibold);
            bool clicked = ImGui.Button(label, size);
            Fonts.Pop();
            ImGui.PopStyleColor(4);
            ImGui.EndDisabled();
            return clicked;
        }

        // solid button for the one thing the screen wants you to do next
        public static bool PrimaryButton(string label, Vector4 color, Vector2 size = default, bool enabled = true)
        {
            ImGui.BeginDisabled(!enabled);
            ImGui.PushStyleColor(ImGuiCol.Button, color * new Vector4(0.85f, 0.85f, 0.85f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, color);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Vector4.Lerp(color, Vector4.One, 0.2f));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Bg);
            Fonts.Push(Fonts.Semibold);
            bool clicked = ImGui.Button(label, size);
            Fonts.Pop();
            ImGui.PopStyleColor(4);
            ImGui.EndDisabled();
            return clicked;
        }

        public static bool Button(string label, bool enabled, string disabledReason = null, Vector2 size = default)
        {
            ImGui.BeginDisabled(!enabled);
            bool clicked = ImGui.Button(label, size);
            ImGui.EndDisabled();
            if (!enabled)
            {
                Tooltip(disabledReason);
            }
            return clicked;
        }

        public static void Heading(string text)
        {
            Fonts.Push(Fonts.Semibold);
            ImGui.TextUnformatted(text);
            Fonts.Pop();
        }

        // icon in the accent color followed by a semibold title
        public static void SectionTitle(string icon, string text)
        {
            Fonts.Push(Fonts.Semibold);
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
            ImGui.TextUnformatted(icon);
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextUnformatted(text);
            Fonts.Pop();
        }

        public static void BigText(string text, float scale = 1.35f)
        {
            Fonts.Push(Fonts.Title);
            ImGui.SetWindowFontScale(scale / 1.55f);
            ImGui.TextWrapped(text);
            ImGui.SetWindowFontScale(1f);
            Fonts.Pop();
        }

        public static void Colored(Vector4 color, string text)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextWrapped(text);
            ImGui.PopStyleColor();
        }

        public static void Dim(string text)
        {
            Colored(Theme.Dim, text);
        }

        // a filled circle used as a status light, with a soft glow
        public static void StatusDot(Vector4 color, bool pulse = false)
        {
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            float h = ImGui.GetTextLineHeight();
            var center = pos + new Vector2(h * 0.5f, h * 0.55f);
            float glow = pulse ? 0.5f + 0.5f * MathF.Sin((float)ImGui.GetTime() * 4f) : 0.6f;
            draw.AddCircleFilled(center, h * 0.42f, Theme.U32(Theme.WithAlpha(color, 0.18f * glow)));
            draw.AddCircleFilled(center, h * 0.24f, Theme.U32(color));
            ImGui.Dummy(new Vector2(h, h));
            ImGui.SameLine();
        }

        // small rounded label, e.g. a status next to a reading
        public static void Pill(string text, Vector4 color)
        {
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var textSize = ImGui.CalcTextSize(text);
            var pad = new Vector2(S(8), S(2));
            var size = textSize + pad * 2;
            draw.AddRectFilled(pos, pos + size, Theme.U32(Theme.WithAlpha(color, 0.16f)), size.Y * 0.5f);
            draw.AddText(pos + pad, Theme.U32(color), text);
            ImGui.Dummy(size);
        }

        // tinted circle with an icon in it, used on stat cards
        public static void IconBadge(string icon, Vector4 color, float diameter)
        {
            var draw = ImGui.GetWindowDrawList();
            var pos = ImGui.GetCursorScreenPos();
            var center = pos + new Vector2(diameter * 0.5f);
            draw.AddCircleFilled(center, diameter * 0.5f, Theme.U32(Theme.WithAlpha(color, 0.14f)));
            var size = ImGui.CalcTextSize(icon);
            draw.AddText(center - size * 0.5f, Theme.U32(color), icon);
            ImGui.Dummy(new Vector2(diameter));
        }

        // raised card; returns the child's begin result like BeginChild
        public static void BeginCard(string id, Vector2 size, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
        {
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.Card);
            ImGui.PushStyleColor(ImGuiCol.Border, Theme.Border);
            // cards never scroll themselves, so the mouse wheel scrolls the page even when hovering one
            ImGui.BeginChild(id, size, ImGuiChildFlags.Border, flags | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        }

        // accentColor tints the border while hovered, null keeps it plain
        public static void EndCard(Vector4? accentColor = null)
        {
            bool hovered = ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows);
            ImGui.EndChild();
            ImGui.PopStyleColor(2);
            if (accentColor is Vector4 accent && hovered)
            {
                // drawn by the parent so it is clipped to the parent's visible area
                var min = ImGui.GetItemRectMin();
                var max = ImGui.GetItemRectMax();
                ImGui.GetWindowDrawList().AddRect(min, max, Theme.U32(Theme.WithAlpha(accent, 0.55f)), ImGui.GetStyle().ChildRounding, ImDrawFlags.None, S(1.5f));
            }
        }

        // 270 degree gauge like a car's instrument cluster. fraction 0..1 (null = no value yet),
        // zones are (from, to, color) in 0..1 drawn as a thin outer ring
        public static void RadialGauge(Vector2 center, float radius, double? fraction, Vector4 color, (float from, float to, Vector4 color)[] zones)
        {
            var draw = ImGui.GetWindowDrawList();
            const float start = MathF.PI * 0.75f;
            const float sweep = MathF.PI * 1.5f;
            float thickness = radius * 0.14f;

            // track
            draw.PathArcTo(center, radius, start, start + sweep, 64);
            draw.PathStroke(Theme.U32(Theme.Track), ImDrawFlags.None, thickness);

            // colored zones just outside the track
            if (zones != null)
            {
                foreach (var (from, to, zoneColor) in zones)
                {
                    draw.PathArcTo(center, radius + thickness * 0.95f, start + sweep * from, start + sweep * to, 32);
                    draw.PathStroke(Theme.U32(Theme.WithAlpha(zoneColor, 0.55f)), ImDrawFlags.None, thickness * 0.22f);
                }
            }

            // tick marks
            for (int i = 0; i <= 10; i++)
            {
                float a = start + sweep * i / 10f;
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                float inner = radius - thickness * (i % 5 == 0 ? 1.45f : 1.05f);
                float outer = radius - thickness * 0.7f;
                draw.AddLine(center + dir * inner, center + dir * outer, Theme.U32(Theme.WithAlpha(Theme.Dim, i % 5 == 0 ? 0.6f : 0.3f)), S(1.5f));
            }

            if (fraction is double f)
            {
                float end = start + sweep * (float)Math.Clamp(f, 0, 1);
                // glow: wide faint strokes under the value arc
                for (int g = 3; g >= 1; g--)
                {
                    draw.PathArcTo(center, radius, start, end, 64);
                    draw.PathStroke(Theme.U32(Theme.WithAlpha(color, 0.07f)), ImDrawFlags.None, thickness * (1f + g * 0.55f));
                }
                draw.PathArcTo(center, radius, start, end, 64);
                draw.PathStroke(Theme.U32(color), ImDrawFlags.None, thickness);
                var tip = center + new Vector2(MathF.Cos(end), MathF.Sin(end)) * radius;
                draw.AddCircleFilled(tip, thickness * 0.75f, Theme.U32(Vector4.Lerp(color, Vector4.One, 0.6f)));
                draw.AddCircleFilled(center + new Vector2(MathF.Cos(start), MathF.Sin(start)) * radius, thickness * 0.5f, Theme.U32(color));
            }
        }

        // filled line chart of recent values; values is a ring buffer starting at offset
        public static void Sparkline(string id, float[] values, int count, int offset, float min, float max, Vector4 color, Vector2 size)
        {
            var pos = ImGui.GetCursorScreenPos();
            if (size.X <= 0)
            {
                size.X = ImGui.GetContentRegionAvail().X;
            }
            ImGui.Dummy(size);
            if (count < 2)
            {
                return;
            }
            var draw = ImGui.GetWindowDrawList();
            float range = Math.Max(1e-3f, max - min);
            var points = new Vector2[count];
            for (int i = 0; i < count; i++)
            {
                float v = values[(offset + i) % values.Length];
                float x = pos.X + size.X * i / (count - 1);
                float y = pos.Y + size.Y - (v - min) / range * size.Y;
                points[i] = new Vector2(x, y);
            }
            uint fillTop = Theme.U32(Theme.WithAlpha(color, 0.22f));
            uint fillBottom = Theme.U32(Theme.WithAlpha(color, 0.0f));
            float bottom = pos.Y + size.Y;
            for (int i = 0; i < count - 1; i++)
            {
                var a = points[i];
                var b = points[i + 1];
                // each slice is a quad from the line down to the bottom, faded toward the bottom
                draw.PrimReserve(6, 4);
                uint idx = (uint)draw._VtxCurrentIdx;
                var uv = ImGui.GetFontTexUvWhitePixel();
                draw.PrimWriteVtx(a, uv, fillTop);
                draw.PrimWriteVtx(b, uv, fillTop);
                draw.PrimWriteVtx(new Vector2(b.X, bottom), uv, fillBottom);
                draw.PrimWriteVtx(new Vector2(a.X, bottom), uv, fillBottom);
                draw.PrimWriteIdx((ushort)idx);
                draw.PrimWriteIdx((ushort)(idx + 1));
                draw.PrimWriteIdx((ushort)(idx + 2));
                draw.PrimWriteIdx((ushort)idx);
                draw.PrimWriteIdx((ushort)(idx + 2));
                draw.PrimWriteIdx((ushort)(idx + 3));
            }
            unsafe
            {
                fixed (Vector2* p = points)
                {
                    ImGuiNative.ImDrawList_AddPolyline(draw.NativePtr, p, count, Theme.U32(color), ImDrawFlags.None, S(2f));
                }
            }
            draw.AddCircleFilled(points[count - 1], S(3.5f), Theme.U32(color));
        }

        public static bool TryParseHex(string text, out uint value)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }
}
