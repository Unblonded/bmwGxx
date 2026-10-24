using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace GDiag.Uds
{
    // one fault memory entry. BMW shows the 3 DTC bytes as 6 hex digits, e.g. 480A14.
    public class Dtc
    {
        public byte Module;
        public uint Code;
        public byte Status;

        public string CodeText => Code.ToString("X6");

        // status byte bits from ISO 14229 D.2
        public bool TestFailed => (Status & 0x01) != 0;
        public bool Pending => (Status & 0x04) != 0;
        public bool Confirmed => (Status & 0x08) != 0;
        public bool WarningLamp => (Status & 0x80) != 0;

        // what the driver would care about: is it happening now, or just remembered
        public string Summary
        {
            get
            {
                if (TestFailed)
                {
                    return "Active now";
                }
                if (Confirmed)
                {
                    return "Stored (not present right now)";
                }
                if (Pending)
                {
                    return "Intermittent / pending";
                }
                return "Logged";
            }
        }

        static readonly string[] BitNames =
        {
            "failing right now",
            "failed during this drive cycle",
            "pending (failed this or last cycle)",
            "confirmed and stored",
            "not tested since the fault memory was cleared",
            "failed since the fault memory was cleared",
            "not tested this drive cycle",
            "warning light requested",
        };

        public IEnumerable<string> StatusBits()
        {
            for (int bit = 0; bit < 8; bit++)
            {
                if ((Status & (1 << bit)) != 0)
                {
                    yield return BitNames[bit];
                }
            }
        }

        // 59 02 [availability mask] then 4 bytes per DTC: high, middle, low, status
        public static List<Dtc> ParseReport(byte[] response, byte module)
        {
            var list = new List<Dtc>();
            for (int i = 3; i + 3 < response.Length; i += 4)
            {
                list.Add(new Dtc
                {
                    Module = module,
                    Code = (uint)(response[i] << 16 | response[i + 1] << 8 | response[i + 2]),
                    Status = response[i + 3],
                });
            }
            return list;
        }
    }

    // BMW fault codes are manufacturer specific and their texts live in the SGBDs, which can't be shipped.
    // The user can load a list of their own: one "code;text" per line (also "code,text" or "code<TAB>text").
    public static class FaultTexts
    {
        static readonly Dictionary<uint, string> Texts = new Dictionary<uint, string>();

        public static int Count => Texts.Count;

        public static string Lookup(uint code) => Texts.TryGetValue(code, out var text) ? text : null;

        public static int Load(string path)
        {
            Texts.Clear();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return 0;
            }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                int split = line.IndexOfAny(new[] { ';', ',', '\t' });
                if (split <= 0 || line.StartsWith("#"))
                {
                    continue;
                }
                string code = line.Substring(0, split).Trim();
                if (code.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    code = code.Substring(2);
                }
                if (uint.TryParse(code, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                {
                    Texts[value] = line.Substring(split + 1).Trim().Trim('"');
                }
            }
            return Texts.Count;
        }
    }
}
