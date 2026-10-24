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
                    case "--drivers":
                        var drivers = J2534Driver.Find(AppSettings.Current.J2534DriverFiles);
                        for (int i = 0; i < drivers.Count; i++)
                        {
                            Console.WriteLine($"{i}: {drivers[i].Name}  {drivers[i].Library}  ({drivers[i].LongSize * 8}-bit longs)");
                        }
                        if (drivers.Count == 0)
                        {
                            Console.WriteLine("No J2534 driver found.");
                        }
                        return 0;
                    case "--scan" when args.Length >= 2:
                        return RunScan(args[1]);
                    case "--live" when args.Length >= 2:
                        return RunLive(args[1], args.Length >= 3 && int.TryParse(args[2], out int rounds) ? rounds : 1);
                    case "--did-scan" when args.Length >= 5:
                        return RunDidScan(args[1], args[2], args[3], args[4]);
                    case "--coding" when args.Length >= 3:
                        return RunCoding(args[1], args[2], args.Length >= 4 ? args[3] : null);
                    case "--read" when args.Length >= 3:
                        return RunRead(args[1], args.Skip(2));
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

                // give the demo car a few seconds to be scanned and the live values to fill in, and 5 more after any task
                if (screenshotPath != null && ++frame >= 600 && (frame >= LastBusyFrame(app, frame) + 300 || frame >= 4000))
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

        // "--scan host[:port]" (ENET), "--scan j2534[:N]" (driver N from --drivers, e.g. the OpenPort) or
        // "--scan sim-dcan" (the demo car on simulated D-CAN): connect, list the control units and their fault codes
        static IDiagTransport OpenTransport(string target)
        {
            if (target == "sim-dcan")
            {
                return new DCanTransport(new SimCanBus(new SimVehicle()));
            }
            if (target.StartsWith("j2534", StringComparison.OrdinalIgnoreCase))
            {
                var drivers = J2534Driver.Find(AppSettings.Current.J2534DriverFiles);
                int index = target.Length > 6 && int.TryParse(target.Substring(6), out int n) ? n : 0;
                if (index >= drivers.Count)
                {
                    throw new InvalidOperationException("No such J2534 driver, see --drivers");
                }
                return new DCanTransport(new J2534CanBus(drivers[index]));
            }
            var (host, port) = SplitHostPort(target);
            return new HsfzClient(host, port);
        }

        static int RunScan(string target)
        {
            try
            {
                using var session = new CarSession(OpenTransport(target));
                Console.WriteLine($"Connected via {session.Uds.Description}. VIN: {session.ReadVin()}");
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

        // "--live target [rounds]": every built-in live value, read like the Live tab does
        static int RunLive(string target, int rounds)
        {
            try
            {
                using var session = new CarSession(OpenTransport(target));
                Console.WriteLine($"Connected via {session.Uds.Description}");
                var supported = session.ReadSupportedPids();
                Console.WriteLine($"Supported PIDs: {(supported is null ? "(not reported)" : string.Join(" ", supported.OrderBy(p => p).Select(p => p.ToString("X2"))))}");
                var values = LiveData.All.Where(v => v.IsComputed || !v.IsObd || supported is null || supported.Contains((byte)v.Did)).ToList();
                for (int round = 0; round < rounds; round++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var readings = new System.Collections.Generic.Dictionary<string, double>();
                    var batch = session.ReadLiveBatch(values.Where(v => !v.IsComputed).ToList());
                    foreach (var value in values.Where(v => !v.IsComputed))
                    {
                        Uds.UdsResponse response = null;
                        byte[] record = batch.TryGetValue(value, out var r) ? r : session.ReadLive(value, out response);
                        double? decoded = value.Decode(record);
                        if (decoded is double d)
                        {
                            readings[value.Id] = d;
                        }
                        string reading = decoded is double x ? $"{x.ToString("F" + value.Decimals)} {value.Unit}" : $"- ({response?.Describe()})";
                        Console.WriteLine($"{value.Group,-22} {value.Name,-38} {reading,-24} {(record is null ? "" : Hex.Format(record))}");
                    }
                    foreach (var value in values.Where(v => v.IsComputed))
                    {
                        string reading = value.Inputs.All(readings.ContainsKey) ? $"{value.Compute(id => readings[id]).ToString("F" + value.Decimals)} {value.Unit}" : "-";
                        Console.WriteLine($"{value.Group,-22} {value.Name,-38} {reading}");
                    }
                    Console.WriteLine($"({watch.ElapsedMilliseconds} ms for {values.Count} values)");
                    Console.WriteLine();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Live data failed: {ex.Message}");
                return 1;
            }
        }

        // "--read target 12:22F40C 40:22F190 ...": read-only requests, printed as they come back
        static int RunRead(string target, System.Collections.Generic.IEnumerable<string> requests)
        {
            try
            {
                using var session = new CarSession(OpenTransport(target));
                foreach (string text in requests)
                {
                    string[] parts = text.Split(':');
                    byte[] request = null;
                    if (parts.Length != 2 || !Hex.TryParse(parts[1], out request) || request.Length == 0 || !Hex.TryParseNumber(parts[0], out uint address) || address > 0xFF)
                    {
                        Console.WriteLine($"{text}: expected ADDRESS:HEX, e.g. 12:22F40C (FE is OBD-II on 7E0)");
                        continue;
                    }
                    if (!Uds.ServiceSafety.IsReadOnly(request) && !(address == DCanTransport.ObdEngine && request[0] is 0x01 or 0x09))
                    {
                        Console.WriteLine($"{text}: only read requests are allowed here");
                        continue;
                    }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var response = session.Uds.Request((byte)address, request);
                    Console.WriteLine($"{address:X2} {Hex.Format(request),-14} -> {response.Describe()}  ({watch.ElapsedMilliseconds} ms)");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Read failed: {ex.Message}");
                return 1;
            }
        }

        // "--coding target module [backup.json]": read a module's coding blocks, save them or compare with a backup
        static int RunCoding(string target, string moduleText, string backupPath)
        {
            if (!Hex.TryParseNumber(moduleText, out uint address) || address > 0xFF)
            {
                Console.Error.WriteLine("usage: --coding target module(hex) [backup.json]");
                return 1;
            }
            try
            {
                using var session = new CarSession(OpenTransport(target));
                session.ReadVin();
                var module = new ModuleState { Address = (byte)address, Info = EcuCatalog.Lookup((byte)address) };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var blocks = session.ReadCoding(module, p => Console.Error.WriteLine(p));
                Console.WriteLine($"{module.Info.Short}: {blocks.Count} blocks, {blocks.Values.Sum(b => b.Length)} bytes in {watch.Elapsed.TotalSeconds:0.0} s");
                if (backupPath is null)
                {
                    return 0;
                }
                if (File.Exists(backupPath))
                {
                    var before = CodingBackup.Load(backupPath).BlocksOf((byte)address) ?? new();
                    var changes = CodingCompare.Changes(before, blocks);
                    Console.WriteLine($"{changes.Count} blocks differ from {backupPath}");
                    foreach (var change in changes)
                    {
                        Console.WriteLine($"  {change.Block:X4} bytes {string.Join(",", change.Offsets)}");
                    }
                }
                else
                {
                    CodingBackup.From(session.Vin, new[] { module }).Save(backupPath);
                    Console.WriteLine($"Saved to {backupPath}");
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Coding read failed: {ex.Message}");
                return 1;
            }
        }

        // "--did-scan target module from to": every DID in the range the module answers, read only
        static int RunDidScan(string target, string moduleText, string fromText, string toText)
        {
            if (!Hex.TryParseNumber(moduleText, out uint module) || !Hex.TryParseNumber(fromText, out uint from) || !Hex.TryParseNumber(toText, out uint to))
            {
                Console.Error.WriteLine("usage: --did-scan target module from to (hex)");
                return 1;
            }
            try
            {
                using var session = new CarSession(OpenTransport(target));
                session.Uds.TimeoutMs = 250;
                for (uint did = from; did <= to && did <= 0xFFFF; did++)
                {
                    byte[] record = session.Uds.ReadDid((byte)module, (ushort)did, out var response);
                    if (record != null)
                    {
                        Console.WriteLine($"{did:X4}  {Hex.Format(record)}");
                    }
                    else if (response.Nrc != 0x31)
                    {
                        Console.Error.WriteLine($"{did:X4}  {response.Describe()}");
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

        // the frame at which the app was last seen busy, so a screenshot waits for scans and reads to finish
        static int lastBusy;
        static int LastBusyFrame(App app, int frame)
        {
            if (app.Busy)
            {
                lastBusy = frame;
            }
            return lastBusy;
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
