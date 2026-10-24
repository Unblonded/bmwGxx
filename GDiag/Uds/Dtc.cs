using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

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
        public bool FailedSinceClear => (Status & 0x20) != 0;
        public bool WarningLamp => (Status & 0x80) != 0;

        // Asking with every status bit (FF) also returns each code a module can set but hasn't tested this
        // drive (status 40/50): most modules list hundreds of those. These bits mean an actual failure.
        public const byte FaultMask = 0xAF;
        public bool IsFault => (Status & FaultMask) != 0;

        // 0 = active, 1 = stored, 2 = pending, 3 = the rest; for sorting worst first
        public int Severity => TestFailed ? 0 : Confirmed ? 1 : Pending ? 2 : 3;

        // filled in by CarSession.ReadDtcDetails
        public DtcDetails Details;

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
                if (FailedSinceClear)
                {
                    return "Happened since last clear";
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
                if ((response[i + 3] & FaultMask) == 0)
                {
                    continue;
                }
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

    // what the module remembers about a fault: snapshots (19 04) and extended data (19 06)
    public class DtcDetails
    {
        public class Snapshot
        {
            public int Number;
            public uint? Mileage;
            public List<(ushort did, byte[] data)> Values = new List<(ushort, byte[])>();
        }

        public List<Snapshot> Snapshots = new List<Snapshot>();
        public int? Occurrences;
        public Dictionary<byte, byte[]> Extended = new Dictionary<byte, byte[]>();
        public string Error;

        // BMW's standard snapshot values and their lengths; others end the parse and are shown as raw bytes
        static readonly Dictionary<ushort, int> KnownLengths = new Dictionary<ushort, int>
        {
            [0x1700] = 3, // mileage, km
            [0x1701] = 4, // vehicle time, seconds
            [0x1750] = 1,
            [0x1751] = 3,
        };

        public static string DidName(ushort did) => did switch
        {
            0x1700 => "Mileage",
            0x1701 => "Vehicle time",
            _ => $"DID {did:X4}",
        };

        // 59 04 [dtc 3] [status] then records: [number] [count] ([did 2] [data])*. BMW sends two records,
        // the first and the latest time the fault happened, each with mileage, time and a fault-specific value.
        public static List<Snapshot> ParseSnapshots(byte[] response)
        {
            var list = new List<Snapshot>();
            int i = 6;
            while (i + 1 < response.Length)
            {
                var snap = new Snapshot { Number = response[i] };
                int count = response[i + 1];
                i += 2;
                for (int n = 0; n < count && i + 2 <= response.Length; n++)
                {
                    ushort did = (ushort)(response[i] << 8 | response[i + 1]);
                    // a value of unknown length runs to where the next record starts
                    int end = KnownLengths.TryGetValue(did, out int length)
                        ? Math.Min(response.Length, i + 2 + length)
                        : FindNextRecord(response, i + 2, snap.Number + 1);
                    byte[] data = response.Skip(i + 2).Take(end - i - 2).ToArray();
                    snap.Values.Add((did, data));
                    if (did == 0x1700 && data.Length == 3)
                    {
                        snap.Mileage = (uint)(data[0] << 16 | data[1] << 8 | data[2]);
                    }
                    i = end;
                }
                list.Add(snap);
            }
            return list;
        }

        // the offset where "number, count, 17 00" starts, or the end
        static int FindNextRecord(byte[] r, int from, int number)
        {
            for (int j = from; j + 3 < r.Length; j++)
            {
                if (r[j] == number && r[j + 2] == 0x17 && r[j + 3] == 0x00)
                {
                    return j;
                }
            }
            return r.Length;
        }

        // 59 06 [dtc 3] [status] then [record number] [data]... BMW records are one byte each:
        // 01 = ?, 02 = how often it happened, 03 = healing counter
        public static void ParseExtended(byte[] response, DtcDetails details)
        {
            for (int i = 6; i + 1 < response.Length; i += 2)
            {
                details.Extended[response[i]] = new[] { response[i + 1] };
                if (response[i] == 0x02)
                {
                    details.Occurrences = response[i + 1];
                }
            }
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
