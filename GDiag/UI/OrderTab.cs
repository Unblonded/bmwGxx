using System;
using System.IO;
using System.Linq;
using GDiag.Bmw;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Shows a vehicle order (FA) in plain English: what car it is and which options it was built with.
    public class OrderTab
    {
        readonly App App;
        VehicleOrder Order;
        string Source = "";
        string Pasted = "";
        string Error;

        static AppSettings Settings => AppSettings.Current;

        public OrderTab(App app)
        {
            App = app;
            if (File.Exists(Settings.LastFaFile))
            {
                Load(Settings.LastFaFile);
            }
        }

        public void OpenDialog()
        {
            string path = FileDialogs.OpenFile("Vehicle order (FA) exported from E-Sys or ISTA", "FA files (*.xml;*.txt)|*.xml;*.txt|All files (*.*)|*.*");
            if (path != null)
            {
                Load(path);
            }
        }

        void Load(string path)
        {
            try
            {
                Order = VehicleOrder.Parse(File.ReadAllText(path));
                Source = path;
                Error = null;
                Settings.LastFaFile = path;
                Settings.Save();
                Console.WriteLine($"Vehicle order loaded from {path}: {Order.Series} with {Order.Options.Count} options");
            }
            catch (Exception ex)
            {
                Error = $"Could not read {Path.GetFileName(path)}: {ex.Message}";
            }
        }

        public void Draw()
        {
            SectionTitle(Icons.Key, "Vehicle order (FA)");
            Help("The FA is the build sheet the car's modules are coded from. Export it with E-Sys " +
                 "(Expert mode > VCM > Read, then save the FA) or paste the FA text from ISTA.");
            if (PrimaryButton($"{Icons.Open}  Open FA file...", Theme.Accent))
            {
                OpenDialog();
            }
            ImGui.SameLine();
            Dim("or paste it below");
            ImGui.InputTextMultiline("##fa", ref Pasted, 64 * 1024, new System.Numerics.Vector2(-S(130), ImGui.GetTextLineHeight() * 3.5f));
            ImGui.SameLine();
            if (Button("Explain", Pasted.Trim().Length > 0, "Paste an FA first"))
            {
                try
                {
                    Order = VehicleOrder.Parse(Pasted);
                    Source = "pasted text";
                    Error = null;
                }
                catch (Exception ex)
                {
                    Error = ex.Message;
                }
            }
            if (Error != null)
            {
                Colored(Theme.Bad, Error);
            }
            if (Order is null)
            {
                return;
            }

            ImGui.Spacing();
            Dim($"From {Source}");
            if (ImGui.BeginTable("fa", 2, ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("k", ImGuiTableColumnFlags.WidthFixed, S(180));
                ImGui.TableSetupColumn("v", ImGuiTableColumnFlags.WidthStretch);
                Row("Series", Order.SeriesName.Length > 0 ? $"{Order.Series}  ({Order.SeriesName})" : Order.Series);
                Row("Type key", Order.TypeKey);
                Row("Built", Order.BuildDateText);
                Row("Paint code", Order.Paint);
                Row("Upholstery code", Order.Upholstery);
                Row("VIN", Order.Vin);
                Row("E-words", string.Join(" ", Order.EWords));
                Row("HO-words", string.Join(" ", Order.HoWords));
                ImGui.EndTable();
            }
            if (App.Session != null && Order.Vin.Length == 17 && App.Session.Vin.Length == 17 && Order.Vin != App.Session.Vin)
            {
                Colored(Theme.Warn, $"{Icons.Warning} This FA belongs to {Order.Vin}, not the connected car ({App.Session.Vin}).");
            }

            ImGui.Spacing();
            int known = Order.Options.Count(o => VehicleOrder.OptionName(o) != null);
            SectionTitle(Icons.Check, $"Options ({Order.Options.Count}, {known} with a built-in name)");
            if (ImGui.BeginTable("options", 2, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
            {
                ImGui.TableSetupColumn("code", ImGuiTableColumnFlags.WidthFixed, S(90));
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthStretch);
                foreach (string option in Order.Options)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Mono);
                    ImGui.TextUnformatted(option);
                    Fonts.Pop();
                    ImGui.TableNextColumn();
                    string name = VehicleOrder.OptionName(option);
                    if (name != null)
                    {
                        ImGui.TextUnformatted(name);
                    }
                    else
                    {
                        ImGui.TextDisabled("look up in the parts catalogue (ETK)");
                    }
                }
                ImGui.EndTable();
            }
        }

        static void Row(string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled(key);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(value);
        }
    }
}
