using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using GDiag.Bmw;
using GDiag.Net;
using GDiag.Uds;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Main window: the three setup steps along the top, the tabs, and the log at the bottom.
    public class App
    {
        static AppSettings Settings => AppSettings.Current;

        public readonly LogBuffer Log;
        public readonly LogBuffer Trace = new LogBuffer();

        readonly VehicleTab Vehicle;
        readonly FaultsTab Faults;
        readonly LiveTab Live;
        readonly OrderTab Order;
        readonly CodingTab Coding;
        readonly ConsoleTab ConsolePanel;

        public CarSession Session { get; private set; }
        public bool Connected => Session != null && Session.IsOpen;
        public bool Scanned { get; private set; }

        List<DiscoveredCar> Discovered = new List<DiscoveredCar>();
        string GatewayAddress = "";

        // one background operation at a time (connecting, scanning, reading faults...)
        volatile bool busy = false;
        volatile bool busyQuiet = false;
        string BusyLabel = "";
        volatile string BusyProgress = "";
        readonly ConcurrentQueue<Action> MainThreadQueue = new ConcurrentQueue<Action>();

        string MessageTitle = null;
        string MessageBody = null;
        bool OpenMessagePopup = false;

        string ConfirmTitle = null;
        string ConfirmBody = null;
        Action ConfirmAction = null;
        bool OpenConfirmPopup = false;
        string ConfirmYesLabel = "Yes, continue";
        bool ConfirmFinalStep = false;
        double ConfirmUnlockTime = 0;

        bool PopupOpenDummy = true;
        bool ShowTrace = false;
        int LastLogVersion = -1;
        int LastTraceVersion = -1;

        public bool Busy => busy;

        // tab to open first ("--tab live"), part of a word from its label is enough
        public static string StartTab;
        bool StartTabApplied;

        public App(LogBuffer log, bool demo)
        {
            Log = log;
            Vehicle = new VehicleTab(this);
            Faults = new FaultsTab(this);
            Live = new LiveTab(this);
            Order = new OrderTab(this);
            Coding = new CodingTab(this);
            ConsolePanel = new ConsoleTab(this);

            GatewayAddress = Settings.RecentGateways.FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(Settings.FaultTextFile))
            {
                int count = FaultTexts.Load(Settings.FaultTextFile);
                Console.WriteLine($"Loaded {count} fault code texts from {Settings.FaultTextFile}");
            }
            Console.WriteLine("G-Diag for BMW F/G series over ENET. Plug in the ENET cable, switch the ignition on, then find your car.");
            if (demo)
            {
                ConnectDemo();
            }
        }

        public void Shutdown()
        {
            Live.Stop();
            CloseConnection();
            Settings.Save();
        }

        // ---------------------------------------------------------------- background work

        public bool RunBackground(string label, Action work, Action onFinishedMainThread = null, bool quiet = false)
        {
            if (busy)
            {
                if (!quiet)
                {
                    Console.WriteLine($"Please wait, '{BusyLabel}' is still running.");
                }
                return false;
            }
            busy = true;
            busyQuiet = quiet;
            BusyLabel = label;
            BusyProgress = "";
            Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{label} failed: {ex.Message}");
                    if (!quiet)
                    {
                        ShowMessage($"{label} failed", ex.Message);
                    }
                }
                finally
                {
                    busy = false;
                    if (onFinishedMainThread != null)
                    {
                        MainThreadQueue.Enqueue(onFinishedMainThread);
                    }
                }
            });
            return true;
        }

        public void Progress(string text) => BusyProgress = text;

        public void OnMainThread(Action action) => MainThreadQueue.Enqueue(action);

        public void ShowMessage(string title, string body)
        {
            MainThreadQueue.Enqueue(() =>
            {
                MessageTitle = title;
                MessageBody = body;
                OpenMessagePopup = true;
            });
        }

        public void Confirm(string title, string body, Action onYes)
        {
            ConfirmTitle = title;
            ConfirmBody = body;
            ConfirmAction = onYes;
            ConfirmYesLabel = "Yes, continue";
            ConfirmFinalStep = false;
            OpenConfirmPopup = true;
        }

        // for anything that changes the car: asks once, then a second time with the yes button locked
        // for a few seconds, so a quick double-click can't get through both
        public void ConfirmDangerous(string title, string body, Action onYes)
        {
            Confirm(title, body, () =>
            {
                ConfirmTitle = "Are you absolutely sure?";
                ConfirmBody = $"{title}\n\nThis changes something in the car. Keep the ignition on, connect a battery charger, and only go ahead if you know what this does.";
                ConfirmAction = onYes;
                ConfirmYesLabel = "Yes, send it";
                ConfirmFinalStep = true;
                ConfirmUnlockTime = ImGui.GetTime() + 3;
                OpenConfirmPopup = true;
            });
        }

        // ---------------------------------------------------------------- connection

        void Discover()
        {
            RunBackground("Looking for cars", () =>
            {
                var cars = Discovery.Find();
                OnMainThread(() =>
                {
                    Discovered = cars;
                    if (cars.Count > 0)
                    {
                        GatewayAddress = cars[0].Address;
                    }
                });
                Console.WriteLine(cars.Count == 0
                    ? "No car answered. Check the ENET cable, that the ignition is on, and that this computer has a 169.254.x.x address on the cable's network adapter."
                    : $"Found {cars.Count}: {string.Join(", ", cars)}");
            });
        }

        void Connect()
        {
            string target = GatewayAddress.Trim();
            if (target.Length == 0)
            {
                ShowMessage("No address", "Press Find car first, or type the car's IP address (it starts with 169.254 on a direct ENET cable).");
                return;
            }
            var (host, port) = Program.SplitHostPort(target);
            RunBackground("Connecting", () =>
            {
                var session = new CarSession(new HsfzClient(host, port));
                AttachTrace(session);
                string vin;
                try
                {
                    vin = session.ReadVin();
                }
                catch
                {
                    session.Dispose();
                    throw;
                }
                Console.WriteLine(vin.Length > 0 ? $"Connected to {host}:{port}, VIN {vin}" : $"Connected to {host}:{port}, but the gateway did not report a VIN");
                OnMainThread(() =>
                {
                    Session = session;
                    Scanned = false;
                    Settings.AddRecentGateway(target);
                    Scan();
                });
            });
        }

        void ConnectDemo()
        {
            CloseConnection();
            var session = new CarSession(new SimTransport(new SimVehicle()));
            AttachTrace(session);
            Session = session;
            Scanned = false;
            RunBackground("Connecting to the demo car", () =>
            {
                session.ReadVin();
                Console.WriteLine($"Connected to the demo car (made-up data), VIN {session.Vin}");
            }, Scan);
        }

        void AttachTrace(CarSession session)
        {
            session.Uds.Trace = line =>
            {
                Trace.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {line}");
            };
        }

        public void Scan()
        {
            if (!Connected)
            {
                return;
            }
            var session = Session;
            RunBackground("Scanning modules", () =>
            {
                var modules = session.Scan(Settings.FullScan, Progress);
                Console.WriteLine($"Found {modules.Count} control units: {string.Join(", ", modules.Select(m => m.Info.Short))}");
            }, () =>
            {
                Scanned = true;
                // when started on a tab, fill it in so there is something to see
                if (StartTab != null && "fault".StartsWith(StartTab, StringComparison.OrdinalIgnoreCase))
                {
                    RunBackground("Reading fault codes", () => session.ModulesSnapshot().ForEach(session.ReadDtcs));
                }
                else if (StartTab != null && "live".StartsWith(StartTab, StringComparison.OrdinalIgnoreCase))
                {
                    Live.Start();
                }
            });
        }

        public void CloseConnection()
        {
            Live.Stop();
            var session = Session;
            Session = null;
            Scanned = false;
            session?.Dispose();
        }

        // ---------------------------------------------------------------- drawing

        public void Draw()
        {
            while (MainThreadQueue.TryDequeue(out var action))
            {
                action();
            }
            if (Session != null && !Session.IsOpen)
            {
                Console.WriteLine("Lost the connection to the car.");
                CloseConnection();
            }

            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowPos(viewport.WorkPos);
            ImGui.SetNextWindowSize(viewport.WorkSize);
            ImGui.Begin("G-Diag", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize |
                ImGuiWindowFlags.NoBringToFrontOnFocus | ImGuiWindowFlags.MenuBar);

            DrawMenuBar();
            DrawSetupBar();
            float logHeight = S(170);
            ImGui.BeginChild("tabs", new Vector2(0, -logHeight - ImGui.GetStyle().ItemSpacing.Y), ImGuiChildFlags.None);
            DrawTabs();
            ImGui.EndChild();
            DrawLogPanel(logHeight);
            DrawPopups();

            ImGui.End();
        }

        void DrawMenuBar()
        {
            if (!ImGui.BeginMenuBar())
            {
                return;
            }
            if (ImGui.BeginMenu("File"))
            {
                if (ImGui.MenuItem($"{Icons.Open}  Open vehicle order (FA)..."))
                {
                    Order.OpenDialog();
                }
                if (ImGui.MenuItem($"{Icons.Open}  Load fault code texts..."))
                {
                    Faults.LoadTextsDialog();
                }
                ImGui.Separator();
                if (ImGui.MenuItem($"{Icons.Car}  Connect to the demo car", null, false, !busy))
                {
                    ConnectDemo();
                }
                if (ImGui.MenuItem($"{Icons.Close}  Disconnect", null, false, Connected && !busy))
                {
                    CloseConnection();
                }
                ImGui.EndMenu();
            }
            if (ImGui.BeginMenu("View"))
            {
                bool advanced = Settings.AdvancedMode;
                if (ImGui.MenuItem("Advanced mode (raw console)", null, ref advanced))
                {
                    Settings.AdvancedMode = advanced;
                    Settings.Save();
                }
                bool translate = Settings.TranslateToEnglish;
                if (ImGui.MenuItem("Translate German coding names", null, ref translate))
                {
                    Settings.TranslateToEnglish = translate;
                    Settings.Save();
                }
                ImGui.EndMenu();
            }

            // status on the right
            string status = busy && !busyQuiet ? $"{BusyLabel}... {BusyProgress}" : Connected ? $"Connected: {Session.Uds.Description}" : "Not connected";
            float width = ImGui.CalcTextSize(status).X + S(40);
            ImGui.SameLine(ImGui.GetWindowWidth() - width);
            StatusDot(busy && !busyQuiet ? Theme.Warn : Connected ? Theme.Good : Theme.Dim, busy && !busyQuiet);
            ImGui.TextUnformatted(status);
            ImGui.EndMenuBar();
        }

        void DrawSetupBar()
        {
            float height = ImGui.GetFrameHeightWithSpacing() * 2 + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2;
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float third = (ImGui.GetContentRegionAvail().X - spacing * 2) / 3;

            // 1: find the car
            BeginCard("step1", new Vector2(third, height));
            StepTitle(1, "Find your car", GatewayAddress.Length > 0 || Connected);
            ImGui.SetNextItemWidth(-S(120));
            ImGui.InputTextWithHint("##addr", "169.254.x.x", ref GatewayAddress, 64);
            Tooltip("The gateway's IP address. Find car fills it in; add :port for the simulator.");
            ImGui.SameLine();
            if (ImGui.BeginCombo("##recent", "", ImGuiComboFlags.NoPreview))
            {
                foreach (var car in Discovered)
                {
                    if (ImGui.Selectable($"{car}##d{car.Address}"))
                    {
                        GatewayAddress = car.Address;
                    }
                }
                foreach (string recent in Settings.RecentGateways)
                {
                    if (ImGui.Selectable($"{recent}  (recent)##r{recent}"))
                    {
                        GatewayAddress = recent;
                    }
                }
                if (Discovered.Count == 0 && Settings.RecentGateways.Count == 0)
                {
                    ImGui.TextDisabled("Nothing found yet");
                }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (Button($"{Icons.Search} Find", !busy && !Connected, "Disconnect first"))
            {
                Discover();
            }
            if (ColoredButton($"{Icons.Car} Try the demo car", Theme.Accent2, default, !busy))
            {
                ConnectDemo();
            }
            Tooltip("A simulated G20 with made-up data, to look around without a car.");
            EndCard();

            // 2: connect
            ImGui.SameLine();
            BeginCard("step2", new Vector2(third, height));
            StepTitle(2, "Connect", Connected);
            if (Connected)
            {
                StatusDot(Theme.Good);
                Fonts.Push(Fonts.Semibold);
                ImGui.TextUnformatted(string.IsNullOrEmpty(Session.Vin) ? "Connected" : Session.Vin);
                Fonts.Pop();
                Dim(Session.Uds.Description);
                if (Button($"{Icons.Close} Disconnect", !busy, "Wait for the current task"))
                {
                    CloseConnection();
                }
            }
            else
            {
                Dim("ENET cable in the OBD port, ignition on.");
                if (PrimaryButton($"{Icons.Plug}  Connect", Theme.Accent, default, !busy && GatewayAddress.Trim().Length > 0))
                {
                    Connect();
                }
            }
            EndCard();

            // 3: scan
            ImGui.SameLine();
            BeginCard("step3", new Vector2(third, height));
            int count = Session?.ModulesSnapshot().Count ?? 0;
            StepTitle(3, "Scan control units", Scanned);
            if (busy && BusyLabel == "Scanning modules")
            {
                StatusDot(Theme.Warn, true);
                ImGui.TextUnformatted(BusyProgress);
            }
            else
            {
                Dim(Scanned ? $"{count} control units answered." : "Lists every module in the car.");
            }
            if (Button($"{Icons.Refresh} Scan again", Connected && !busy, Connected ? "Wait for the current task" : "Connect first"))
            {
                Scan();
            }
            EndCard();
        }

        static void StepTitle(int number, string title, bool done)
        {
            var color = done ? Theme.Good : Theme.Accent;
            Fonts.Push(Fonts.Semibold);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(done ? Icons.CheckCircle : $"{number}.");
            ImGui.PopStyleColor();
            ImGui.SameLine();
            ImGui.TextUnformatted(title);
            Fonts.Pop();
        }

        void DrawTabs()
        {
            if (!ImGui.BeginTabBar("main"))
            {
                return;
            }
            TabItem($"{Icons.Car}  Vehicle", Vehicle.Draw);
            TabItem($"{Icons.Warning}  Fault codes", Faults.Draw);
            TabItem($"{Icons.Speedometer}  Live data", Live.Draw);
            TabItem($"{Icons.Key}  Vehicle order", Order.Draw);
            TabItem($"{Icons.Code}  Coding explainer", Coding.Draw);
            if (Settings.AdvancedMode)
            {
                TabItem($"{Icons.Terminal}  Console", ConsolePanel.Draw);
            }
            TabItem($"{Icons.Cog}  Settings", DrawSettingsTab);
            ImGui.EndTabBar();
            StartTabApplied = true;
        }

        void TabItem(string label, Action draw)
        {
            var flags = ImGuiTabItemFlags.None;
            if (!StartTabApplied && StartTab != null && label.Contains(StartTab, StringComparison.OrdinalIgnoreCase))
            {
                // the first matching tab wins ("vehicle" is the Vehicle tab, not Vehicle order)
                flags = ImGuiTabItemFlags.SetSelected;
                StartTabApplied = true;
            }
            if (BeginTabItem(label, flags))
            {
                ImGui.BeginChild("tabcontent", Vector2.Zero, ImGuiChildFlags.None);
                draw();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
        }

        // ImGui.NET only takes flags together with a close button (p_open), so call the native function without one
        static unsafe bool BeginTabItem(string label, ImGuiTabItemFlags flags)
        {
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(label + "\0");
            fixed (byte* p = utf8)
            {
                return ImGuiNative.igBeginTabItem(p, null, flags) != 0;
            }
        }

        void DrawSettingsTab()
        {
            SectionTitle(Icons.Cog, "Settings");
            bool changed = false;
            bool full = Settings.FullScan;
            if (ImGui.Checkbox("Scan every address (0x00-0x7F), not just the known ones", ref full))
            {
                Settings.FullScan = full;
                changed = true;
            }
            Help("Slower (up to a minute) but finds modules this program has no name for.");
            bool us = Settings.UsUnits;
            if (ImGui.Checkbox("US units (°F, mph, psi)", ref us))
            {
                Settings.UsUnits = us;
                changed = true;
            }
            bool translate = Settings.TranslateToEnglish;
            if (ImGui.Checkbox("Translate German coding names to English", ref translate))
            {
                Settings.TranslateToEnglish = translate;
                changed = true;
            }
            bool advanced = Settings.AdvancedMode;
            if (ImGui.Checkbox("Advanced mode (raw UDS console)", ref advanced))
            {
                Settings.AdvancedMode = advanced;
                changed = true;
            }
            float interval = Settings.LiveInterval;
            ImGui.SetNextItemWidth(S(220));
            if (ImGui.SliderFloat("Live data refresh (seconds)", ref interval, 0.2f, 5f, "%.1f"))
            {
                Settings.LiveInterval = interval;
                changed = true;
            }
            float font = Settings.FontSize;
            ImGui.SetNextItemWidth(S(220));
            if (ImGui.SliderFloat("Text size (after restart)", ref font, 14f, 28f, "%.0f"))
            {
                Settings.FontSize = font;
                changed = true;
            }
            if (changed)
            {
                Settings.Save();
            }
            ImGui.Spacing();
            Dim($"Settings are saved in {AppSettings.SettingsFolder}");
        }

        void DrawLogPanel(float height)
        {
            BeginCard("log", new Vector2(0, height));
            if (ImGui.RadioButton("Log", !ShowTrace))
            {
                ShowTrace = false;
            }
            ImGui.SameLine();
            if (ImGui.RadioButton("Trace (every message to and from the car)", ShowTrace))
            {
                ShowTrace = true;
            }
            ImGui.SameLine(ImGui.GetWindowWidth() - S(90));
            if (ImGui.SmallButton($"{Icons.Copy} Copy"))
            {
                ImGui.SetClipboardText(string.Join("\n", (ShowTrace ? Trace : Log).Snapshot()));
            }

            // the card itself never scrolls, the lines get their own scrolling child
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
            ImGui.BeginChild("loglines", Vector2.Zero, ImGuiChildFlags.None);
            ImGui.PopStyleColor();
            Fonts.Push(Fonts.Mono);
            var buffer = ShowTrace ? Trace : Log;
            ref int lastVersion = ref ShowTrace ? ref LastTraceVersion : ref LastLogVersion;
            foreach (string line in buffer.Snapshot())
            {
                ImGui.TextUnformatted(line);
            }
            Fonts.Pop();
            if (buffer.Version != lastVersion)
            {
                lastVersion = buffer.Version;
                ImGui.SetScrollHereY(1f);
            }
            ImGui.EndChild();
            EndCard();
        }

        void DrawPopups()
        {
            if (OpenMessagePopup)
            {
                ImGui.OpenPopup("message");
                OpenMessagePopup = false;
                PopupOpenDummy = true;
            }
            ImGui.SetNextWindowSize(new Vector2(S(520), 0));
            if (ImGui.BeginPopupModal("message", ref PopupOpenDummy, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
            {
                SectionTitle(Icons.Info, MessageTitle ?? "");
                ImGui.Separator();
                ImGui.TextWrapped(MessageBody ?? "");
                ImGui.Spacing();
                if (PrimaryButton("OK", Theme.Accent, new Vector2(S(120), 0)))
                {
                    ImGui.CloseCurrentPopup();
                }
                ImGui.EndPopup();
            }

            if (OpenConfirmPopup)
            {
                ImGui.OpenPopup("confirm");
                OpenConfirmPopup = false;
                PopupOpenDummy = true;
            }
            ImGui.SetNextWindowSize(new Vector2(S(560), 0));
            if (ImGui.BeginPopupModal("confirm", ref PopupOpenDummy, ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.AlwaysAutoResize))
            {
                SectionTitle(ConfirmFinalStep ? Icons.Warning : Icons.Info, ConfirmTitle ?? "");
                ImGui.Separator();
                ImGui.TextWrapped(ConfirmBody ?? "");
                ImGui.Spacing();
                double wait = ConfirmUnlockTime - ImGui.GetTime();
                bool unlocked = !ConfirmFinalStep || wait <= 0;
                string yes = unlocked ? ConfirmYesLabel : $"{ConfirmYesLabel} ({Math.Ceiling(wait)})";
                // on the final step the safe choice sits where "yes" was the first time
                if (ConfirmFinalStep)
                {
                    if (PrimaryButton("No, cancel", Theme.Accent, new Vector2(S(160), 0)))
                    {
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.SameLine();
                }
                if (ColoredButton(yes + "##yes", ConfirmFinalStep ? Theme.Bad : Theme.Warn, new Vector2(S(180), 0), unlocked))
                {
                    var action = ConfirmAction;
                    ConfirmAction = null;
                    ImGui.CloseCurrentPopup();
                    action?.Invoke();
                }
                if (!ConfirmFinalStep)
                {
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(S(120), 0)))
                    {
                        ImGui.CloseCurrentPopup();
                    }
                }
                ImGui.EndPopup();
            }
        }
    }
}
