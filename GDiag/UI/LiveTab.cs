using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using GDiag.Bmw;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Engine readings as gauges and charts, plus any DID the user adds, read again and again while it runs.
    // Gauges and charted values are read every round; the rest of the table every few rounds, so what you
    // watch updates as fast as the car answers.
    public class LiveTab
    {
        const int HistoryLength = 600;
        const int SlowEvery = 4;

        class Track
        {
            public readonly float[] Data = new float[HistoryLength];
            public int Count;
            public int Offset;
            public double Min = double.MaxValue;
            public double Max = double.MinValue;

            public void Add(double v)
            {
                if (Count < HistoryLength)
                {
                    Data[Count++] = (float)v;
                }
                else
                {
                    Data[Offset] = (float)v;
                    Offset = (Offset + 1) % HistoryLength;
                }
                Min = Math.Min(Min, v);
                Max = Math.Max(Max, v);
            }

            public (float lo, float hi) Range()
            {
                float lo = float.MaxValue, hi = float.MinValue;
                for (int i = 0; i < Count; i++)
                {
                    lo = Math.Min(lo, Data[i]);
                    hi = Math.Max(hi, Data[i]);
                }
                return (lo, hi);
            }
        }

        readonly App App;
        readonly ConcurrentDictionary<string, double?> Values = new ConcurrentDictionary<string, double?>();
        readonly ConcurrentDictionary<string, string> Problems = new ConcurrentDictionary<string, string>();
        readonly Dictionary<string, Track> History = new Dictionary<string, Track>();
        List<LiveValue> CustomValues;
        // OBD PIDs the engine reported, null until read (or when it doesn't report them)
        volatile HashSet<byte> SupportedPids;

        Thread Poller;
        volatile bool polling;
        int LastSampleVersion;
        volatile int SampleVersion;
        volatile float RoundMs;
        bool ShowUnsupported;
        string Filter = "";

        // CSV recording, written by the UI thread once per new sample
        StreamWriter Recorder;
        string RecordPath;
        DateTime RecordStarted;
        List<LiveValue> RecordColumns;
        // units chosen when recording started, so switching mid-recording doesn't mix them in one file
        bool RecordUs;

        // add-a-value form
        string NewModule = "12";
        string NewDid = "";
        string NewName = "";
        string NewScale = "1";
        string NewOffset = "0";
        string NewUnit = "";
        int NewBytes = 1;
        bool NewSigned;

        static AppSettings Settings => AppSettings.Current;

        public LiveTab(App app)
        {
            App = app;
            CustomValues = LiveData.Custom(Settings.CustomDids);
            if (Settings.LiveGauges is null || Settings.LiveGauges.Count == 0)
            {
                Settings.LiveGauges = LiveData.DefaultGauges.ToList();
            }
            Settings.LiveCharts ??= new List<string>();
        }

        public bool Polling => polling;

        IEnumerable<LiveValue> AllValues => LiveData.All.Concat(CustomValues);

        // false only when the engine's support bitmap says it doesn't have this PID
        bool Supported(LiveValue value)
        {
            var pids = SupportedPids;
            if (value.IsComputed)
            {
                return value.Inputs.All(id => AllValues.FirstOrDefault(v => v.Id == id) is LiveValue input && Supported(input));
            }
            return pids is null || !value.IsObd || pids.Contains((byte)value.Did);
        }

        bool Watched(LiveValue value) => Settings.LiveGauges.Contains(value.Id) || Settings.LiveCharts.Contains(value.Id);

        public void Start()
        {
            var session = App.Session;
            if (session is null || polling)
            {
                return;
            }
            polling = true;
            Problems.Clear();
            Poller = new Thread(() =>
            {
                try
                {
                    SupportedPids = session.ReadSupportedPids();
                    if (SupportedPids != null)
                    {
                        Console.WriteLine($"The engine reports {SupportedPids.Count} standard values; the others are hidden.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Live data stopped: {ex.Message}");
                    polling = false;
                    return;
                }

                // values the car refused are asked again only every 20 rounds
                var refused = new Dictionary<string, int>();
                int round = 0;
                while (polling && session.IsOpen)
                {
                    var started = DateTime.UtcNow;
                    var due = new List<LiveValue>();
                    foreach (var value in AllValues.ToList())
                    {
                        if (value.IsComputed || !Supported(value))
                        {
                            continue;
                        }
                        if (refused.TryGetValue(value.Id, out int at) && (round - at) % 20 != 0)
                        {
                            continue;
                        }
                        // inputs of a watched computed value count as watched
                        bool watched = Watched(value) || AllValues.Any(c => c.IsComputed && Watched(c) && c.Inputs.Contains(value.Id));
                        if (watched || round % SlowEvery == 0 || Recorder != null)
                        {
                            due.Add(value);
                        }
                    }
                    try
                    {
                        var batch = session.ReadLiveBatch(due);
                        foreach (var (value, record) in batch)
                        {
                            Values[value.Id] = value.Decode(record);
                            refused.Remove(value.Id);
                            Problems.TryRemove(value.Id, out _);
                        }
                        foreach (var value in due.Where(v => !batch.ContainsKey(v)))
                        {
                            if (!polling)
                            {
                                break;
                            }
                            byte[] record = session.ReadLive(value, out var response);
                            if (record is null)
                            {
                                refused[value.Id] = refused.TryGetValue(value.Id, out int r) ? r : round;
                                Problems[value.Id] = response.Describe();
                            }
                            else
                            {
                                refused.Remove(value.Id);
                                Problems.TryRemove(value.Id, out _);
                            }
                            Values[value.Id] = value.Decode(record);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Live data stopped: {ex.Message}");
                        break;
                    }
                    foreach (var value in AllValues.Where(v => v.IsComputed))
                    {
                        Values[value.Id] = Calculate(value);
                    }
                    SampleVersion++;
                    round++;
                    RoundMs = (float)(DateTime.UtcNow - started).TotalMilliseconds;
                    int wait = (int)(Settings.LiveInterval * 1000 - RoundMs);
                    if (wait > 0)
                    {
                        Thread.Sleep(wait);
                    }
                }
                polling = false;
            }) { IsBackground = true, Name = "Live data" };
            Poller.Start();
        }

        double? Calculate(LiveValue value)
        {
            var inputs = new Dictionary<string, double>();
            foreach (string id in value.Inputs)
            {
                if (!Values.TryGetValue(id, out var v) || v is null)
                {
                    return null;
                }
                inputs[id] = v.Value;
            }
            return value.Compute(id => inputs[id]);
        }

        public void Stop()
        {
            polling = false;
            Poller?.Join(3000);
            Poller = null;
            StopRecording();
        }

        void RecordHistory()
        {
            if (SampleVersion == LastSampleVersion)
            {
                return;
            }
            LastSampleVersion = SampleVersion;
            foreach (var value in AllValues)
            {
                if (!Values.TryGetValue(value.Id, out var v) || v is null)
                {
                    continue;
                }
                if (!History.TryGetValue(value.Id, out var track))
                {
                    History[value.Id] = track = new Track();
                }
                track.Add(v.Value);
            }
            WriteRecordRow();
        }

        // ---------------------------------------------------------------- recording

        void StartRecording()
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "G-Diag logs");
            try
            {
                Directory.CreateDirectory(folder);
                RecordPath = Path.Combine(folder, $"live-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv");
                Recorder = new StreamWriter(RecordPath);
                RecordStarted = DateTime.Now;
                RecordColumns = AllValues.Where(Supported).ToList();
                RecordUs = Settings.UsUnits;
                Recorder.WriteLine("time_s," + string.Join(",", RecordColumns.Select(v => Csv($"{v.Name} ({LiveData.ToDisplayUnits(0, v.Unit, RecordUs).unit})"))));
                Console.WriteLine($"Recording live data to {RecordPath}");
            }
            catch (Exception ex)
            {
                Recorder = null;
                App.ShowMessage("Could not start recording", ex.Message);
            }
        }

        void WriteRecordRow()
        {
            if (Recorder is null)
            {
                return;
            }
            var cells = RecordColumns.Select(v => Values.TryGetValue(v.Id, out var x) && x is double d ? LiveData.ToDisplayUnits(d, v.Unit, RecordUs).value.ToString("0.###", CultureInfo.InvariantCulture) : "");
            Recorder.WriteLine((DateTime.Now - RecordStarted).TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture) + "," + string.Join(",", cells));
        }

        void StopRecording()
        {
            if (Recorder is null)
            {
                return;
            }
            Recorder.Dispose();
            Recorder = null;
            Console.WriteLine($"Saved the recording: {RecordPath}");
        }

        static string Csv(string text) => text.Contains(',') || text.Contains('"') ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

        // ---------------------------------------------------------------- drawing

        public void Draw()
        {
            if (App.Session is null)
            {
                if (polling)
                {
                    Stop();
                }
                Dim("Connect to a car first.");
                return;
            }
            RecordHistory();
            DrawToolbar();
            ImGui.Spacing();

            var gauges = Settings.LiveGauges.Select(id => AllValues.FirstOrDefault(v => v.Id == id)).Where(v => v != null).ToList();
            if (gauges.Count == 0)
            {
                Dim($"No gauges picked. Tick {Icons.Gauge} next to a value in the table to show it here.");
            }
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float cardWidth = S(190);
            int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cardWidth + spacing)));
            perRow = Math.Min(perRow, Math.Max(1, gauges.Count));
            cardWidth = (ImGui.GetContentRegionAvail().X - spacing * (perRow - 1)) / perRow;
            cardWidth = Math.Min(cardWidth, S(320));
            for (int i = 0; i < gauges.Count; i++)
            {
                if (i % perRow != 0)
                {
                    ImGui.SameLine();
                }
                DrawGauge(gauges[i], cardWidth);
            }

            var charts = Settings.LiveCharts.Select(id => AllValues.FirstOrDefault(v => v.Id == id)).Where(v => v != null).ToList();
            if (charts.Count > 0)
            {
                ImGui.Spacing();
                SectionTitle(Icons.Chart, "Charts");
                ImGui.SameLine();
                ImGui.TextDisabled($"last {HistoryLength} readings");
                foreach (var value in charts)
                {
                    DrawChart(value);
                }
            }

            ImGui.Spacing();
            SectionTitle(Icons.Pulse, "All values");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(200));
            ImGui.InputTextWithHint("##livefilter", $"{Icons.Search} filter", ref Filter, 64);
            ImGui.SameLine();
            ImGui.Checkbox("Show values this car doesn't have", ref ShowUnsupported);
            DrawTable();
            ImGui.Spacing();
            DrawAddForm();
        }

        void DrawToolbar()
        {
            if (!polling)
            {
                if (PrimaryButton($"{Icons.Play}  Start live data", Theme.Good, default, !App.Busy))
                {
                    Start();
                }
                Tooltip("Reads the values over and over. Other tasks wait while it runs.");
            }
            else if (ColoredButton($"{Icons.Stop}  Stop", Theme.Bad))
            {
                Stop();
            }
            ImGui.SameLine();
            if (Recorder is null)
            {
                if (ColoredButton($"{Icons.Pulse}  Record", Theme.Accent2, default, polling))
                {
                    StartRecording();
                }
                Tooltip("Saves every reading of every value to a CSV file (Documents/G-Diag logs) for Excel, Numbers or a log viewer.");
            }
            else
            {
                if (ColoredButton($"{Icons.Stop}  Stop recording", Theme.Warn))
                {
                    StopRecording();
                }
                Tooltip(RecordPath);
                ImGui.SameLine();
                StatusDot(Theme.Bad, true);
                ImGui.TextUnformatted($"{(DateTime.Now - RecordStarted):mm\\:ss}");
            }
            ImGui.SameLine();
            // Metric / US switch: the car always sends metric, this only changes what is shown (and recorded)
            int units = Settings.UsUnits ? 1 : 0;
            ImGui.SetNextItemWidth(S(150));
            if (ImGui.Combo("##units", ref units, "Metric\0US units\0"))
            {
                Settings.UsUnits = units == 1;
                Settings.Save();
            }
            Tooltip("°C → °F, km/h → mph, bar and kPa → psi, km → miles, Nm → lb-ft, kW → hp, l/h → gal/h, g/s → lb/min.\nThe car always reports metric; this only changes how it is shown.");
            ImGui.SameLine();
            if (ImGui.Button($"{Icons.Refresh} Reset min/max"))
            {
                History.Clear();
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(220));
            float interval = Settings.LiveInterval;
            if (ImGui.SliderFloat("##interval", ref interval, 0f, 2f, interval < 0.05f ? "pause: none" : "pause: %.1f s"))
            {
                Settings.LiveInterval = interval;
                Settings.Save();
            }
            Tooltip("How long to wait between rounds of readings. 0 reads as fast as the car answers.");
            if (polling)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"{(RoundMs > 0 ? 1000f / Math.Max(RoundMs, Settings.LiveInterval * 1000) : 0):0.0} updates/s");
            }
            Dim(App.Session?.Uds.Transport is GDiag.Net.DCanTransport
                ? "Engine values come from the DME (0x12). If it refuses the BMW request, they are read as plain OBD-II (7E0/7E8) instead."
                : "Engine values come from the DME (0x12) as standard OBD values. Not every DME answers them over ENET.");
        }

        (double value, string unit) Display(LiveValue value, double raw) => LiveData.ToDisplayUnits(raw, value.Unit, Settings.UsUnits);

        string Format(LiveValue value, double display)
        {
            int decimals = value.Decimals;
            // a converted value (°F, psi) keeps at least one decimal when it is small
            if (Settings.UsUnits && value.Unit == "bar")
            {
                decimals = 1;
            }
            return display.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        void DrawGauge(LiveValue value, float width)
        {
            float radius = width * 0.30f;
            float height = S(12) + radius * 2 + S(6) + ImGui.GetTextLineHeightWithSpacing() * 2 + ImGui.GetStyle().WindowPadding.Y * 2;
            BeginCard($"g{value.Id}", new Vector2(width, height));
            Values.TryGetValue(value.Id, out var raw);
            var (display, unit) = raw is double r ? Display(value, r) : (0, Display(value, 0).unit);
            var (min, _) = Display(value, value.Min);
            var (max, _) = Display(value, value.Max);

            var pos = ImGui.GetCursorScreenPos();
            var center = pos + new Vector2(width * 0.5f - ImGui.GetStyle().WindowPadding.X, radius + S(12));
            double? fraction = raw is null ? null : (display - min) / Math.Max(1e-6, max - min);
            var color = fraction > 0.9 ? Theme.Bad : fraction > 0.75 ? Theme.Warn : Theme.Accent;
            RadialGauge(center, radius, fraction, color, null);

            // peak marker
            if (History.TryGetValue(value.Id, out var track) && track.Count > 0)
            {
                var (peak, _) = Display(value, track.Max);
                float f = (float)Math.Clamp((peak - min) / Math.Max(1e-6, max - min), 0, 1);
                float a = MathF.PI * 0.75f + MathF.PI * 1.5f * f;
                var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
                ImGui.GetWindowDrawList().AddLine(center + dir * radius * 0.78f, center + dir * radius * 1.12f, Theme.U32(Theme.Warn), S(2.5f));
            }

            string text = raw is null ? "-" : Format(value, display);
            Fonts.Push(Fonts.DisplaySmall);
            var size = ImGui.CalcTextSize(text);
            ImGui.GetWindowDrawList().AddText(center - size * 0.5f, Theme.U32(Theme.Text), text);
            Fonts.Pop();
            var unitSize = ImGui.CalcTextSize(unit);
            ImGui.GetWindowDrawList().AddText(center + new Vector2(-unitSize.X * 0.5f, size.Y * 0.5f), Theme.U32(Theme.Dim), unit);

            ImGui.SetCursorScreenPos(new Vector2(pos.X, center.Y + radius + S(6)));
            var labelSize = ImGui.CalcTextSize(value.Name);
            ImGui.SetCursorPosX(Math.Max(ImGui.GetStyle().WindowPadding.X, (width - labelSize.X) * 0.5f));
            ImGui.TextUnformatted(value.Name);
            if (track != null && track.Count > 0)
            {
                string range = $"{Format(value, Display(value, track.Min).value)} - {Format(value, Display(value, track.Max).value)}";
                var rangeSize = ImGui.CalcTextSize(range);
                ImGui.SetCursorPosX(Math.Max(ImGui.GetStyle().WindowPadding.X, (width - rangeSize.X) * 0.5f));
                ImGui.TextDisabled(range);
            }
            ImGui.SetCursorScreenPos(pos);
            ImGui.InvisibleButton($"gauge{value.Id}", new Vector2(width - ImGui.GetStyle().WindowPadding.X * 2, height - ImGui.GetStyle().WindowPadding.Y * 2));
            string problem = Problems.TryGetValue(value.Id, out string p) ? $"\n\nNot available: {p}" : !Supported(value) ? "\n\nThis car's engine doesn't report this value." : "";
            Tooltip($"{value.Name}{(value.Help != null ? "\n" + value.Help : "")}{problem}\n\nRight-click to remove.");
            if (ImGui.BeginPopupContextItem($"gctx{value.Id}"))
            {
                if (ImGui.MenuItem("Remove gauge"))
                {
                    Toggle(Settings.LiveGauges, value.Id);
                }
                if (ImGui.MenuItem(Settings.LiveCharts.Contains(value.Id) ? "Remove chart" : "Show as chart"))
                {
                    Toggle(Settings.LiveCharts, value.Id);
                }
                ImGui.EndPopup();
            }
            EndCard(Theme.Accent);
        }

        void DrawChart(LiveValue value)
        {
            float height = S(92);
            BeginCard($"c{value.Id}", new Vector2(-1, height));
            History.TryGetValue(value.Id, out var track);
            Values.TryGetValue(value.Id, out var raw);
            float labelWidth = S(220);
            Fonts.Push(Fonts.Semibold);
            ImGui.TextUnformatted(value.Name);
            Fonts.Pop();
            string unit = Display(value, 0).unit;
            Fonts.Push(Fonts.DisplaySmall);
            ImGui.TextUnformatted(raw is double r ? Format(value, Display(value, r).value) : "-");
            Fonts.Pop();
            ImGui.SameLine();
            ImGui.TextDisabled(unit);
            if (track != null && track.Count > 0)
            {
                ImGui.TextDisabled($"min {Format(value, Display(value, track.Min).value)}  max {Format(value, Display(value, track.Max).value)}");
            }

            float top = ImGui.GetWindowPos().Y + ImGui.GetStyle().WindowPadding.Y;
            float left = ImGui.GetWindowPos().X + labelWidth;
            float chartWidth = ImGui.GetWindowWidth() - labelWidth - ImGui.GetStyle().WindowPadding.X - S(60);
            float chartHeight = height - ImGui.GetStyle().WindowPadding.Y * 2;
            if (track != null && track.Count > 1 && chartWidth > S(50))
            {
                var (lo, hi) = track.Range();
                float pad = Math.Max((hi - lo) * 0.1f, 1e-3f);
                lo -= pad;
                hi += pad;
                var draw = ImGui.GetWindowDrawList();
                for (int i = 0; i <= 2; i++)
                {
                    float y = top + chartHeight * i / 2f;
                    draw.AddLine(new Vector2(left, y), new Vector2(left + chartWidth, y), Theme.U32(Theme.WithAlpha(Theme.Dim, 0.15f)));
                    double level = hi - (hi - lo) * i / 2.0;
                    draw.AddText(new Vector2(left + chartWidth + S(6), y - ImGui.GetTextLineHeight() * 0.5f), Theme.U32(Theme.Dim),
                        Format(value, Display(value, level).value));
                }
                // the track is metric; the chart is drawn in the same space and labelled in display units
                var data = new float[track.Count];
                for (int i = 0; i < track.Count; i++)
                {
                    data[i] = track.Data[(track.Offset + i) % HistoryLength];
                }
                ImGui.SetCursorScreenPos(new Vector2(left, top));
                Sparkline($"chart{value.Id}", data, data.Length, 0, lo, hi, Theme.Accent, new Vector2(chartWidth * data.Length / HistoryLength, chartHeight));
            }
            EndCard(Theme.Accent);
            if (ImGui.BeginPopupContextItem($"cctx{value.Id}"))
            {
                if (ImGui.MenuItem("Remove chart"))
                {
                    Toggle(Settings.LiveCharts, value.Id);
                }
                ImGui.EndPopup();
            }
        }

        static void Toggle(List<string> list, string id)
        {
            if (!list.Remove(id))
            {
                list.Add(id);
            }
            Settings.Save();
        }

        void DrawTable()
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH;
            if (!ImGui.BeginTable("live", 8, flags))
            {
                return;
            }
            ImGui.TableSetupColumn(Icons.Gauge, ImGuiTableColumnFlags.WidthFixed, S(28));
            ImGui.TableSetupColumn(Icons.Chart, ImGuiTableColumnFlags.WidthFixed, S(28));
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Reading", ImGuiTableColumnFlags.WidthFixed, S(130));
            ImGui.TableSetupColumn("Min / max", ImGuiTableColumnFlags.WidthFixed, S(150));
            ImGui.TableSetupColumn("History", ImGuiTableColumnFlags.WidthFixed, S(180));
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, S(130));
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, S(70));
            ImGui.TableHeadersRow();
            LiveValue remove = null;
            string group = null;
            foreach (var value in AllValues)
            {
                bool supported = Supported(value);
                if (!supported && !ShowUnsupported)
                {
                    continue;
                }
                if (Filter.Length > 0 && !value.Name.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (value.Group != group)
                {
                    group = value.Group;
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(2);
                    Fonts.Push(Fonts.Semibold);
                    Colored(Theme.Accent, group);
                    Fonts.Pop();
                }
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                bool gauge = Settings.LiveGauges.Contains(value.Id);
                if (ImGui.Checkbox($"##g{value.Id}", ref gauge))
                {
                    Toggle(Settings.LiveGauges, value.Id);
                }
                Tooltip("Show as a gauge");
                ImGui.TableNextColumn();
                bool chart = Settings.LiveCharts.Contains(value.Id);
                if (ImGui.Checkbox($"##c{value.Id}", ref chart))
                {
                    Toggle(Settings.LiveCharts, value.Id);
                }
                Tooltip("Show as a chart");
                ImGui.TableNextColumn();
                if (supported)
                {
                    ImGui.TextUnformatted(value.Name);
                }
                else
                {
                    ImGui.TextDisabled(value.Name);
                }
                if (value.Help != null)
                {
                    Tooltip(value.Help);
                }
                ImGui.TableNextColumn();
                Values.TryGetValue(value.Id, out var raw);
                if (raw is double r)
                {
                    var (display, unit) = Display(value, r);
                    Fonts.Push(Fonts.Semibold);
                    ImGui.TextUnformatted($"{Format(value, display)} {unit}");
                    Fonts.Pop();
                }
                else if (!supported)
                {
                    ImGui.TextDisabled("not on this car");
                }
                else if (Problems.TryGetValue(value.Id, out string problem))
                {
                    ImGui.TextDisabled("not available");
                    Tooltip(problem);
                }
                else
                {
                    ImGui.TextDisabled("-");
                }
                ImGui.TableNextColumn();
                History.TryGetValue(value.Id, out var track);
                if (track != null && track.Count > 0)
                {
                    ImGui.TextDisabled($"{Format(value, Display(value, track.Min).value)} / {Format(value, Display(value, track.Max).value)}");
                }
                ImGui.TableNextColumn();
                if (track != null && track.Count > 1)
                {
                    var (lo, hi) = track.Range();
                    int shown = Math.Min(track.Count, 120);
                    var data = new float[shown];
                    for (int i = 0; i < shown; i++)
                    {
                        data[i] = track.Data[(track.Offset + track.Count - shown + i) % HistoryLength];
                    }
                    Sparkline($"s{value.Id}", data, shown, 0, lo, hi + (hi - lo < 1e-3f ? 1 : 0), Theme.Accent, new Vector2(S(170), ImGui.GetTextLineHeight()));
                }
                ImGui.TableNextColumn();
                Fonts.Push(Fonts.Mono);
                if (value.IsComputed)
                {
                    ImGui.TextDisabled("calculated");
                }
                else if (value.IsObd)
                {
                    ImGui.TextDisabled($"OBD PID {value.Did & 0xFF:X2}");
                    Tooltip($"Read as 22 {value.Did >> 8:X2} {value.Did & 0xFF:X2} from 0x{value.Module:X2}, or OBD-II mode 01 PID {value.Did & 0xFF:X2}");
                }
                else
                {
                    ImGui.TextDisabled($"{value.Module:X2} / {value.Did:X4}");
                }
                Fonts.Pop();
                ImGui.TableNextColumn();
                if (value.Custom && ImGui.SmallButton($"Remove##{value.Id}"))
                {
                    remove = value;
                }
            }
            ImGui.EndTable();
            if (remove != null)
            {
                CustomValues = CustomValues.Where(v => v != remove).ToList();
                Settings.CustomDids = CustomValues.Select(v => v.Serialize()).ToList();
                Settings.Save();
            }
        }

        void DrawAddForm()
        {
            if (!ImGui.CollapsingHeader("Add a value by DID"))
            {
                return;
            }
            Dim("For values you know the identifier of (from ISTA, EDIABAS job traces or a forum). Reading is safe: it only uses service 22.");
            float w = S(110);
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("Module (hex)", ref NewModule, 4);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("DID (hex)", ref NewDid, 6);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(220));
            ImGui.InputText("Name", ref NewName, 64);
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("Scale", ref NewScale, 16);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("Offset", ref NewOffset, 16);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(w);
            ImGui.InputText("Unit", ref NewUnit, 12);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(w);
            ImGui.SliderInt("Bytes", ref NewBytes, 1, 4);
            ImGui.SameLine();
            ImGui.Checkbox("Signed", ref NewSigned);

            bool valid = Hex.TryParseNumber(NewModule, out uint module) && module <= 0xFF
                && Hex.TryParseNumber(NewDid, out uint did) && did <= 0xFFFF
                && double.TryParse(NewScale, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && double.TryParse(NewOffset, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (Button($"{Icons.Add} Add", valid, "Module and DID are hex numbers, scale and offset decimal numbers"))
            {
                Hex.TryParseNumber(NewModule, out module);
                Hex.TryParseNumber(NewDid, out did);
                string name = NewName.Trim().Length > 0 ? NewName.Trim() : $"DID {did:X4}";
                var value = new LiveValue
                {
                    Module = (byte)module,
                    Did = (ushort)did,
                    Name = name,
                    Scale = double.Parse(NewScale, CultureInfo.InvariantCulture),
                    Offset = double.Parse(NewOffset, CultureInfo.InvariantCulture),
                    Unit = NewUnit.Trim(),
                    ByteCount = NewBytes,
                    Signed = NewSigned,
                };
                string serialized = value.Serialize();
                Settings.CustomDids.Add(serialized);
                Settings.Save();
                CustomValues = LiveData.Custom(Settings.CustomDids);
                NewDid = "";
                NewName = "";
            }
        }
    }
}
