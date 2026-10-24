using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GDiag.Net
{
    // A made-up G20 330i that answers UDS like the real control units would, for "--demo" and tests.
    // The values are invented; only the message formats follow the real car.
    public class SimVehicle
    {
        public const string DemoVin = "WBA5R1C50KAZ00001";
        public const string DemoMac = "001A37DE0001";

        class SimEcu
        {
            public byte Address;
            public string PartNumber;
            public byte[] Svk;
            public List<(uint code, byte status)> Dtcs = new List<(uint, byte)>();
            public byte Session = 0x01;
        }

        readonly Dictionary<byte, SimEcu> Ecus = new Dictionary<byte, SimEcu>();
        readonly DateTime Started = DateTime.UtcNow;
        readonly object StateLock = new object();

        public SimVehicle()
        {
            Add(0x10, "8798543", 0x0000A1B2, (0x0000341F, 5, 34, 14), (3, 1, 2));
            Add(0x12, "8654321", 0x00007E11, (0x00004C20, 2, 12, 1), (12, 7, 3), (0x480A14, 0x2F), (0x21F01C, 0x08));
            Add(0x18, "8687001", 0x00003300, (0x00002210, 1, 4, 0), (8, 2, 0));
            Add(0x29, "6893211", 0x00002901, (0x00003120, 3, 3, 3), (4, 0, 1), (0x5E2410, 0x09));
            Add(0x30, "6882200", 0x00003001, (0x00003010, 1, 1, 0), (2, 1, 0));
            Add(0x40, "9876500", 0x00004001, (0x00004400, 7, 21, 5), (17, 4, 2), (0x8040E0, 0x08));
            Add(0x60, "5A1B2C3", 0x00006001, (0x00006A00, 4, 2, 1), (9, 3, 0));
            Add(0x63, "5A9F8E7", 0x00006301, (0x0000A1F0, 11, 2, 7), (40, 1, 18), (0xB7F35A, 0x2C));
            Add(0x78, "9393939", 0x00007801, (0x00007800, 2, 0, 3), (5, 5, 5));
            Add(0x01, "6994801", 0x00000101, (0x00000110, 1, 2, 0), (3, 0, 0));
            Add(0x5D, "5A0C0D1", 0x00005D01, (0x00005DC0, 6, 1, 2), (14, 2, 1));
        }

        void Add(byte address, string partNumber, uint swfl, (uint id, byte main, byte sub, byte patch) cafd, (byte main, byte sub, byte patch) sw,
            params (uint code, byte status)[] dtcs)
        {
            var entries = new List<(byte cls, uint id, byte a, byte b, byte c)>
            {
                (0x01, 0x00001000u + address, 1, 0, 0),
                (0x06, 0x00002000u + address, 2, 1, 0),
                (0x08, swfl, sw.main, sw.sub, sw.patch),
                (0x05, cafd.id, cafd.main, cafd.sub, cafd.patch),
            };
            var svk = new List<byte> { 0x01, 0x01, 0x00, (byte)entries.Count };
            foreach (var e in entries)
            {
                svk.Add(e.cls);
                svk.AddRange(new[] { (byte)(e.id >> 24), (byte)(e.id >> 16), (byte)(e.id >> 8), (byte)e.id });
                svk.AddRange(new[] { e.a, e.b, e.c });
            }
            var ecu = new SimEcu { Address = address, PartNumber = partNumber, Svk = svk.ToArray() };
            ecu.Dtcs.AddRange(dtcs);
            Ecus[address] = ecu;
        }

        public bool Has(byte address) => Ecus.ContainsKey(address);

        // the ASCII the gateway sends back for a UDP vehicle identification request
        public string IdentificationText => $"DIAGADR10BMWMAC{DemoMac}BMWVIN{DemoVin}";

        // every answer to one request: (source, data). A functional request gets answers from all units.
        public List<(byte source, byte[] data)> Handle(byte target, byte[] request)
        {
            var answers = new List<(byte, byte[])>();
            if (request.Length == 0)
            {
                return answers;
            }
            IEnumerable<SimEcu> targets = target == Addresses.Functional ? Ecus.Values.OrderBy(e => e.Address)
                : Ecus.TryGetValue(target, out var one) ? new[] { one } : Array.Empty<SimEcu>();
            lock (StateLock)
            {
                foreach (var ecu in targets)
                {
                    foreach (byte[] answer in Answer(ecu, request, functional: target == Addresses.Functional))
                    {
                        answers.Add((ecu.Address, answer));
                    }
                }
            }
            return answers;
        }

        IEnumerable<byte[]> Answer(SimEcu ecu, byte[] request, bool functional)
        {
            byte sid = request[0];
            byte[] Negative(byte nrc) => new byte[] { 0x7F, sid, nrc };
            byte[] Positive(params byte[] rest) => new[] { (byte)(sid + 0x40) }.Concat(rest).ToArray();

            switch (sid)
            {
                case 0x10 when request.Length == 2:
                    ecu.Session = (byte)(request[1] & 0x7F);
                    yield return Positive(request[1], 0x00, 0x32, 0x01, 0xF4);
                    break;
                case 0x3E when request.Length == 2:
                    if ((request[1] & 0x80) == 0)
                    {
                        yield return Positive(0x00);
                    }
                    break;
                case 0x22 when request.Length >= 3:
                    ushort did = (ushort)(request[1] << 8 | request[2]);
                    byte[] record = ReadDid(ecu, did);
                    if (record != null)
                    {
                        yield return Positive(new[] { request[1], request[2] }.Concat(record).ToArray());
                    }
                    else if (!functional)
                    {
                        yield return Negative(0x31);
                    }
                    break;
                case 0x19 when request.Length == 3 && request[1] == 0x02:
                    var report = new List<byte> { 0x02, 0xFF };
                    foreach (var (code, status) in ecu.Dtcs.Where(d => (d.status & request[2]) != 0))
                    {
                        report.AddRange(new[] { (byte)(code >> 16), (byte)(code >> 8), (byte)code, status });
                    }
                    yield return Positive(report.ToArray());
                    break;
                case 0x14 when request.Length == 4:
                    // clearing takes a moment on a real unit, which answers "response pending" first
                    yield return Negative(0x78);
                    ecu.Dtcs.Clear();
                    yield return Positive();
                    break;
                case 0x11 when request.Length == 2:
                    yield return Positive(request[1]);
                    break;
                default:
                    if (!functional)
                    {
                        yield return Negative(0x11);
                    }
                    break;
            }
        }

        byte[] ReadDid(SimEcu ecu, ushort did)
        {
            switch (did)
            {
                case 0xF190: return Encoding.ASCII.GetBytes(DemoVin);
                case 0xF187: return Encoding.ASCII.GetBytes(ecu.PartNumber);
                case 0xF18A: return Encoding.ASCII.GetBytes("BMW SIM");
                case 0xF18B: return new byte[] { 0x19, 0x07, 0x15 };
                case 0xF18C: return Encoding.ASCII.GetBytes($"SN{ecu.Address:X2}00042");
                case 0xF186: return new[] { ecu.Session };
                case 0xF101: return ecu.Svk;
            }
            if (ecu.Address == 0x12 && (did & 0xFF00) == 0xF400)
            {
                return EngineValue((byte)did);
            }
            return null;
        }

        // standard OBD-II mode 01 data for a PID, or null when the engine doesn't support it
        public byte[] ObdMode01(byte pid)
        {
            if (pid == 0x00)
            {
                // supported PIDs 01-20: 04 05 0B 0C 0D 0E 0F 10 11 1F
                return new byte[] { 0x18, 0x3F, 0x80, 0x02 };
            }
            return EngineValue(pid);
        }

        // a gently idling, warming engine
        byte[] EngineValue(byte pid)
        {
            double t = (DateTime.UtcNow - Started).TotalSeconds;
            double wave = Math.Sin(t * 0.7);
            byte B(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
            byte[] W(double v)
            {
                int raw = (int)Math.Clamp(Math.Round(v), 0, 65535);
                return new[] { (byte)(raw >> 8), (byte)raw };
            }
            switch (pid)
            {
                case 0x04: return new[] { B((18 + 6 * wave) * 255 / 100) };
                case 0x05: return new[] { B(Math.Min(90, 35 + t * 0.8) + 40) };
                case 0x0B: return new[] { B(34 + 5 * wave) };
                case 0x0C: return W((720 + 60 * wave + 1200 * Math.Max(0, Math.Sin(t * 0.21))) * 4);
                case 0x0D: return new[] { B(Math.Max(0, 48 * Math.Sin(t * 0.21))) };
                case 0x0E: return new[] { B((12 + 4 * wave + 64) * 2) };
                case 0x0F: return new[] { B(24 + 40) };
                case 0x10: return W((4.5 + wave) * 100);
                case 0x11: return new[] { B((14 + 8 * Math.Max(0, Math.Sin(t * 0.21))) * 255 / 100) };
                case 0x1F: return W(t);
                case 0x2F: return new[] { B(62 * 255 / 100.0) };
                case 0x33: return new[] { B(101) };
                case 0x42: return W((14.1 + 0.1 * wave) * 1000);
                case 0x46: return new[] { B(17 + 40) };
                case 0x5C: return new[] { B(Math.Min(105, 30 + t * 0.6) + 40) };
                default: return null;
            }
        }
    }

    // talks to a SimVehicle in-process, for "--demo"
    public class SimTransport : IDiagTransport
    {
        readonly SimVehicle Car;
        readonly BlockingCollection<DiagMessage> Incoming = new BlockingCollection<DiagMessage>();

        public SimTransport(SimVehicle car)
        {
            Car = car;
        }

        public string Description => "Demo car (simulated G20)";
        public bool IsOpen { get; private set; } = true;

        public void Send(byte target, byte[] data)
        {
            if (target != Addresses.Functional && !Car.Has(target))
            {
                Incoming.Add(new DiagMessage { Source = target, Target = Addresses.Tester, Error = Hsfz.DescribeError(Hsfz.ControlBadTargetAddress) });
                return;
            }
            foreach (var (source, answer) in Car.Handle(target, data))
            {
                Incoming.Add(new DiagMessage { Source = source, Target = Addresses.Tester, Data = answer });
            }
        }

        public DiagMessage Receive(int timeoutMs)
        {
            return Incoming.TryTake(out var message, timeoutMs) ? message : null;
        }

        public void DiscardPending()
        {
            while (Incoming.TryTake(out _))
            {
            }
        }

        public void Dispose()
        {
            IsOpen = false;
        }
    }
}
