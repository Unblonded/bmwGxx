using System.Numerics;
using ImGuiNET;

namespace GDiag.UI
{
    // Dark "instrument cluster" look: near-black blue background, raised cards, cyan accent,
    // and bright status colors that read at a glance.
    public static class Theme
    {
        public static readonly Vector4 Accent = Rgb(0x2D, 0xD4, 0xFF);
        public static readonly Vector4 Accent2 = Rgb(0x8B, 0x5C, 0xF6);
        public static readonly Vector4 Good = Rgb(0x34, 0xD3, 0x99);
        public static readonly Vector4 Warn = Rgb(0xFB, 0xBF, 0x24);
        public static readonly Vector4 Bad = Rgb(0xFF, 0x5C, 0x6C);
        public static readonly Vector4 Dim = Rgb(0x83, 0x8D, 0xA3);
        public static readonly Vector4 Modified = Rgb(0xFF, 0x8C, 0x3A);

        public static readonly Vector4 Bg = Rgb(0x09, 0x0B, 0x10);
        public static readonly Vector4 Panel = Rgb(0x0F, 0x13, 0x1B);
        public static readonly Vector4 Card = Rgb(0x14, 0x19, 0x23);
        public static readonly Vector4 CardHover = Rgb(0x19, 0x20, 0x2D);
        public static readonly Vector4 Border = Rgb(0x22, 0x2A, 0x39);
        public static readonly Vector4 Track = Rgb(0x1E, 0x25, 0x33);
        public static readonly Vector4 Text = Rgb(0xE8, 0xEC, 0xF4);

        static Vector4 Rgb(int r, int g, int b, float a = 1f) => new Vector4(r / 255f, g / 255f, b / 255f, a);

        public static Vector4 WithAlpha(Vector4 c, float a) => new Vector4(c.X, c.Y, c.Z, a);

        public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

        public static void Apply()
        {
            ImGui.StyleColorsDark();
            var style = ImGui.GetStyle();
            style.WindowRounding = 0;
            style.ChildRounding = 12;
            style.FrameRounding = 8;
            style.PopupRounding = 10;
            style.GrabRounding = 8;
            style.TabRounding = 8;
            style.ScrollbarRounding = 8;
            style.WindowBorderSize = 0;
            style.ChildBorderSize = 1;
            style.PopupBorderSize = 1;
            style.FrameBorderSize = 0;
            style.TabBorderSize = 0;
            style.TabBarBorderSize = 1;
            style.FramePadding = new Vector2(12, 7);
            style.ItemSpacing = new Vector2(10, 8);
            style.ItemInnerSpacing = new Vector2(8, 6);
            style.CellPadding = new Vector2(8, 6);
            style.WindowPadding = new Vector2(16, 14);
            style.ScrollbarSize = 10;
            style.GrabMinSize = 12;
            style.SelectableTextAlign = new Vector2(0, 0.5f);

            // spacing, rounding and scrollbars grow with the screen like the font does
            style.ScaleAllSizes(Widgets.UiScale);

            var c = style.Colors;
            c[(int)ImGuiCol.Text] = Text;
            c[(int)ImGuiCol.TextDisabled] = Dim;
            c[(int)ImGuiCol.WindowBg] = Bg;
            c[(int)ImGuiCol.ChildBg] = Panel;
            c[(int)ImGuiCol.PopupBg] = WithAlpha(Card, 0.98f);
            c[(int)ImGuiCol.Border] = Border;
            c[(int)ImGuiCol.BorderShadow] = Vector4.Zero;
            c[(int)ImGuiCol.FrameBg] = Rgb(0x1A, 0x20, 0x2C);
            c[(int)ImGuiCol.FrameBgHovered] = Rgb(0x21, 0x29, 0x38);
            c[(int)ImGuiCol.FrameBgActive] = Rgb(0x27, 0x31, 0x43);
            c[(int)ImGuiCol.TitleBg] = Panel;
            c[(int)ImGuiCol.TitleBgActive] = Panel;
            c[(int)ImGuiCol.MenuBarBg] = Panel;
            c[(int)ImGuiCol.ScrollbarBg] = Vector4.Zero;
            c[(int)ImGuiCol.ScrollbarGrab] = Rgb(0x2A, 0x33, 0x45);
            c[(int)ImGuiCol.ScrollbarGrabHovered] = Rgb(0x36, 0x41, 0x57);
            c[(int)ImGuiCol.ScrollbarGrabActive] = Accent;
            c[(int)ImGuiCol.CheckMark] = Accent;
            c[(int)ImGuiCol.SliderGrab] = Accent;
            c[(int)ImGuiCol.SliderGrabActive] = Text;
            c[(int)ImGuiCol.Button] = Rgb(0x1C, 0x23, 0x31);
            c[(int)ImGuiCol.ButtonHovered] = Rgb(0x25, 0x2F, 0x42);
            c[(int)ImGuiCol.ButtonActive] = WithAlpha(Accent, 0.55f);
            c[(int)ImGuiCol.Header] = WithAlpha(Accent, 0.16f);
            c[(int)ImGuiCol.HeaderHovered] = WithAlpha(Accent, 0.24f);
            c[(int)ImGuiCol.HeaderActive] = WithAlpha(Accent, 0.34f);
            c[(int)ImGuiCol.Separator] = Border;
            c[(int)ImGuiCol.SeparatorHovered] = WithAlpha(Accent, 0.6f);
            c[(int)ImGuiCol.SeparatorActive] = Accent;
            c[(int)ImGuiCol.ResizeGrip] = Vector4.Zero;
            c[(int)ImGuiCol.ResizeGripHovered] = WithAlpha(Accent, 0.5f);
            c[(int)ImGuiCol.ResizeGripActive] = Accent;
            c[(int)ImGuiCol.Tab] = Vector4.Zero;
            c[(int)ImGuiCol.TabHovered] = WithAlpha(Accent, 0.18f);
            c[(int)ImGuiCol.TabActive] = WithAlpha(Accent, 0.20f);
            c[(int)ImGuiCol.TabUnfocused] = Vector4.Zero;
            c[(int)ImGuiCol.TabUnfocusedActive] = WithAlpha(Accent, 0.14f);
            c[(int)ImGuiCol.PlotLines] = Accent;
            c[(int)ImGuiCol.PlotHistogram] = Accent;
            c[(int)ImGuiCol.TableHeaderBg] = Rgb(0x17, 0x1D, 0x29);
            c[(int)ImGuiCol.TableBorderStrong] = Border;
            c[(int)ImGuiCol.TableBorderLight] = WithAlpha(Border, 0.6f);
            c[(int)ImGuiCol.TableRowBg] = Vector4.Zero;
            c[(int)ImGuiCol.TableRowBgAlt] = new Vector4(1f, 1f, 1f, 0.025f);
            c[(int)ImGuiCol.TextSelectedBg] = WithAlpha(Accent, 0.30f);
            c[(int)ImGuiCol.NavHighlight] = Accent;
            c[(int)ImGuiCol.ModalWindowDimBg] = new Vector4(0, 0, 0, 0.6f);
        }
    }
}
