using System;
using System.Linq;
using System.Numerics;
using GDiag.Bmw;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // The car at a glance and every control unit that answered the scan, with what each one is for.
    public class VehicleTab
    {
        readonly App App;
        byte? Selected;

        public VehicleTab(App app)
        {
            App = app;
        }

        public void Draw()
        {
            var session = App.Session;
            if (session is null)
            {
                Empty();
                return;
            }

            DrawSummary(session);
            ImGui.Spacing();

            var modules = session.ModulesSnapshot();
            if (modules.Count == 0)
            {
                Dim(App.Busy ? "Scanning..." : "No control units yet. Press Scan again.");
                return;
            }
            Selected ??= modules[0].Address;

            float listWidth = ImGui.GetContentRegionAvail().X * 0.55f;
            ImGui.BeginChild("modules", new Vector2(listWidth, 0), ImGuiChildFlags.Border);
            SectionTitle(Icons.Chip, $"Control units ({modules.Count})");
            ImGui.SameLine(ImGui.GetWindowWidth() - S(210));
            if (Button($"{Icons.Refresh} Read all details", !App.Busy, "Wait for the current task"))
            {
                App.RunBackground("Reading module details", () =>
                {
                    foreach (var m in modules)
                    {
                        App.Progress(m.Info.Short);
                        session.ReadIdentification(m);
                    }
                });
            }
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (ImGui.BeginTable("moduletable", 4, flags))
            {
                ImGui.TableSetupScrollFreeze(0, 1);
                ImGui.TableSetupColumn("Addr", ImGuiTableColumnFlags.WidthFixed, S(56));
                ImGui.TableSetupColumn("Module", ImGuiTableColumnFlags.WidthFixed, S(80));
                ImGui.TableSetupColumn("What it does", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Coding data (CAFD)", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();
                foreach (var m in modules)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (ImGui.Selectable($"0x{m.Address:X2}##m{m.Address}", Selected == m.Address, ImGuiSelectableFlags.SpanAllColumns))
                    {
                        Selected = m.Address;
                    }
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Semibold);
                    ImGui.TextUnformatted(m.Info.Short);
                    Fonts.Pop();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(m.Info.Name);
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Mono);
                    ImGui.TextDisabled(m.Cafd.Length > 0 ? m.Cafd : "-");
                    Fonts.Pop();
                }
                ImGui.EndTable();
            }
            ImGui.EndChild();

            ImGui.SameLine();
            ImGui.BeginChild("details", Vector2.Zero, ImGuiChildFlags.Border);
            var selected = modules.FirstOrDefault(m => m.Address == Selected);
            if (selected != null)
            {
                DrawDetails(session, selected);
            }
            ImGui.EndChild();
        }

        static void Empty()
        {
            ImGui.Spacing();
            BigText("Connect to a car to see its control units");
            ImGui.Spacing();
            Dim("With a Tactrix OpenPort 2.0 (or another J2534 adapter):");
            ImGui.BulletText(OperatingSystem.IsMacOS()
                ? "Install its driver: brew install bisak/tap/openport-j2534, then press Refresh in step 1."
                : "Install Tactrix's J2534 driver, then press Refresh in step 1.");
            ImGui.BulletText("Plug it into the OBD port. It talks BMW D-CAN on pins 6 and 14 at 500 kbit/s.");
            ImGui.BulletText("Ignition on (press Start without the brake), a battery charger if you'll be a while.");
            ImGui.Spacing();
            Dim("Or with an ENET cable:");
            ImGui.BulletText("Choose ENET cable in step 1. G series cars need the activation resistor between OBD pins 8 and 16.");
            ImGui.BulletText("Leave the network adapter on automatic: the computer and the car pick 169.254.x.x addresses.");
            ImGui.Spacing();
            Dim("Then press Connect in step 2, or try the demo car.");
        }

        static void DrawSummary(CarSession session)
        {
            string vin = session.Vin;
            BeginCard("summary", new Vector2(0, ImGui.GetTextLineHeightWithSpacing() * 2.6f + ImGui.GetStyle().WindowPadding.Y * 2));
            IconBadge(Icons.Car, Theme.Accent, S(52));
            ImGui.SameLine();
            ImGui.BeginGroup();
            Fonts.Push(Fonts.DisplaySmall);
            ImGui.TextUnformatted(string.IsNullOrEmpty(vin) ? "VIN not reported" : vin);
            Fonts.Pop();
            if (!string.IsNullOrEmpty(vin))
            {
                int? year = Vin.ModelYear(vin);
                Dim($"{Vin.Maker(vin)}   ·   model year {(year?.ToString() ?? "?")}   ·   short VIN {Vin.Short(vin)}");
            }
            ImGui.EndGroup();
            EndCard();
        }

        void DrawDetails(CarSession session, ModuleState m)
        {
            Fonts.Push(Fonts.Title);
            ImGui.TextUnformatted($"{m.Info.Short}");
            Fonts.Pop();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            Dim($"{m.Info.Name}, address 0x{m.Address:X2}");
            ImGui.TextWrapped(m.Info.Description);
            ImGui.Spacing();
            if (Button($"{Icons.Refresh} Read identification", !App.Busy, "Wait for the current task"))
            {
                App.RunBackground($"Reading {m.Info.Short}", () => session.ReadIdentification(m));
            }

            ImGui.Spacing();
            Heading("Identification");
            if (m.Ident.Count == 0)
            {
                Dim("Not read yet.");
            }
            else if (ImGui.BeginTable("ident", 2, ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("name", ImGuiTableColumnFlags.WidthFixed, S(170));
                ImGui.TableSetupColumn("value", ImGuiTableColumnFlags.WidthStretch);
                foreach (var (did, name, _) in LiveData.Identification)
                {
                    if (!m.Ident.TryGetValue(did, out string value))
                    {
                        continue;
                    }
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(name);
                    Tooltip($"DID 0x{did:X4}");
                    ImGui.TableNextColumn();
                    bool missing = value == "not supported" || value == "no answer";
                    if (missing)
                    {
                        ImGui.TextDisabled(value);
                    }
                    else
                    {
                        ImGui.TextUnformatted(value);
                    }
                }
                ImGui.EndTable();
            }

            ImGui.Spacing();
            Heading("Software and hardware parts (SVK)");
            Help("What is flashed into this unit. BMW names each part by type, number and version. " +
                 "The CAFD is the coding data that switches features on and off; E-Sys edits it.");
            if (m.Svk is null)
            {
                Dim("The unit did not report its parts list.");
                return;
            }
            if (ImGui.BeginTable("svk", 2, ImGuiTableFlags.RowBg))
            {
                ImGui.TableSetupColumn("part", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("meaning", ImGuiTableColumnFlags.WidthStretch);
                foreach (var part in m.Svk)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Mono);
                    ImGui.TextUnformatted(part.ToString());
                    Fonts.Pop();
                    ImGui.TableNextColumn();
                    ImGui.TextDisabled(Svk.ClassMeaning(part.ProcessClass));
                }
                ImGui.EndTable();
            }
        }
    }
}
