using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GDiag.Bmw;
using GDiag.Net;
using GDiag.Uds;

namespace GDiag
{
    public class ModuleState
    {
        public byte Address;
        public EcuInfo Info;
        // identification DID -> text, filled by ReadIdentification
        public ConcurrentDictionary<ushort, string> Ident = new ConcurrentDictionary<ushort, string>();
        public List<SgbmId> Svk;
        public List<Dtc> Dtcs;
        public string DtcError;
        public DateTime? DtcReadAt;

        public string Cafd => Svk?.FirstOrDefault(s => s.ProcessClass == 0x05)?.ToString() ?? "";
        public string PartNumber => Ident.TryGetValue(0xF187, out var p) ? p : "";
    }

    // Everything known about the connected car. Methods block, so call them from a background thread.
    public class CarSession : IDisposable
    {
        public readonly UdsClient Uds;
        public string Vin = "";
        public readonly List<ModuleState> Modules = new List<ModuleState>();
        readonly object ModulesLock = new object();

        public CarSession(IDiagTransport transport)
        {
            Uds = new UdsClient(transport);
        }

        public bool IsOpen => Uds.IsOpen;

        public List<ModuleState> ModulesSnapshot()
        {
            lock (ModulesLock)
            {
                return Modules.ToList();
            }
        }

        public ModuleState Module(byte address)
        {
            lock (ModulesLock)
            {
                return Modules.FirstOrDefault(m => m.Address == address);
            }
        }

        public string ReadVin()
        {
            byte[] record = Uds.ReadDid(Addresses.Gateway, 0xF190);
            Vin = record != null ? Hex.Ascii(record, 0) : "";
            return Vin;
        }

        // Finds the control units: first one functional request that every unit answers, then each address
        // that stayed silent is asked directly (some units ignore functional requests).
        public List<ModuleState> Scan(bool fullScan, Action<string> progress = null)
        {
            var found = new SortedDictionary<byte, ModuleState>();
            progress?.Invoke("Asking all control units at once");
            foreach (var answer in Uds.RequestFunctional(new byte[] { UdsClient.ReadDataByIdentifier, 0xF1, 0x01 }))
            {
                found[answer.Source] = NewModule(answer.Source, answer);
            }

            IEnumerable<byte> candidates = fullScan ? Enumerable.Range(0x00, 0x80).Select(a => (byte)a) : EcuCatalog.KnownAddresses;
            int savedTimeout = Uds.TimeoutMs;
            Uds.TimeoutMs = 400;
            try
            {
                foreach (byte address in candidates.Where(a => !found.ContainsKey(a) && a != Addresses.Tester))
                {
                    progress?.Invoke($"Checking address 0x{address:X2}");
                    var answer = Uds.Request(address, UdsClient.ReadDataByIdentifier, 0xF1, 0x01);
                    // any UDS answer, even a refusal, means something lives there
                    if (answer.Positive || answer.Nrc != 0)
                    {
                        found[address] = NewModule(address, answer);
                    }
                }
            }
            finally
            {
                Uds.TimeoutMs = savedTimeout;
            }

            lock (ModulesLock)
            {
                Modules.Clear();
                Modules.AddRange(found.Values);
            }
            return ModulesSnapshot();
        }

        static ModuleState NewModule(byte address, UdsResponse svkAnswer)
        {
            var module = new ModuleState { Address = address, Info = EcuCatalog.Lookup(address) };
            if (svkAnswer.Positive && svkAnswer.Data.Length > 3)
            {
                module.Svk = Svk.Parse(svkAnswer.Data.Skip(3).ToArray());
            }
            return module;
        }

        public void ReadIdentification(ModuleState module)
        {
            foreach (var (did, _, ascii) in LiveData.Identification)
            {
                byte[] record = Uds.ReadDid(module.Address, did, out var response);
                if (record is null)
                {
                    module.Ident[did] = response.Nrc != 0 ? "not supported" : "no answer";
                    continue;
                }
                module.Ident[did] = ascii ? Hex.Ascii(record, 0) : DescribeBinaryIdent(did, record);
            }
            byte[] svk = Uds.ReadDid(module.Address, 0xF101);
            if (svk != null)
            {
                module.Svk = Svk.Parse(svk) ?? module.Svk;
            }
        }

        static string DescribeBinaryIdent(ushort did, byte[] record)
        {
            if (did == 0xF18B && record.Length == 3)
            {
                // BCD year, month, day
                return $"20{record[0]:X2}-{record[1]:X2}-{record[2]:X2}";
            }
            if (did == 0xF186 && record.Length >= 1)
            {
                return record[0] switch
                {
                    0x01 => "default",
                    0x02 => "programming",
                    0x03 => "extended",
                    0x41 => "coding (BMW)",
                    _ => $"0x{record[0]:X2}",
                };
            }
            return Hex.Format(record);
        }

        public void ReadDtcs(ModuleState module)
        {
            var dtcs = Uds.ReadDtcs(module.Address, out var response);
            module.Dtcs = dtcs;
            module.DtcError = dtcs is null ? response.Describe() : null;
            module.DtcReadAt = DateTime.Now;
        }

        public UdsResponse ClearDtcs(ModuleState module)
        {
            var response = Uds.ClearAllDtcs(module.Address);
            if (response.Positive)
            {
                ReadDtcs(module);
            }
            return response;
        }

        public void Dispose()
        {
            // the transport belongs to the session
            Uds.Transport.Dispose();
        }
    }
}
