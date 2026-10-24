using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using GDiag.Bmw;
using GDiag.Net;
using GDiag.Translation;
using GDiag.UI;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;

namespace GDiag
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            // command line tools that run without a window
            if (args.Length >= 1 && !args[0].StartsWith("--demo") && args[0] != "--screenshot")
            {
                // a Windows GUI program has no console of its own, so print to the terminal it was started from
                if (OperatingSystem.IsWindows())
                {
                    AttachConsole(-1);
                }
                switch (args[0])
                {
                    case "--translate" when args.Length >= 2:
                        foreach (string text in args.Skip(1))
                        {
                            Console.WriteLine($"{text}  ->  {CodingExplainer.ExplainLine(text).English}");
                        }
                        return 0;
                    case "--sim-server":
                        return RunSimServer(args.Length >= 2 && int.TryParse(args[1], out int port) ? port : Hsfz.DiagPort);
                    case "--discover":
                        foreach (var car in Discovery.Find())
                        {
                            Console.WriteLine($"{car.Address}  VIN {car.Vin}  MAC {car.Mac}  ({car.Raw})");
                        }
                        return 0;
                    case "--scan" when args.Length >= 2:
                        return RunScan(args[1]);
                }
            }

            AppDomain.CurrentDomain.UnhandledException += (sender, e) => ReportCrash(e.ExceptionObject as Exception);

            var log = new LogBuffer();
            Console.SetOut(log);

            // Silk.NET normally finds GLFW by scanning the app folder, which doesn't work in a single-file exe
            Silk.NET.Windowing.Glfw.GlfwWindowing.RegisterPlatform();
            Silk.NET.Input.Glfw.GlfwInput.RegisterPlatform();

            bool demo = Array.IndexOf(args, "--demo") >= 0;
            // "--screenshot file.png [--tab name]" saves one rendered frame and quits (for the README and for checking layouts)
            string screenshotPath = ArgAfter(args, "--screenshot");
            App.StartTab = ArgAfter(args, "--tab");
            int frame = 0;

            // open at three quarters of the screen, centered, with the UI scaled to match the screen
            var (screenWidth, screenHeight) = DetectUiScale();
            int width = Math.Max(1000, screenWidth * 3 / 4);
            int height = Math.Max(700, screenHeight * 3 / 4);

            var options = WindowOptions.Default;
            options.Size = new Vector2D<int>(width, height);
            options.Position = new Vector2D<int>((screenWidth - width) / 2, (screenHeight - height) / 2);
            options.Title = "G-Diag";
            options.VSync = true;
            if (OperatingSystem.IsMacOS())
            {
                // macOS only offers OpenGL 3.2+ as a forward compatible core profile
                options.API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3));
            }

            using var window = Window.Create(options);

            GL gl = null;
            IInputContext input = null;
            ImGuiController controller = null;
            App app = null;

            window.Load += () =>
            {
                gl = window.CreateOpenGL();
                input = window.CreateInput();

                // on Retina screens the framebuffer has more pixels than the window has points
                if (window.Size.X > 0)
                {
                    Fonts.RasterScale = Math.Max(1f, (float)window.FramebufferSize.X / window.Size.X);
                }

                string fontPath = Fonts.MainFontPath;
                if (fontPath != null)
                {
                    // the text size setting is applied as FontGlobalScale in App.Draw, so the atlas is built once at 18pt
                    int baseSize = (int)Math.Round(18 * Widgets.UiScale * Fonts.RasterScale);
                    controller = new ImGuiController(gl, window, input, new ImGuiFontConfig(fontPath, baseSize), () =>
                    {
                        ConfigureIO();
                        Fonts.Load(baseSize);
                    });
                }
                else
                {
                    controller = new ImGuiController(gl, window, input, ConfigureIO);
                }
                Theme.Apply();
                app = new App(log, demo);
            };

            window.FramebufferResize += size => gl?.Viewport(size);

            window.Render += delta =>
            {
                controller.Update((float)delta);
                gl.ClearColor(Color.FromArgb(255, 9, 11, 16));
                gl.Clear(ClearBufferMask.ColorBufferBit);
                app.Draw();
                controller.Render();

                // give the demo car a few seconds to be scanned and the live values to fill in
                if (screenshotPath != null && ++frame == 600)
                {
                    var size = window.FramebufferSize;
                    var pixels = new byte[size.X * size.Y * 4];
                    unsafe
                    {
                        fixed (byte* p = pixels)
                        {
                            gl.ReadPixels(0, 0, (uint)size.X, (uint)size.Y, PixelFormat.Rgba, PixelType.UnsignedByte, p);
                        }
                    }
                    PngWriter.Save(screenshotPath, pixels, size.X, size.Y);
                    window.Close();
                }
            };

            window.Closing += () =>
            {
                app?.Shutdown();

                // GL objects must be freed while the window's OpenGL context still exists
                controller?.Dispose();
                input?.Dispose();
                gl?.Dispose();
                controller = null;
            };

            window.Run();
            return 0;
        }

        static int RunSimServer(int port)
        {
            using var server = new HsfzSimServer(new SimVehicle(), port);
            Console.WriteLine($"Simulated G20 listening on 127.0.0.1:{server.TcpPort} (HSFZ), discovery on UDP {server.UdpPort}. Ctrl+C to stop.");
            Console.WriteLine($"Connect with: GDiag --scan 127.0.0.1:{server.TcpPort}, or type 127.0.0.1:{server.TcpPort} into the app.");
            var stop = new ManualResetEventSlim();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                stop.Set();
            };
            stop.Wait();
            return 0;
        }

        // "--scan host[:port]": connect, list the control units and their fault codes, for checking a setup quickly
        static int RunScan(string target)
        {
            var (host, port) = SplitHostPort(target);
            try
            {
                using var session = new CarSession(new HsfzClient(host, port));
                Console.WriteLine($"Connected to {host}:{port}. VIN: {session.ReadVin()}");
                foreach (var module in session.Scan(AppSettings.Current.FullScan, p => Console.Error.WriteLine(p)))
                {
                    session.ReadDtcs(module);
                    string faults = module.Dtcs is null ? module.DtcError : $"{module.Dtcs.Count} fault(s)";
                    Console.WriteLine($"0x{module.Address:X2} {module.Info.Short,-6} {module.Info.Name,-28} {module.Cafd,-28} {faults}");
                    foreach (var dtc in module.Dtcs ?? new())
                    {
                        Console.WriteLine($"      {dtc.CodeText}  {dtc.Summary}");
                    }
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Scan failed: {ex.Message}");
                return 1;
            }
        }

        static string ArgAfter(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static (string host, int port) SplitHostPort(string text)
        {
            text = text.Trim();
            int colon = text.LastIndexOf(':');
            if (colon > 0 && int.TryParse(text.Substring(colon + 1), out int port))
            {
                return (text.Substring(0, colon), port);
            }
            return (text, Hsfz.DiagPort);
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern bool AttachConsole(int processId);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern uint GetDpiForSystem();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);

        // Picks the UI scale from the Windows display scaling and the screen resolution, whichever is larger,
        // so a 4K screen gets big enough text even when Windows scaling is left at 100%.
        static (int width, int height) DetectUiScale()
        {
            if (!OperatingSystem.IsWindows())
            {
                // macOS already works in points that are scaled for the screen, Retina sharpness comes from Fonts.RasterScale
                var mode = Silk.NET.Windowing.Monitor.GetMainMonitor(null)?.VideoMode;
                var resolution = mode?.Resolution ?? new Vector2D<int>(1440, 900);
                Widgets.UiScale = 1f;
                return (resolution.X, resolution.Y);
            }
            int width = 1920;
            int height = 1080;
            float dpiScale = 1f;
            try
            {
                // per-monitor v2: real pixel sizes instead of Windows stretching a blurry bitmap
                SetProcessDpiAwarenessContext(new IntPtr(-4));
                dpiScale = GetDpiForSystem() / 96f;
                width = GetSystemMetrics(0);
                height = GetSystemMetrics(1);
            }
            catch
            {
                // older Windows: keep defaults
            }
            float resolutionScale = height / 1200f;
            Widgets.UiScale = Math.Clamp(Math.Max(dpiScale, resolutionScale), 1f, 4f);
            return (width, height);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        static void ReportCrash(Exception ex)
        {
            string path = Path.Combine(AppSettings.SettingsFolder, "crash.log");
            try
            {
                Directory.CreateDirectory(AppSettings.SettingsFolder);
                File.WriteAllText(path, $"{DateTime.Now}\n{ex}");
            }
            catch
            {
                // nothing else we can do
            }
            string message = $"G-Diag crashed:\n\n{ex?.Message}\n\nDetails were saved to {path}";
            if (OperatingSystem.IsWindows())
            {
                MessageBoxW(IntPtr.Zero, message, "G-Diag", 0x10);
            }
            else if (OperatingSystem.IsMacOS())
            {
                FileDialogs.Alert("G-Diag crashed", message);
            }
        }

        static void ConfigureIO()
        {
            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
            // don't litter the working directory with imgui.ini
            unsafe { io.NativePtr->IniFilename = null; }
        }
    }
}
