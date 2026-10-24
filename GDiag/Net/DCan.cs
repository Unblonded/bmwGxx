using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GDiag.Net
{
    // raw CAN frames in and out, 11-bit ids. Send must be safe to call from two threads.
    public interface ICanBus : IDisposable
    {
        string Description { get; }
        void Send(uint id, byte[] data);
        // frames that arrived within timeoutMs (may be empty)
        List<(uint id, byte[] data)> Receive(int timeoutMs);
    }

    // a J2534 adapter (Tactrix OpenPort 2.0 and others) as an ICanBus on the OBD port's CAN pins 6 and 14
    public class J2534CanBus : ICanBus
    {
        readonly J2534Api Api;

        public string Description { get; }

        public J2534CanBus(J2534Driver driver, int baud = DCanTransport.Baud)
        {
            Api = new J2534Api(driver);
            try
            {
                Api.OpenCan(baud);
                // BMW diagnostics answer on 0x600-0x6FF; standard OBD-II answers on 0x7E8-0x7EF
                Api.PassFilter11(0x700, 0x600);
                Api.PassFilter11(0x7F8, 0x7E8);
            }
            catch
            {
                Api.Dispose();
                throw;
            }
            string versions = Api.Versions();
            Description = $"{driver.Name} (D-CAN {baud / 1000} kbit/s)";
            if (versions.Length > 0)
            {
                Console.WriteLine($"{driver.Name}: {versions}");
            }
        }

        public void Send(uint id, byte[] data) => Api.Write(id, data);

        public List<(uint id, byte[] data)> Receive(int timeoutMs) => Api.Read(timeoutMs);

        public void Dispose() => Api.Dispose();
    }

    // BMW diagnostics on D-CAN (the OBD port's CAN bus, 500 kbit/s), ISO 15765-2 with extended addressing:
    //   tester -> module: CAN id 0x6F1, first data byte = module address, then the ISO-TP frame
    //   module -> tester: CAN id 0x600 + module address, first data byte = 0xF1 (the tester)
    // So each CAN frame carries at most 6 UDS bytes in a single frame, 5 in a first frame and 6 per consecutive frame.
    // The ISO-TP segmentation is done here in software, which lets one raw CAN channel talk to every module and
    // to the functional address 0xDF at once.
    // Standard OBD-II (0x7DF/0x7E0 -> 0x7E8, normal addressing) is reachable through the pseudo address ObdEngine.
    public class DCanTransport : IDiagTransport
    {
        public const int Baud = 500000;
        public const uint TesterCanId = 0x6F1;
        public const byte TesterAddress = 0xF1;
        public const uint ResponseBase = 0x600;
        // not a real BMW address: requests to it go out as standard OBD-II to the engine (0x7E0)
        public const byte ObdEngine = 0xFE;
        const uint ObdRequestId = 0x7E0;
        const uint ObdResponseId = 0x7E8;
        const byte Padding = 0x00;

        readonly ICanBus Bus;
        readonly Thread Reader;
        readonly BlockingCollection<DiagMessage> Incoming = new BlockingCollection<DiagMessage>();
        // flow control frames for a multi-frame request we are sending, by module address
        readonly BlockingCollection<(byte source, byte[] frame)> FlowControl = new BlockingCollection<(byte, byte[])>();
        readonly object SendLock = new object();
        volatile bool open = true;

        class Assembly
        {
            public byte[] Buffer;
            public int Received;
            public int NextSequence;
        }
        readonly Dictionary<uint, Assembly> Partial = new Dictionary<uint, Assembly>();

        public DCanTransport(ICanBus bus)
        {
            Bus = bus;
            Reader = new Thread(ReadLoop) { IsBackground = true, Name = "D-CAN reader" };
            Reader.Start();
        }

        public string Description => Bus.Description;
        public bool IsOpen => open;

        // ---------------------------------------------------------------- sending

        public void Send(byte target, byte[] data)
        {
            if (!open)
            {
                throw new InvalidOperationException("The adapter is closed");
            }
            lock (SendLock)
            {
                if (target == ObdEngine)
                {
                    SendNormal(data);
                }
                else
                {
                    SendExtended(target, data);
                }
            }
        }

        void SendExtended(byte target, byte[] data)
        {
            if (data.Length <= 6)
            {
                Bus.Send(TesterCanId, Frame(target, new[] { (byte)data.Length }.Concat(data)));
                return;
            }
            if (target == Addresses.Functional)
            {
                // nobody sends flow control for a functional request, so it has to fit in one frame
                throw new ArgumentException("Functional requests must fit in one CAN frame (6 bytes)");
            }
            if (data.Length > 0xFFF)
            {
                throw new ArgumentException("Request too long for ISO-TP");
            }
            while (FlowControl.TryTake(out _))
            {
            }
            Bus.Send(TesterCanId, Frame(target, new[] { (byte)(0x10 | data.Length >> 8), (byte)data.Length }.Concat(data.Take(5))));
            int offset = 5;
            byte sequence = 1;
            while (offset < data.Length)
            {
                var (blockSize, separationMs) = WaitFlowControl(target);
                int sentInBlock = 0;
                while (offset < data.Length && (blockSize == 0 || sentInBlock < blockSize))
                {
                    if (separationMs > 0)
                    {
                        Thread.Sleep(separationMs);
                    }
                    Bus.Send(TesterCanId, Frame(target, new[] { (byte)(0x20 | sequence) }.Concat(data.Skip(offset).Take(6))));
                    offset += 6;
                    sequence = (byte)((sequence + 1) & 0x0F);
                    sentInBlock++;
                }
            }
        }

        (int blockSize, int separationMs) WaitFlowControl(byte target)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(1000);
            while (true)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0 || !FlowControl.TryTake(out var fc, left))
                {
                    throw new TimeoutException($"Module 0x{target:X2} did not send flow control");
                }
                if (fc.source != target)
                {
                    continue;
                }
                switch (fc.frame[0] & 0x0F)
                {
                    case 0: // continue to send
                        int st = fc.frame[2];
                        // 0xF1-0xF9 are 100-900 µs, the smallest sleep is 1 ms
                        int separation = st <= 0x7F ? st : 1;
                        return (fc.frame[1], separation);
                    case 1: // wait
                        deadline = DateTime.UtcNow.AddMilliseconds(1000);
                        continue;
                    default:
                        throw new InvalidOperationException($"Module 0x{target:X2} refused the request (flow control overflow)");
                }
            }
        }

        // standard OBD-II single frame request to the engine (mode 01 etc are always short)
        void SendNormal(byte[] data)
        {
            if (data.Length > 7)
            {
                throw new ArgumentException("OBD-II requests must fit in one CAN frame");
            }
            var frame = new byte[8];
            frame[0] = (byte)data.Length;
            Buffer.BlockCopy(data, 0, frame, 1, data.Length);
            for (int i = 1 + data.Length; i < 8; i++)
            {
                frame[i] = Padding;
            }
            Bus.Send(ObdRequestId, frame);
        }

        static byte[] Frame(byte address, IEnumerable<byte> isoTp)
        {
            var frame = new byte[8];
            frame[0] = address;
            int i = 1;
            foreach (byte b in isoTp)
            {
                frame[i++] = b;
            }
            for (; i < 8; i++)
            {
                frame[i] = Padding;
            }
            return frame;
        }

        // ---------------------------------------------------------------- receiving

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

        void ReadLoop()
        {
            try
            {
                while (open)
                {
                    foreach (var (id, data) in Bus.Receive(20))
                    {
                        if (id >= ResponseBase && id <= ResponseBase + 0xFF && data.Length >= 2 && data[0] == TesterAddress)
                        {
                            HandleExtended((byte)(id - ResponseBase), data);
                        }
                        else if (id >= ObdResponseId && id <= ObdResponseId + 7 && data.Length >= 1)
                        {
                            HandleNormal(id, data);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (open)
                {
                    Console.WriteLine($"Lost the adapter: {ex.Message}");
                }
            }
            open = false;
        }

        void HandleExtended(byte source, byte[] data)
        {
            byte pci = data[1];
            switch (pci >> 4)
            {
                case 0x0: // single frame
                    int length = pci & 0x0F;
                    if (length > 0 && length <= data.Length - 2)
                    {
                        Deliver(source, data.Skip(2).Take(length).ToArray());
                    }
                    break;
                case 0x1: // first frame: ask for the rest
                    int total = (pci & 0x0F) << 8 | data[2];
                    var assembly = new Assembly { Buffer = new byte[total], NextSequence = 1 };
                    int first = Math.Min(total, data.Length - 3);
                    Buffer.BlockCopy(data, 3, assembly.Buffer, 0, first);
                    assembly.Received = first;
                    Partial[ResponseBase + source] = assembly;
                    // block size 0 (send everything), no separation time. Not under SendLock: a request may be
                    // holding it while it waits for this thread to deliver its own flow control.
                    Bus.Send(TesterCanId, Frame(source, new byte[] { 0x30, 0x00, 0x00 }));
                    break;
                case 0x2: // consecutive frame
                    Continue(ResponseBase + source, pci, data, 2, payload => Deliver(source, payload));
                    break;
                case 0x3: // flow control for our multi-frame request
                    FlowControl.Add((source, data.Skip(1).ToArray()));
                    break;
            }
        }

        void HandleNormal(uint id, byte[] data)
        {
            byte pci = data[0];
            switch (pci >> 4)
            {
                case 0x0:
                    int length = pci & 0x0F;
                    if (length > 0 && length <= data.Length - 1)
                    {
                        Deliver(ObdEngine, data.Skip(1).Take(length).ToArray());
                    }
                    break;
                case 0x1:
                    int total = (pci & 0x0F) << 8 | data[1];
                    var assembly = new Assembly { Buffer = new byte[total], NextSequence = 1 };
                    int first = Math.Min(total, data.Length - 2);
                    Buffer.BlockCopy(data, 2, assembly.Buffer, 0, first);
                    assembly.Received = first;
                    Partial[id] = assembly;
                    // flow control goes to the physical request id of the answering module (0x7E8 -> 0x7E0)
                    Bus.Send(id - 8, new byte[] { 0x30, 0x00, 0x00, Padding, Padding, Padding, Padding, Padding });
                    break;
                case 0x2:
                    Continue(id, pci, data, 1, payload => Deliver(ObdEngine, payload));
                    break;
            }
        }

        void Continue(uint key, byte pci, byte[] data, int dataStart, Action<byte[]> complete)
        {
            if (!Partial.TryGetValue(key, out var assembly))
            {
                return;
            }
            if ((pci & 0x0F) != assembly.NextSequence)
            {
                Console.WriteLine($"D-CAN: frame out of order from 0x{key:X3}, message dropped");
                Partial.Remove(key);
                return;
            }
            assembly.NextSequence = (assembly.NextSequence + 1) & 0x0F;
            int count = Math.Min(assembly.Buffer.Length - assembly.Received, data.Length - dataStart);
            Buffer.BlockCopy(data, dataStart, assembly.Buffer, assembly.Received, count);
            assembly.Received += count;
            if (assembly.Received >= assembly.Buffer.Length)
            {
                Partial.Remove(key);
                complete(assembly.Buffer);
            }
        }

        void Deliver(byte source, byte[] payload)
        {
            Incoming.Add(new DiagMessage { Source = source, Target = TesterAddress, Data = payload });
        }

        public void Dispose()
        {
            open = false;
            // the reader must be out of the driver before the adapter is closed
            Reader.Join(1000);
            Bus.Dispose();
        }
    }
}
