using System.Collections.Generic;
using System.Numerics;
using GDiag.Uds;
using ImGuiNET;
using static GDiag.UI.Widgets;

namespace GDiag.UI
{
    // Advanced mode: send raw UDS bytes to one control unit. Anything that isn't a plain read asks twice.
    public class ConsoleTab
    {
        readonly App App;
        string Target = "10";
        string Request = "22 F1 90";
        readonly List<(string request, string answer)> History = new List<(string, string)>();

        public ConsoleTab(App app)
        {
            App = app;
        }

        public void Draw()
        {
            SectionTitle(Icons.Terminal, "Raw UDS console");
            Help("Sends the bytes as they are. Reads (22, 19, 3E) go straight out; " +
                 "anything else could change the car and needs confirming.");
            ImGui.SetNextItemWidth(S(80));
            ImGui.InputText("Module (hex)", ref Target, 4);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(S(360));
            bool enter = ImGui.InputText("Request (hex)", ref Request, 512, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            bool valid = Hex.TryParseNumber(Target, out uint target) && target <= 0xFF && Hex.TryParse(Request, out _);
            bool send = Button($"{Icons.Play} Send", valid && App.Connected && !App.Busy, App.Connected ? "Enter a module address and hex bytes" : "Connect first") || enter && valid;

            if (send && App.Connected && Hex.TryParse(Request, out byte[] bytes))
            {
                byte address = (byte)target;
                if (ServiceSafety.IsReadOnly(bytes))
                {
                    SendNow(address, bytes);
                }
                else
                {
                    App.ConfirmDangerous($"Send {Hex.Format(bytes)} to 0x{address:X2}?",
                        "This is not a read-only request. It can change coding, reset the module or start a routine.",
                        () => SendNow(address, bytes));
                }
            }

            ImGui.Spacing();
            ImGui.BeginChild("history", Vector2.Zero, ImGuiChildFlags.Border);
            Fonts.Push(Fonts.Mono);
            lock (History)
            {
                foreach (var (request, answer) in History)
                {
                    ImGui.TextUnformatted(request);
                    ImGui.PushStyleColor(ImGuiCol.Text, Theme.Accent);
                    ImGui.TextWrapped("   " + answer);
                    ImGui.PopStyleColor();
                }
            }
            Fonts.Pop();
            ImGui.EndChild();
        }

        void SendNow(byte address, byte[] bytes)
        {
            var session = App.Session;
            App.RunBackground("Sending", () =>
            {
                var response = session.Uds.Request(address, bytes, 5000);
                lock (History)
                {
                    History.Add(($"-> {address:X2}  {Hex.Format(bytes)}", response.Describe()));
                }
            });
        }
    }
}
