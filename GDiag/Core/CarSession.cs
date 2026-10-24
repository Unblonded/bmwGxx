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

        // coding blocks (DID 3000-3FFF) read from the module, filled by ReadCoding
        public SortedDictionary<ushort, byte[]> Coding;

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

        // values that only answered through plain OBD-II, so they aren't asked the BMW way again
        readonly ConcurrentDictionary<string, bool> ObdOnly = new ConcurrentDictionary<string, bool>();

        // One live value. Engine values (DIDs F4xx) are asked as UDS first; over D-CAN, a DME that refuses
        // them is asked again as standard OBD-II mode 01, which every car has to answer.
        public byte[] ReadLive(LiveValue value, out UdsResponse response)
        {
            response = null;
            bool obdCapable = Uds.Transport is DCanTransport && (value.Did & 0xFF00) == 0xF400 && value.Module == 0x12;
            if (!ObdOnly.ContainsKey(value.Id))
            {
                byte[] record = Uds.ReadDid(value.Module, value.Did, out response);
                if (record != null || !obdCapable)
                {
                    return record;
                }
            }
            if (!obdCapable)
            {
                return null;
            }
            byte pid = (byte)value.Did;
            var obd = Uds.Request(DCanTransport.ObdEngine, 0x01, pid);
            if (obd.Positive && obd.Data.Length > 2 && obd.Data[1] == pid)
            {
                ObdOnly[value.Id] = true;
                response = obd;
                return obd.Data.Skip(2).ToArray();
            }
            response ??= obd;
            return null;
        }

        // set when the DME refused a request for several DIDs at once, so they are asked one by one from then on
        volatile bool NoMultiDid;
        volatile bool NoMultiPid;

        // Several engine values in one request: "22 F40C F40D ..." (ISO 14229 allows up to the server's limit) or,
        // for values that only answer as OBD-II, "01 0C 0D ..." (up to 6 PIDs). Returns the record of each value
        // that came back; the rest are read one at a time by the caller.
        public Dictionary<LiveValue, byte[]> ReadLiveBatch(IList<LiveValue> values)
        {
            var result = new Dictionary<LiveValue, byte[]>();
            var batchable = values.Where(v => v.Batchable).ToList();
            var uds = batchable.Where(v => !ObdOnly.ContainsKey(v.Id)).ToList();
            var obd = batchable.Where(v => v.IsObd && ObdOnly.ContainsKey(v.Id)).ToList();

            if (!NoMultiDid)
            {
                foreach (var chunk in uds.GroupBy(v => v.Module).SelectMany(g => g.Chunk(6)).Where(c => c.Length > 1))
                {
                    var request = new List<byte> { UdsClient.ReadDataByIdentifier };
                    foreach (var v in chunk)
                    {
                        request.Add((byte)(v.Did >> 8));
                        request.Add((byte)v.Did);
                    }
                    var response = Uds.Request(chunk[0].Module, request.ToArray());
                    if (response.Nrc == 0x13 || response.Nrc == 0x14 || response.Nrc == 0x12)
                    {
                        NoMultiDid = true;
                        break;
                    }
                    if (response.Positive)
                    {
                        SplitRecords(response.Data, 1, 2, chunk, result);
                    }
                }
            }

            if (!NoMultiPid && Uds.Transport is DCanTransport)
            {
                foreach (var chunk in obd.Chunk(6).Where(c => c.Length > 1))
                {
                    var request = new List<byte> { 0x01 };
                    request.AddRange(chunk.Select(v => (byte)v.Did));
                    var response = Uds.Request(DCanTransport.ObdEngine, request.ToArray());
                    if (!response.Positive && response.Nrc != 0)
                    {
                        NoMultiPid = true;
                        break;
                    }
                    if (response.Positive)
                    {
                        SplitRecords(response.Data, 1, 1, chunk, result);
                    }
                }
            }
            return result;
        }

        // walks "id record id record ..." with ids of idBytes bytes; stops at an id it doesn't expect
        static void SplitRecords(byte[] data, int start, int idBytes, IList<LiveValue> asked, Dictionary<LiveValue, byte[]> result)
        {
            int i = start;
            while (i + idBytes <= data.Length)
            {
                uint id = Hex.BigEndian(data, i, idBytes);
                var value = asked.FirstOrDefault(v => (idBytes == 2 ? v.Did : (byte)v.Did) == id);
                if (value is null || i + idBytes + value.RecordLength > data.Length)
                {
                    return;
                }
                result[value] = data.Skip(i + idBytes).Take(value.RecordLength).ToArray();
                i += idBytes + value.RecordLength;
            }
        }

        // the OBD PIDs the engine says it has, from the support bitmaps; null when it doesn't answer them
        public HashSet<byte> ReadSupportedPids()
        {
            var supported = new HashSet<byte>();
            bool any = false;
            for (byte basePid = 0x00; basePid <= 0xC0; basePid += 0x20)
            {
                if (basePid != 0 && !supported.Contains(basePid))
                {
                    break;
                }
                var probe = new LiveValue { Id = $"pids{basePid:X2}", Module = 0x12, Did = (ushort)(0xF400 | basePid), ByteCount = 4 };
                byte[] bitmap = ReadLive(probe, out _);
                if (bitmap is null || bitmap.Length < 4)
                {
                    break;
                }
                any = true;
                supported.UnionWith(LiveData.SupportedPids(basePid, bitmap));
            }
            return any ? supported : null;
        }

        public void ReadDtcs(ModuleState module)
        {
            var dtcs = Uds.ReadDtcs(module.Address, out var response);
            module.Dtcs = dtcs;
            module.DtcError = dtcs is null ? response.Describe() : null;
            module.DtcReadAt = DateTime.Now;
        }

        public void ReadDtcDetails(Dtc dtc)
        {
            var details = new DtcDetails();
            byte[] code = { (byte)(dtc.Code >> 16), (byte)(dtc.Code >> 8), (byte)dtc.Code };
            var snapshots = Uds.Request(dtc.Module, new byte[] { UdsClient.ReadDtcInformation, 0x04, code[0], code[1], code[2], 0xFF });
            if (snapshots.Positive)
            {
                details.Snapshots = DtcDetails.ParseSnapshots(snapshots.Data);
            }
            else
            {
                details.Error = snapshots.Describe();
            }
            var extended = Uds.Request(dtc.Module, new byte[] { UdsClient.ReadDtcInformation, 0x06, code[0], code[1], code[2], 0xFF });
            if (extended.Positive)
            {
                DtcDetails.ParseExtended(extended.Data, details);
            }
            dtc.Details = details;
        }

        // Reads the module's coding: on F/G series every coding block is a DID from 3000 to 3FFF, the same
        // number E-Sys shows in front of a block ("3002 KomfortSchliessen"). Only reads (service 22).
        public SortedDictionary<ushort, byte[]> ReadCoding(ModuleState module, Action<string> progress = null, Func<bool> cancelled = null)
        {
            var blocks = new SortedDictionary<ushort, byte[]>();
            int savedTimeout = Uds.TimeoutMs;
            Uds.TimeoutMs = 300;
            int silent = 0;
            try
            {
                for (int did = 0x3000; did <= 0x3FFF; did++)
                {
                    if (cancelled?.Invoke() == true)
                    {
                        break;
                    }
                    if ((did & 0xFF) == 0)
                    {
                        progress?.Invoke($"{module.Info.Short}: block {did:X4} ({blocks.Count} found)");
                    }
                    byte[] record = Uds.ReadDid(module.Address, (ushort)did, out var response);
                    if (record != null)
                    {
                        blocks[(ushort)did] = record;
                    }
                    if (response.TimedOut)
                    {
                        // a module that doesn't answer at all would take 20 minutes to walk through
                        if (++silent >= 8)
                        {
                            break;
                        }
                    }
                    else
                    {
                        silent = 0;
                    }
                    if (response.Nrc == 0x11 || response.Nrc == 0x7F)
                    {
                        break;
                    }
                }
            }
            finally
            {
                Uds.TimeoutMs = savedTimeout;
            }
            module.Coding = blocks;
            return blocks;
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
