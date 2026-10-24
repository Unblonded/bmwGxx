using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using GDiag.Translation;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Paste coding notes (an E-Sys FDL tree or a cheat-sheet list) and read them in English.
    // Read only on purpose: changing coding safely needs the CAFD and E-Sys' checks.
    public class CodingTab
    {
        readonly App App;
        string Input = Sample;
        List<CodingLine> Lines;
        string LastExplained;
        bool ShowOriginal = true;

        const string Sample =
@"# Paste lines from E-Sys or a coding list here, for example:
HU_MGU > 3000 Exbox > VIDEO_FAHRT_ERLAUBT = aktiv
BDC_BODY > 3002 KomfortSchliessen > SPIEGEL_ANKLAPPEN_FB = aktiv
BDC_BODY > TFL_NSW_DIMMUNG = nicht_aktiv
KOMBI > GURTWARNUNG_GONG = nicht_aktiv
3000 Exbox
    MSA_LETZTER_ZUSTAND
        wert_01";

        public CodingTab(App app)
        {
            App = app;
        }

        public void Draw()
        {
            SectionTitle(Icons.Code, "Coding explainer");
            Help("BMW coding names (FDL parameters) are German abbreviations. This translates them so you can tell " +
                 "what a line in a coding guide or E-Sys does. Nothing is sent to the car.");
            if (Button($"{Icons.Open} Load text file...", true))
            {
                string path = FileDialogs.OpenFile("Coding notes or an FDL text export", "Text files (*.txt)|*.txt|All files (*.*)|*.*");
                if (path != null)
                {
                    try
                    {
                        Input = File.ReadAllText(path);
                    }
                    catch (Exception ex)
                    {
                        App.ShowMessage("Could not open the file", ex.Message);
                    }
                }
            }
            ImGui.SameLine();
            if (ImGui.Button($"{Icons.Close} Clear"))
            {
                Input = "";
            }
            ImGui.SameLine();
            ImGui.Checkbox("Show the original next to it", ref ShowOriginal);

            ImGui.InputTextMultiline("##coding", ref Input, 512 * 1024, new Vector2(-1, ImGui.GetTextLineHeight() * 8));
            if (Input != LastExplained)
            {
                LastExplained = Input;
                Lines = CodingExplainer.Explain(Input);
            }

            if (!AppSettings.Current.TranslateToEnglish)
            {
                Colored(Theme.Warn, "Translation is switched off in Settings.");
                return;
            }
            ImGui.Spacing();
            int columns = ShowOriginal ? 3 : 2;
            if (!ImGui.BeginTable("explained", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable))
            {
                return;
            }
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Kind", ImGuiTableColumnFlags.WidthFixed, S(90));
            if (ShowOriginal)
            {
                ImGui.TableSetupColumn("Original", ImGuiTableColumnFlags.WidthStretch);
            }
            ImGui.TableSetupColumn("In English", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (var line in Lines.Where(l => l.Kind != CodingLineKind.Blank))
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                var (label, color) = KindLabel(line.Kind);
                Pill(label, color);
                string indent = new string(' ', Math.Min(line.Indent, 24));
                if (ShowOriginal)
                {
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Mono);
                    ImGui.TextDisabled(indent + line.Original.Trim());
                    Fonts.Pop();
                }
                ImGui.TableNextColumn();
                if (line.Kind == CodingLineKind.Comment)
                {
                    ImGui.TextDisabled(line.English);
                }
                else
                {
                    ImGui.TextWrapped(indent + line.English);
                }
            }
            ImGui.EndTable();
        }

        static (string, Vector4) KindLabel(CodingLineKind kind)
        {
            switch (kind)
            {
                case CodingLineKind.Module: return ("module", Theme.Accent2);
                case CodingLineKind.Block: return ("group", Theme.Accent);
                case CodingLineKind.Parameter: return ("setting", Theme.Good);
                case CodingLineKind.Value: return ("value", Theme.Warn);
                case CodingLineKind.Path: return ("change", Theme.Modified);
                default: return ("note", Theme.Dim);
            }
        }
    }
}
