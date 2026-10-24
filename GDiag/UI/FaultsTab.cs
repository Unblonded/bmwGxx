using System.Linq;
using System.Numerics;
using GDiag.Uds;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Fault memory of every control unit, with the status byte spelled out.
    public class FaultsTab
    {
        readonly App App;
        string Filter = "";
        bool OnlyActive = false;

        public FaultsTab(App app)
        {
            App = app;
        }

        static AppSettings Settings => AppSettings.Current;

        public void LoadTextsDialog()
        {
            string path = FileDialogs.OpenFile("Fault code texts: one \"code;text\" per line", "Text and CSV files (*.txt;*.csv)|*.txt;*.csv|All files (*.*)|*.*");
            if (path is null)
            {
                return;
            }
            int count = FaultTexts.Load(path);
            Settings.FaultTextFile = path;
            Settings.Save();
            System.Console.WriteLine($"Loaded {count} fault code texts from {path}");
        }

        public void Draw()
        {
            var session = App.Session;
            if (session is null)
            {
                Dim("Connect to a car first.");
                return;
            }
            var modules = session.ModulesSnapshot();

            bool canRun = !App.Busy && modules.Count > 0;
            if (PrimaryButton($"{Icons.Refresh}  Read all fault codes", Theme.Accent, default, canRun))
            {
                App.RunBackground("Reading fault codes", () =>
                {
                    foreach (var m in modules)
                    {
                        App.Progress(m.Info.Short);
                        session.ReadDtcs(m);
                    }
                    int total = modules.Sum(m => m.Dtcs?.Count ?? 0);
                    System.Console.WriteLine($"Read fault memory of {modules.Count} control units: {total} entries.");
                });
            }
            ImGui.SameLine();
            bool anyRead = modules.Any(m => m.Dtcs != null);
            if (ColoredButton($"{Icons.Close} Clear all fault codes", Theme.Bad, default, canRun && anyRead))
            {
                App.ConfirmDangerous("Clear the fault memory of every control unit?",
                    "Clears stored faults in all modules. Faults that are still present come straight back. " +
                    "Write down or copy anything you need first: the history is gone afterwards.",
                    () => App.RunBackground("Clearing fault codes", () =>
                    {
                        foreach (var m in modules)
                        {
                            App.Progress(m.Info.Short);
                            var r = session.ClearDtcs(m);
                            System.Console.WriteLine($"{m.Info.Short}: {(r.Positive ? "cleared" : r.Describe())}");
                        }
                    }));
            }
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(220));
            ImGui.InputTextWithHint("##filter", $"{Icons.Search} filter", ref Filter, 64);
            ImGui.SameLine();
            ImGui.Checkbox("Only active now", ref OnlyActive);
            ImGui.SameLine();
            if (ImGui.SmallButton($"{Icons.Copy} Copy list"))
            {
                ImGui.SetClipboardText(string.Join("\n", modules.Where(m => m.Dtcs != null)
                    .SelectMany(m => m.Dtcs.Select(d => $"{m.Info.Short}\t{d.CodeText}\t{FaultTexts.Lookup(d.Code) ?? ""}\t{d.Summary}"))));
            }

            if (FaultTexts.Count == 0)
            {
                Dim("BMW fault codes are maker-specific, so only the code and its status are shown. " +
                    "File > Load fault code texts adds descriptions from a \"code;text\" list.");
            }
            ImGui.Spacing();

            if (!anyRead)
            {
                Dim("Not read yet.");
                return;
            }

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (!ImGui.BeginTable("dtcs", 5, flags))
            {
                return;
            }
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Module", ImGuiTableColumnFlags.WidthFixed, S(90));
            ImGui.TableSetupColumn("Code", ImGuiTableColumnFlags.WidthFixed, S(90));
            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, S(230));
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, S(80));
            ImGui.TableHeadersRow();

            foreach (var m in modules.Where(m => m.Dtcs != null || m.DtcError != null))
            {
                var dtcs = (m.Dtcs ?? new()).Where(d => !OnlyActive || d.TestFailed)
                    .Where(d => Filter.Length == 0 || Matches(m, d)).ToList();
                if (m.Dtcs != null && dtcs.Count == 0 && (Filter.Length > 0 || OnlyActive))
                {
                    continue;
                }

                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                Fonts.Push(Fonts.Semibold);
                ImGui.TextUnformatted(m.Info.Short);
                Fonts.Pop();
                Tooltip($"{m.Info.Name} (0x{m.Address:X2})");
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                if (m.DtcError != null)
                {
                    Colored(Theme.Warn, $"Could not read: {m.DtcError}");
                }
                else if (m.Dtcs.Count == 0)
                {
                    Colored(Theme.Good, $"{Icons.Check} No faults stored");
                }
                else
                {
                    ImGui.TextDisabled($"{m.Dtcs.Count} entr{(m.Dtcs.Count == 1 ? "y" : "ies")}");
                }
                ImGui.TableNextColumn();
                ImGui.TableNextColumn();
                if (m.Dtcs?.Count > 0 && ImGui.SmallButton($"Clear##{m.Address}"))
                {
                    var module = m;
                    App.ConfirmDangerous($"Clear the fault memory of {m.Info.Short}?",
                        $"Clears every stored fault in the {m.Info.Name}. Faults that are still present come back.",
                        () => App.RunBackground($"Clearing {module.Info.Short}", () =>
                        {
                            var r = session.ClearDtcs(module);
                            System.Console.WriteLine($"{module.Info.Short}: {(r.Positive ? "fault memory cleared" : r.Describe())}");
                        }));
                }

                foreach (var d in dtcs)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    Fonts.Push(Fonts.Mono);
                    ImGui.TextUnformatted(d.CodeText);
                    Fonts.Pop();
                    ImGui.TableNextColumn();
                    string text = FaultTexts.Lookup(d.Code);
                    if (text != null)
                    {
                        ImGui.TextWrapped(text);
                    }
                    else
                    {
                        ImGui.TextDisabled("(no description loaded)");
                    }
                    ImGui.TableNextColumn();
                    Pill(d.Summary, d.TestFailed ? Theme.Bad : d.Confirmed ? Theme.Warn : Theme.Dim);
                    Tooltip($"Status 0x{d.Status:X2}:\n- {string.Join("\n- ", d.StatusBits())}");
                    ImGui.TableNextColumn();
                }
            }
            ImGui.EndTable();
        }

        bool Matches(ModuleState m, Dtc d)
        {
            string f = Filter.Trim();
            return m.Info.Short.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || d.CodeText.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || (FaultTexts.Lookup(d.Code)?.Contains(f, System.StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }
}
