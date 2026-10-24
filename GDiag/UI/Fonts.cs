using System;
using System.IO;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace GDiag.UI
{
    // Extra fonts on top of the main Segoe UI one: a semibold for headings, Bahnschrift (a DIN style
    // font, like car instrument clusters use) for big readings, Cascadia Mono for bytes and logs,
    // and the Windows icon font merged into the text fonts so icons can sit inside labels.
    // On a Mac the same roles go to Helvetica Neue, DIN Alternate and Menlo, with the bundled
    // Material Icons font for icons.
    public static class Fonts
    {
        public static ImFontPtr Semibold;
        public static ImFontPtr Display;      // gauge readings
        public static ImFontPtr DisplaySmall; // quick facts, card values
        public static ImFontPtr Title;        // page headings
        public static ImFontPtr Mono;

        // fonts are rasterized this many times larger and drawn scaled down, so they stay sharp on Retina screens
        public static float RasterScale = 1f;

        static string FontFolder => Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        // file (and face inside a .ttc collection) for each role
        static (string path, int face) MainFile => OperatingSystem.IsMacOS() ? ("/System/Library/Fonts/HelveticaNeue.ttc", 0) : (Path.Combine(FontFolder, "segoeui.ttf"), 0);
        static (string path, int face) SemiboldFile => OperatingSystem.IsMacOS() ? ("/System/Library/Fonts/HelveticaNeue.ttc", 10) : (Path.Combine(FontFolder, "seguisb.ttf"), 0);
        static (string path, int face) DisplayFile => OperatingSystem.IsMacOS() ? ("/System/Library/Fonts/Supplemental/DIN Alternate Bold.ttf", 0) : (Path.Combine(FontFolder, "bahnschrift.ttf"), 0);
        static (string path, int face) MonoFile => OperatingSystem.IsMacOS() ? ("/System/Library/Fonts/Menlo.ttc", 0) : (Path.Combine(FontFolder, "CascadiaMono.ttf"), 0);
        static string IconFile => OperatingSystem.IsWindows() ? Path.Combine(FontFolder, "segmdl2.ttf") : Path.Combine(AppContext.BaseDirectory, "Fonts", "MaterialIcons-Regular.ttf");

        // the main text font, or null when it isn't on this computer
        public static string MainFontPath => File.Exists(MainFile.path) ? MainFile.path : null;

        // called from ImGuiController's configure callback, after it added the main font and before the atlas is built
        public static void Load(float baseSize)
        {
            var io = ImGui.GetIO();
            string icons = IconFile;

            // the main font was just added by the controller, so this merges the icons into it
            MergeIcons(io, icons, baseSize);

            Semibold = Add(io, SemiboldFile, baseSize);
            MergeIcons(io, icons, baseSize);
            Title = Add(io, SemiboldFile, baseSize * 1.55f);
            MergeIcons(io, icons, baseSize * 1.4f);
            Display = Add(io, DisplayFile, baseSize * 2.6f);
            DisplaySmall = Add(io, DisplayFile, baseSize * 1.45f);
            Mono = Add(io, MonoFile, baseSize * 0.9f);
        }

        static unsafe ImFontPtr Add(ImGuiIOPtr io, (string path, int face) file, float size)
        {
            if (!File.Exists(file.path))
            {
                return default;
            }
            if (file.face == 0)
            {
                return io.Fonts.AddFontFromFileTTF(file.path, size, null, io.Fonts.GetGlyphRangesDefault());
            }
            var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            config.FontNo = file.face;
            var font = io.Fonts.AddFontFromFileTTF(file.path, size, config, io.Fonts.GetGlyphRangesDefault());
            config.Destroy();
            return font;
        }

        static unsafe void MergeIcons(ImGuiIOPtr io, string path, float size)
        {
            if (!File.Exists(path))
            {
                return;
            }
            var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            config.MergeMode = true;
            config.PixelSnapH = true;
            config.GlyphOffset = new System.Numerics.Vector2(0, size * 0.12f);
            config.GlyphMinAdvanceX = size;
            io.Fonts.AddFontFromFileTTF(path, size * 0.95f, config, IconRange);
            config.Destroy();
        }

        // the atlas reads the range when it is built, so it lives for the whole program
        static readonly IntPtr IconRange = OperatingSystem.IsWindows() ? MakeRange(0xE700, 0xEDFF) : MakeRange(0xE000, 0xF8FF);

        static IntPtr MakeRange(ushort first, ushort last)
        {
            IntPtr range = Marshal.AllocHGlobal(sizeof(ushort) * 3);
            Marshal.WriteInt16(range, 0, unchecked((short)first));
            Marshal.WriteInt16(range, 2, unchecked((short)last));
            Marshal.WriteInt16(range, 4, 0);
            return range;
        }

        static unsafe bool Has(ImFontPtr font) => font.NativePtr != null;

        // PushFont that quietly does nothing when the font file wasn't on this PC
        public static void Push(ImFontPtr font)
        {
            ImGui.PushFont(Has(font) ? font : ImGui.GetFont());
        }

        public static void Pop()
        {
            ImGui.PopFont();
        }
    }

    // glyphs from Segoe MDL2 Assets (ships with Windows 10 and 11), or from Material Icons elsewhere
    public static class Icons
    {
        static string Pick(string segoe, string material) => OperatingSystem.IsWindows() ? segoe : material;

        public static readonly string Heart = Pick("\uE95E", "\uE87D");
        public static readonly string Thermometer = Pick("\uE9CA", "\uF076");
        public static readonly string Bolt = Pick("\uE945", "\uEA0B");
        public static readonly string Battery = Pick("\uEBA7", "\uE1A4");
        public static readonly string Speedometer = Pick("\uEC4A", "\uE9E4");
        public static readonly string Gauge = Pick("\uEC48", "\uE01B");
        public static readonly string Drop = Pick("\uEB42", "\uE798");
        public static readonly string Flame = Pick("\uECAD", "\uEF55");
        public static readonly string Sun = Pick("\uE706", "\uE430");
        public static readonly string Cog = Pick("\uE713", "\uE8B8");
        public static readonly string Pulse = Pick("\uE9D9", "\uEAA2");
        public static readonly string Car = Pick("\uE804", "\uE531");
        public static readonly string Key = Pick("\uE8D7", "\uE0DA");
        public static readonly string Chip = Pick("\uE950", "\uE322");
        public static readonly string Warning = Pick("\uE7BA", "\uE002");
        public static readonly string Error = Pick("\uE783", "\uE000");
        public static readonly string Info = Pick("\uE946", "\uE88E");
        public static readonly string Check = Pick("\uE73E", "\uE5CA");
        public static readonly string CheckCircle = Pick("\uEC61", "\uE86C");
        public static readonly string Play = Pick("\uE768", "\uE037");
        public static readonly string Stop = Pick("\uE71A", "\uE047");
        public static readonly string Refresh = Pick("\uE895", "\uE5D5");
        public static readonly string Open = Pick("\uE8E5", "\uE2C8");
        public static readonly string Plug = Pick("\uE703", "\uE63C");
        public static readonly string Close = Pick("\uE711", "\uE5CD");
        public static readonly string Add = Pick("\uE710", "\uE145");
        public static readonly string More = Pick("\uE712", "\uE5D3");
        public static readonly string Search = Pick("\uE721", "\uE8B6");
        public static readonly string Copy = Pick("\uE8C8", "\uE14D");
        public static readonly string Terminal = Pick("\uE756", "\uEB8E");
        public static readonly string Code = Pick("\uE943", "\uE86F");
        public static readonly string Chart = Pick("\uE9D2", "\uE6E1");
        public static readonly string Road = Pick("\uEC49", "\uEACD");
    }
}
