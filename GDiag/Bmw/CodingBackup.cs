using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GDiag.Bmw
{
    // The coding blocks of one or more modules saved to a file, so two readings (before and after a
    // BimmerCode or E-Sys session, or two cars) can be compared byte by byte.
    public class CodingBackup
    {
        public class ModuleCoding
        {
            public string Address { get; set; }
            public string Name { get; set; }
            public string Cafd { get; set; }
            // block DID (hex) -> bytes (hex)
            public SortedDictionary<string, string> Blocks { get; set; } = new SortedDictionary<string, string>();
        }

        public string Vin { get; set; } = "";
        public DateTime Saved { get; set; } = DateTime.Now;
        public List<ModuleCoding> Modules { get; set; } = new List<ModuleCoding>();

        public static CodingBackup From(string vin, IEnumerable<ModuleState> modules)
        {
            var backup = new CodingBackup { Vin = vin ?? "" };
            foreach (var m in modules.Where(m => m.Coding?.Count > 0))
            {
                var coding = new ModuleCoding { Address = m.Address.ToString("X2"), Name = m.Info.Short, Cafd = m.Cafd };
                foreach (var (did, data) in m.Coding)
                {
                    coding.Blocks[did.ToString("X4")] = Hex.Format(data);
                }
                backup.Modules.Add(coding);
            }
            return backup;
        }

        public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));

        public static CodingBackup Load(string path) => JsonSerializer.Deserialize<CodingBackup>(File.ReadAllText(path));

        public SortedDictionary<ushort, byte[]> BlocksOf(byte address)
        {
            var module = Modules.FirstOrDefault(m => Hex.TryParseNumber(m.Address, out uint a) && a == address);
            if (module is null)
            {
                return null;
            }
            var blocks = new SortedDictionary<ushort, byte[]>();
            foreach (var (did, hex) in module.Blocks)
            {
                if (Hex.TryParseNumber(did, out uint d) && Hex.TryParse(hex, out byte[] data))
                {
                    blocks[(ushort)d] = data;
                }
            }
            return blocks;
        }
    }

    public class BlockChange
    {
        public ushort Block;
        public byte[] Before;
        public byte[] After;
        // byte offsets that differ
        public List<int> Offsets = new List<int>();
    }

    public static class CodingCompare
    {
        public static List<BlockChange> Changes(SortedDictionary<ushort, byte[]> before, SortedDictionary<ushort, byte[]> after)
        {
            var changes = new List<BlockChange>();
            foreach (ushort block in before.Keys.Union(after.Keys).OrderBy(b => b))
            {
                before.TryGetValue(block, out var a);
                after.TryGetValue(block, out var b);
                var change = new BlockChange { Block = block, Before = a, After = b };
                int length = Math.Max(a?.Length ?? 0, b?.Length ?? 0);
                for (int i = 0; i < length; i++)
                {
                    int x = a != null && i < a.Length ? a[i] : -1;
                    int y = b != null && i < b.Length ? b[i] : -1;
                    if (x != y)
                    {
                        change.Offsets.Add(i);
                    }
                }
                if (change.Offsets.Count > 0)
                {
                    changes.Add(change);
                }
            }
            return changes;
        }
    }
}
