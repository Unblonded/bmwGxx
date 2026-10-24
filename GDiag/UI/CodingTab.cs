using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using GDiag.Bmw;
using GDiag.Translation;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Two parts:
    //  - Car coding: read the coding blocks of a module, save them, and compare with a saved copy to see exactly
    //    which bytes a BimmerCode or E-Sys session changed. Read only.
    //  - Explain text: paste coding notes (an E-Sys FDL tree or a cheat-sheet list) and read them in English.
    // Nothing here writes to the car on purpose: changing coding safely needs the CAFD and E-Sys' checks.
    public class CodingTab
    {
        readonly App App;
        string Input = Sample;
        List<CodingLine> Lines;
        string LastExplained;
        bool ShowOriginal = true;

        byte SelectedModule = 0x40;
        CodingBackup Comparison;
        string ComparisonPath;
        bool OnlyChanged = true;
        volatile bool CancelRead;

        const string Sample =
@"# Paste lines from E-Sys or a coding list here, for example:
HU_MGU > 3000 Exbox > VIDEO_FAHRT_ERLAUBT = aktiv
BDC_BODY > 3002 KomfortSchliessen > SPIEGEL_ANKLAPPEN_FB = aktiv
BDC_BODY > TFL_NSW_DIMMUNG = nicht_aktiv
KOMBI > GURTWARNUNG_GONG = nicht_aktiv
3000 Exbox
    MSA_LETZTER_ZUSTAND
        wert_01";

        static readonly Regex BlockRef = new Regex(@"\b(3[0-9A-F]{3})\s+([A-Za-z][A-Za-z0-9_]+)");

        public CodingTab(App app)
        {
            App = app;
        }

        public void Draw()
        {
            if (!ImGui.BeginTabBar("coding"))
            {
                return;
            }
            if (ImGui.BeginTabItem($"{Icons.Chip}  Car coding"))
            {
                ImGui.BeginChild("carcoding");
                DrawCarCoding();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem($"{Icons.Code}  Explain text"))
            {
                ImGui.BeginChild("explain");
                DrawExplainer();
                ImGui.EndChild();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        // ---------------------------------------------------------------- car coding

        void DrawCarCoding()
        {
            SectionTitle(Icons.Chip, "Coding in the car");
            Help("Every F/G series module keeps its coding in blocks numbered 3000-3FFF; those are the numbers " +
                 "in front of the block names in E-Sys and coding guides (\"3002 KomfortSchliessen\"). " +
                 "This reads them (service 22 only), saves them, and compares two readings byte by byte. " +
                 "Typical use: back up, code with BimmerCode or E-Sys, read again and see exactly what changed.");

            var session = App.Session;
            var modules = session?.ModulesSnapshot() ?? new List<ModuleState>();
            if (session is null)
            {
                Dim("Connect to a car to read its coding. Without a car you can still look at a saved backup.");
            }
            else if (modules.Count == 0)
            {
                Dim("Scan the car first (Vehicle tab).");
            }

            if (modules.Count > 0)
            {
                if (!modules.Any(m => m.Address == SelectedModule))
                {
                    SelectedModule = modules[0].Address;
                }
                var selected = modules.First(m => m.Address == SelectedModule);
                ImGui.SetNextItemWidth(S(320));
                if (ImGui.BeginCombo("##codingmodule", ModuleLabel(selected)))
                {
                    foreach (var m in modules)
                    {
                        if (ImGui.Selectable(ModuleLabel(m), m.Address == SelectedModule))
                        {
                            SelectedModule = m.Address;
                        }
                    }
                    ImGui.EndCombo();
                }
                ImGui.SameLine();
                if (PrimaryButton($"{Icons.Refresh}  Read coding", Theme.Accent, default, !App.Busy))
                {
                    var module = selected;
                    CancelRead = false;
                    App.RunBackground($"Reading coding of {module.Info.Short}", () =>
                    {
                        var blocks = session.ReadCoding(module, App.Progress, () => CancelRead);
                        Console.WriteLine($"{module.Info.Short}: {blocks.Count} coding blocks read.");
                    });
                }
                Tooltip("Takes about half a minute per module.");
                ImGui.SameLine();
                if (ColoredButton($"{Icons.Refresh}  Read all modules", Theme.Accent2, default, !App.Busy))
                {
                    CancelRead = false;
                    App.RunBackground("Reading coding of every module", () =>
                    {
                        foreach (var m in modules)
                        {
                            if (CancelRead)
                            {
                                break;
                            }
                            var blocks = session.ReadCoding(m, App.Progress, () => CancelRead);
                            Console.WriteLine($"{m.Info.Short}: {blocks.Count} coding blocks read.");
                        }
                    });
                }
                Tooltip("Reads every module one after the other: about half a minute each. Good before a coding session.");
                if (App.Busy)
                {
                    ImGui.SameLine();
                    if (ImGui.Button($"{Icons.Stop} Stop reading"))
                    {
                        CancelRead = true;
                    }
                }
            }

            bool anyRead = modules.Any(m => m.Coding?.Count > 0);
            if (Button($"{Icons.Copy} Save backup...", anyRead, "Read some coding first"))
            {
                string path = FileDialogs.SaveFile("Save the coding that was read", "Coding backup (*.json)|*.json",
                    $"coding-{(string.IsNullOrEmpty(session?.Vin) ? "car" : session.Vin)}-{DateTime.Now:yyyy-MM-dd_HH-mm}.json");
                if (path != null)
                {
                    try
                    {
                        CodingBackup.From(session?.Vin, modules).Save(path);
                        Console.WriteLine($"Saved the coding of {modules.Count(m => m.Coding?.Count > 0)} modules to {path}");
                    }
                    catch (Exception ex)
                    {
                        App.ShowMessage("Could not save the backup", ex.Message);
                    }
                }
            }
            ImGui.SameLine();
            if (ImGui.Button($"{Icons.Open} Compare with a backup..."))
            {
                string path = FileDialogs.OpenFile("A coding backup saved earlier", "Coding backup (*.json)|*.json|All files (*.*)|*.*");
                if (path != null)
                {
                    try
                    {
                        Comparison = CodingBackup.Load(path);
                        ComparisonPath = path;
                    }
                    catch (Exception ex)
                    {
                        App.ShowMessage("Could not open the backup", ex.Message);
                    }
                }
            }
            if (Comparison != null)
            {
                ImGui.SameLine();
                ImGui.TextDisabled($"{Path.GetFileName(ComparisonPath)} ({Comparison.Saved:yyyy-MM-dd HH:mm}{(Comparison.Vin.Length > 0 ? ", " + Comparison.Vin : "")})");
                ImGui.SameLine();
                if (ImGui.SmallButton($"{Icons.Close}##closecompare"))
                {
                    Comparison = null;
                }
            }
            ImGui.Spacing();

            var current = modules.FirstOrDefault(m => m.Address == SelectedModule);
            var now = current?.Coding;
            var then = Comparison?.BlocksOf(SelectedModule);
            if (modules.Count == 0 && Comparison != null)
            {
                DrawBackupOnly();
                return;
            }
            if (now is null)
            {
                Dim(current is null ? "" : $"The coding of {current.Info.Short} hasn't been read yet.");
                return;
            }
            if (now.Count == 0)
            {
                Colored(Theme.Warn, $"{current.Info.Short} didn't give any coding blocks over this connection.");
                return;
            }

            var names = BlockNames();
            if (then != null)
            {
                var changes = CodingCompare.Changes(then, now);
                if (changes.Count == 0)
                {
                    Colored(Theme.Good, $"{Icons.CheckCircle}  Same as the backup: all {now.Count} blocks of {current.Info.Short} match.");
                }
                else
                {
                    Colored(Theme.Warn, $"{changes.Count} of {now.Count} blocks differ from the backup ({changes.Sum(c => c.Offsets.Count)} bytes).");
                }
                ImGui.SameLine();
                ImGui.Checkbox("Only changed blocks", ref OnlyChanged);
                DrawBlocks(now, then, names, OnlyChanged ? changes.Select(c => c.Block).ToHashSet() : null);
            }
            else
            {
                ImGui.TextDisabled($"{now.Count} blocks, {now.Values.Sum(b => b.Length):N0} bytes. CAFD {current.Cafd}");
                DrawBlocks(now, null, names, null);
            }
        }

        // a backup opened without a car: show what it holds
        void DrawBackupOnly()
        {
            foreach (var m in Comparison.Modules)
            {
                if (ImGui.CollapsingHeader($"{m.Name} (0x{m.Address}) - {m.Blocks.Count} blocks  {m.Cafd}"))
                {
                    if (Hex.TryParseNumber(m.Address, out uint address))
                    {
                        DrawBlocks(Comparison.BlocksOf((byte)address), null, BlockNames(), null);
                    }
                }
            }
        }

        void DrawBlocks(SortedDictionary<ushort, byte[]> now, SortedDictionary<ushort, byte[]> then, Dictionary<ushort, string> names, HashSet<ushort> only)
        {
            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("blocks", 3, flags, new Vector2(0, Math.Max(S(200), ImGui.GetContentRegionAvail().Y))))
            {
                return;
            }
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Block", ImGuiTableColumnFlags.WidthFixed, S(220));
            ImGui.TableSetupColumn("Bytes", ImGuiTableColumnFlags.WidthFixed, S(60));
            ImGui.TableSetupColumn(then != null ? "Data (changed bytes in color, the backup's value on hover)" : "Data", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableHeadersRow();
            foreach (ushort block in now.Keys.Union(then?.Keys ?? Enumerable.Empty<ushort>()).OrderBy(b => b))
            {
                if (only != null && !only.Contains(block))
                {
                    continue;
                }
                now.TryGetValue(block, out var data);
                byte[] old = null;
                then?.TryGetValue(block, out old);
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Fonts.Push(Fonts.Mono);
                ImGui.TextUnformatted(block.ToString("X4"));
                Fonts.Pop();
                if (names.TryGetValue(block, out string name))
                {
                    ImGui.SameLine();
                    ImGui.TextDisabled(name);
                }
                ImGui.TableNextColumn();
                ImGui.TextDisabled((data ?? old)?.Length.ToString() ?? "");
                ImGui.TableNextColumn();
                if (data is null)
                {
                    Colored(Theme.Bad, "not in the car any more");
                    continue;
                }
                if (then != null && old is null)
                {
                    Colored(Theme.Modified, "new since the backup");
                }
                DrawBytes(block, data, old);
            }
            ImGui.EndTable();
        }

        // hex bytes wrapped to the column; with a backup, changed ones are colored and show the old value on hover
        static void DrawBytes(ushort block, byte[] data, byte[] old)
        {
            Fonts.Push(Fonts.Mono);
            float width = ImGui.CalcTextSize("00 ").X;
            int perLine = Math.Max(4, (int)(ImGui.GetContentRegionAvail().X / width));
            perLine -= perLine % 4;
            for (int i = 0; i < data.Length; i++)
            {
                if (i % perLine != 0)
                {
                    ImGui.SameLine(0, 0);
                }
                bool changed = old != null && (i >= old.Length || old[i] != data[i]);
                string text = data[i].ToString("X2") + " ";
                if (changed)
                {
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.Warn);
                    ImGui.TextUnformatted(text);
                    ImGui.PopStyleColor();
                    Tooltip($"Block {block:X4}, byte {i} (0x{i:X2})\nbackup: {(i < old.Length ? old[i].ToString("X2") : "-")} = {(i < old.Length ? Bits(old[i]) : "")}\nnow:    {data[i]:X2} = {Bits(data[i])}");
                }
                else if (data[i] == 0)
                {
                    ImGui.TextDisabled(text);
                }
                else
                {
                    ImGui.TextUnformatted(text);
                }
            }
            Fonts.Pop();
        }

        static string Bits(byte b) => Convert.ToString(b, 2).PadLeft(8, '0').Insert(4, " ");

        static string ModuleLabel(ModuleState m) =>
            $"{m.Info.Short} - {m.Info.Name}{(m.Coding != null ? $"  ({m.Coding.Count} blocks)" : "")}";

        // block numbers named in the pasted text ("3002 KomfortSchliessen"), in English
        Dictionary<ushort, string> BlockNames()
        {
            var names = new Dictionary<ushort, string>();
            foreach (Match match in BlockRef.Matches(Input ?? ""))
            {
                ushort block = Convert.ToUInt16(match.Groups[1].Value, 16);
                if (!names.ContainsKey(block))
                {
                    names[block] = CodingExplainer.ExplainLine(match.Value).English;
                }
            }
            return names;
        }

        // ---------------------------------------------------------------- explain text

        void DrawExplainer()
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
            var modules = App.Session?.ModulesSnapshot() ?? new List<ModuleState>();
            int columns = ShowOriginal ? 4 : 3;
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
            ImGui.TableSetupColumn("In this car", ImGuiTableColumnFlags.WidthFixed, S(170));
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
                ImGui.TableNextColumn();
                DrawInCar(line, modules);
            }
            ImGui.EndTable();
        }

        // for "MODULE > 3002 Block > ..." lines: the block's bytes if that module's coding was read
        void DrawInCar(CodingLine line, List<ModuleState> modules)
        {
            var match = BlockRef.Match(line.Original);
            if (!match.Success)
            {
                return;
            }
            ushort block = Convert.ToUInt16(match.Groups[1].Value, 16);
            var module = line.ModuleName != null ? modules.FirstOrDefault(m => m.Info.Name == line.ModuleName) : null;
            if (module is null)
            {
                return;
            }
            if (module.Coding is null)
            {
                if (ImGui.SmallButton($"Read {module.Info.Short}##{line.GetHashCode()}") && !App.Busy)
                {
                    var session = App.Session;
                    CancelRead = false;
                    App.RunBackground($"Reading coding of {module.Info.Short}", () => session.ReadCoding(module, App.Progress, () => CancelRead));
                }
                Tooltip($"Reads the coding of {module.Info.Name} to show block {block:X4} here");
                return;
            }
            if (module.Coding.TryGetValue(block, out var data))
            {
                Fonts.Push(Fonts.Mono);
                ImGui.TextDisabled($"{block:X4}: {data.Length} bytes");
                Fonts.Pop();
                Tooltip(Hex.Format(data));
            }
            else
            {
                ImGui.TextDisabled($"no block {block:X4}");
            }
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
