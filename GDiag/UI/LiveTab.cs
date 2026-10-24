using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading;
using GDiag.Bmw;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Engine readings as gauges, plus any DID the user adds, read again every few tenths of a second.
    public class LiveTab
    {
        const int HistoryLength = 120;

        readonly App App;
        readonly ConcurrentDictionary<string, double?> Values = new ConcurrentDictionary<string, double?>();
        readonly ConcurrentDictionary<string, string> Problems = new ConcurrentDictionary<string, string>();
        readonly Dictionary<string, (float[] data, int count, int offset)> History = new Dictionary<string, (float[], int, int)>();
        List<LiveValue> CustomValues;

        Thread Poller;
        volatile bool polling;
        int LastSampleVersion;
        volatile int SampleVersion;

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
        }

        public bool Polling => polling;

        IEnumerable<LiveValue> AllValues => LiveData.Engine.Concat(CustomValues);

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
                // values the DME refused are asked again only every 10 rounds
                var refused = new Dictionary<string, int>();
                int round = 0;
                while (polling && session.IsOpen)
                {
                    var started = DateTime.UtcNow;
                    foreach (var value in AllValues.ToList())
                    {
                        if (!polling)
                        {
                            break;
                        }
                        if (refused.TryGetValue(value.Id, out int at) && (round - at) % 10 != 0)
                        {
                            continue;
                        }
                        byte[] record;
                        try
                        {
                            record = session.ReadLive(value, out var response);
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
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Live data stopped: {ex.Message}");
                            polling = false;
                            break;
                        }
                        Values[value.Id] = value.Decode(record);
                    }
                    SampleVersion++;
                    round++;
                    int wait = (int)(Settings.LiveInterval * 1000 - (DateTime.UtcNow - started).TotalMilliseconds);
                    if (wait > 0)
                    {
                        Thread.Sleep(wait);
                    }
                }
                polling = false;
            }) { IsBackground = true, Name = "Live data" };
            Poller.Start();
        }

        public void Stop()
        {
            polling = false;
            Poller?.Join(3000);
            Poller = null;
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
                if (!History.TryGetValue(value.Id, out var h))
                {
                    h = (new float[HistoryLength], 0, 0);
                }
                if (h.count < HistoryLength)
                {
                    h.data[h.count++] = (float)v.Value;
                }
                else
                {
                    h.data[h.offset] = (float)v.Value;
                    h.offset = (h.offset + 1) % HistoryLength;
                }
                History[value.Id] = h;
            }
        }

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

            if (!polling)
            {
                if (PrimaryButton($"{Icons.Play}  Start live data", Theme.Good, default, !App.Busy))
                {
                    Start();
                }
                Tooltip("Reads the values below over and over. Other tasks wait while it runs.");
            }
            else if (ColoredButton($"{Icons.Stop}  Stop", Theme.Bad))
            {
                Stop();
            }
            ImGui.SameLine();
            Dim(App.Session?.Uds.Transport is GDiag.Net.DCanTransport
                ? "Engine values come from the DME (0x12). If it refuses the BMW request, they are read as plain OBD-II (7E0/7E8) instead."
                : "Engine values come from the DME (0x12) as standard OBD values. Not every DME answers them over ENET.");
            ImGui.Spacing();

            // gauges, as many per row as fit
            var gauges = LiveData.Engine.Take(8).ToList();
            float spacing = ImGui.GetStyle().ItemSpacing.X;
            float cardWidth = S(210);
            int perRow = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing) / (cardWidth + spacing)));
            cardWidth = (ImGui.GetContentRegionAvail().X - spacing * (perRow - 1)) / perRow;
            for (int i = 0; i < gauges.Count; i++)
            {
                if (i % perRow != 0)
                {
                    ImGui.SameLine();
                }
                DrawGauge(gauges[i], cardWidth);
            }

            ImGui.Spacing();
            SectionTitle(Icons.Chart, "All values");
            DrawTable();
            ImGui.Spacing();
            DrawAddForm();
        }

        void DrawGauge(LiveValue value, float width)
        {
            float height = width * 0.9f;
            BeginCard($"g{value.Id}", new Vector2(width, height));
            Values.TryGetValue(value.Id, out var raw);
            var (display, unit) = raw is double r ? LiveData.ToDisplayUnits(r, value.Unit, Settings.UsUnits) : (0, value.Unit);
            var (min, _) = LiveData.ToDisplayUnits(value.Min, value.Unit, Settings.UsUnits);
            var (max, _) = LiveData.ToDisplayUnits(value.Max, value.Unit, Settings.UsUnits);

            var pos = ImGui.GetCursorScreenPos();
            float radius = width * 0.30f;
            var center = pos + new Vector2(width * 0.5f - ImGui.GetStyle().WindowPadding.X, radius + S(12));
            double? fraction = raw is null ? null : (display - min) / Math.Max(1e-6, max - min);
            var color = fraction > 0.9 ? Theme.Bad : fraction > 0.75 ? Theme.Warn : Theme.Accent;
            RadialGauge(center, radius, fraction, color, null);

            string text = raw is null ? "-" : display.ToString(Math.Abs(display) >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture);
            Fonts.Push(Fonts.DisplaySmall);
            var size = ImGui.CalcTextSize(text);
            ImGui.GetWindowDrawList().AddText(center - size * 0.5f, Theme.U32(Theme.Text), text);
            Fonts.Pop();
            var unitSize = ImGui.CalcTextSize(unit);
            ImGui.GetWindowDrawList().AddText(center + new Vector2(-unitSize.X * 0.5f, size.Y * 0.5f), Theme.U32(Theme.Dim), unit);

            ImGui.SetCursorScreenPos(new Vector2(pos.X, center.Y + radius + S(6)));
            var labelSize = ImGui.CalcTextSize(value.Name);
            ImGui.SetCursorPosX((width - labelSize.X) * 0.5f);
            ImGui.TextUnformatted(value.Name);
            if (Problems.TryGetValue(value.Id, out string problem))
            {
                Tooltip(problem);
            }
            EndCard(Theme.Accent);
        }

        void DrawTable()
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH;
            if (!ImGui.BeginTable("live", 5, flags))
            {
                return;
            }
            ImGui.TableSetupColumn("Value", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Reading", ImGuiTableColumnFlags.WidthFixed, S(150));
            ImGui.TableSetupColumn("History", ImGuiTableColumnFlags.WidthFixed, S(220));
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, S(130));
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, S(70));
            ImGui.TableHeadersRow();
            LiveValue remove = null;
            foreach (var value in AllValues)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(value.Name);
                ImGui.TableNextColumn();
                Values.TryGetValue(value.Id, out var raw);
                if (raw is double r)
                {
                    var (display, unit) = LiveData.ToDisplayUnits(r, value.Unit, Settings.UsUnits);
                    Fonts.Push(Fonts.Semibold);
                    ImGui.TextUnformatted($"{display.ToString("0.##", CultureInfo.InvariantCulture)} {unit}");
                    Fonts.Pop();
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
                if (History.TryGetValue(value.Id, out var h) && h.count > 1)
                {
                    float lo = h.data.Take(h.count).Min();
                    float hi = h.data.Take(h.count).Max();
                    Sparkline($"s{value.Id}", h.data, h.count, h.offset, lo, hi + (hi - lo < 1e-3f ? 1 : 0), Theme.Accent, new Vector2(S(210), ImGui.GetTextLineHeight()));
                }
                ImGui.TableNextColumn();
                Fonts.Push(Fonts.Mono);
                ImGui.TextDisabled($"{value.Module:X2} / 22 {value.Did >> 8:X2} {value.Did & 0xFF:X2}");
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
