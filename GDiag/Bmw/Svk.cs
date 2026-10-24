using System.Collections.Generic;

namespace GDiag.Bmw
{
    // one software/hardware part a control unit reports, e.g. CAFD_0000341F_005_034_014
    public class SgbmId
    {
        public byte ProcessClass;
        public uint Id;
        public byte Main;
        public byte Sub;
        public byte Patch;

        public string ClassName => Svk.ClassName(ProcessClass);
        public override string ToString() => $"{ClassName}_{Id:X8}_{Main:D3}_{Sub:D3}_{Patch:D3}";
    }

    // The SVK ("Steuergeräte-Verbau-Kennung", DID F101) lists every part flashed into a control unit:
    // hardware, bootloader, software and coding data (CAFD). Layout:
    //   [SVK version] [programming dependencies checked] [number of entries, 2 bytes]
    //   then 8 bytes per entry: [process class] [id, 4 bytes] [main] [sub] [patch]
    // Some units add a date or fingerprint after the list, which is ignored.
    public static class Svk
    {
        static readonly Dictionary<byte, (string name, string meaning)> Classes = new Dictionary<byte, (string, string)>
        {
            [0x01] = ("HWEL", "Hardware (electronics)"),
            [0x02] = ("HWAP", "Hardware assembly part"),
            [0x03] = ("HWFR", "Hardware (free)"),
            [0x04] = ("GWTB", "Gateway routing table"),
            [0x05] = ("CAFD", "Coding data: which features are switched on"),
            [0x06] = ("BTLD", "Bootloader"),
            [0x07] = ("FLSL", "Flash loader"),
            [0x08] = ("SWFL", "Software (program)"),
            [0x09] = ("SWFF", "Software (function data)"),
            [0x0A] = ("SWPF", "Software (test/protection data)"),
            [0x0B] = ("ONPS", "Onboard programming system"),
            [0x0F] = ("FAFP", "Vehicle order fingerprint"),
            [0x1A] = ("TLRT", "Temporary loader routine"),
            [0x1B] = ("TPRG", "Temporary program"),
        };

        public static string ClassName(byte processClass) => Classes.TryGetValue(processClass, out var c) ? c.name : $"PC{processClass:X2}";

        public static string ClassMeaning(byte processClass) => Classes.TryGetValue(processClass, out var c) ? c.meaning : "Unknown part type";

        // null when the record doesn't look like an SVK
        public static List<SgbmId> Parse(byte[] record)
        {
            if (record is null || record.Length < 4)
            {
                return null;
            }
            int count = record[2] << 8 | record[3];
            const int first = 4;
            if (count == 0 || first + count * 8 > record.Length)
            {
                return null;
            }
            var list = new List<SgbmId>();
            for (int i = 0; i < count; i++)
            {
                int o = first + i * 8;
                list.Add(new SgbmId
                {
                    ProcessClass = record[o],
                    Id = Hex.BigEndian(record, o + 1, 4),
                    Main = record[o + 5],
                    Sub = record[o + 6],
                    Patch = record[o + 7],
                });
            }
            return list;
        }
    }
}
