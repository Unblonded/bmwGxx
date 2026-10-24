using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace GDiag.Net
{
    // The SimVehicle's modules on a pretend D-CAN bus, speaking BMW's extended-address ISO-TP (and OBD-II on
    // 0x7E0/0x7E8) frame by frame, so DCanTransport can be tested and demoed without an adapter.
    public class SimCanBus : ICanBus
    {
        readonly SimVehicle Car;
        readonly BlockingCollection<(uint id, byte[] data)> Outgoing = new BlockingCollection<(uint, byte[])>();
        readonly object Lock = new object();

        // multi-frame request being received from the tester, by module address
        readonly Dictionary<byte, (byte[] buffer, int received)> Requests = new Dictionary<byte, (byte[], int)>();
        // consecutive frames of a long answer, sent once the tester's flow control arrives
        readonly Dictionary<byte, Queue<byte[]>> PendingAnswers = new Dictionary<byte, Queue<byte[]>>();

        // every frame the tester sent, for tests
        public readonly List<(uint id, byte[] data)> Sent = new List<(uint, byte[])>();

        public SimCanBus(SimVehicle car)
        {
            Car = car;
        }

        public string Description => "Demo car on simulated D-CAN";

        public void Send(uint id, byte[] data)
        {
            lock (Lock)
            {
                Sent.Add((id, data.ToArray()));
                if (id == DCanTransport.TesterCanId && data.Length >= 2)
                {
                    FromTester(data[0], data.Skip(1).ToArray());
                }
                else if (id == 0x7E0 && data.Length >= 3 && data[0] <= 7 && data[1] == 0x01)
                {
                    byte[] value = Car.ObdMode01(data[2]);
                    if (value != null)
                    {
                        var answer = new byte[] { 0x41, data[2] }.Concat(value).ToArray();
                        Emit(0x7E8, Pad(new[] { (byte)answer.Length }.Concat(answer)));
                    }
                }
            }
        }

        void FromTester(byte target, byte[] isoTp)
        {
            switch (isoTp[0] >> 4)
            {
                case 0x0:
                    int length = isoTp[0] & 0x0F;
                    Answer(target, isoTp.Skip(1).Take(length).ToArray());
                    break;
                case 0x1:
                    int total = (isoTp[0] & 0x0F) << 8 | isoTp[1];
                    var buffer = new byte[total];
                    int first = Math.Min(total, isoTp.Length - 2);
                    Array.Copy(isoTp, 2, buffer, 0, first);
                    Requests[target] = (buffer, first);
                    // flow control: block size 0, 1 ms between frames
                    Emit(DCanTransport.ResponseBase + target, Pad(new byte[] { DCanTransport.TesterAddress, 0x30, 0x00, 0x01 }));
                    break;
                case 0x2:
                    if (Requests.TryGetValue(target, out var request))
                    {
                        int count = Math.Min(request.buffer.Length - request.received, isoTp.Length - 1);
                        Array.Copy(isoTp, 1, request.buffer, request.received, count);
                        request.received += count;
                        Requests[target] = request;
                        if (request.received >= request.buffer.Length)
                        {
                            Requests.Remove(target);
                            Answer(target, request.buffer);
                        }
                    }
                    break;
                case 0x3:
                    if (PendingAnswers.TryGetValue(target, out var frames))
                    {
                        while (frames.Count > 0)
                        {
                            Emit(DCanTransport.ResponseBase + target, frames.Dequeue());
                        }
                        PendingAnswers.Remove(target);
                    }
                    break;
            }
        }

        void Answer(byte target, byte[] request)
        {
            if (target != Addresses.Functional && !Car.Has(target))
            {
                // nobody there: on a real bus the request just goes unanswered
                return;
            }
            foreach (var (source, data) in Car.Handle(target, request))
            {
                uint id = DCanTransport.ResponseBase + source;
                byte tester = DCanTransport.TesterAddress;
                if (data.Length <= 6)
                {
                    Emit(id, Pad(new[] { tester, (byte)data.Length }.Concat(data)));
                    continue;
                }
                Emit(id, Pad(new[] { tester, (byte)(0x10 | data.Length >> 8), (byte)data.Length }.Concat(data.Take(5))));
                var rest = new Queue<byte[]>();
                byte sequence = 1;
                for (int offset = 5; offset < data.Length; offset += 6)
                {
                    rest.Enqueue(Pad(new[] { tester, (byte)(0x20 | sequence) }.Concat(data.Skip(offset).Take(6))));
                    sequence = (byte)((sequence + 1) & 0x0F);
                }
                PendingAnswers[source] = rest;
            }
        }

        static byte[] Pad(IEnumerable<byte> bytes)
        {
            var frame = bytes.ToList();
            while (frame.Count < 8)
            {
                frame.Add(0x00);
            }
            return frame.ToArray();
        }

        void Emit(uint id, byte[] data) => Outgoing.Add((id, data));

        public List<(uint id, byte[] data)> Receive(int timeoutMs)
        {
            var frames = new List<(uint, byte[])>();
            if (Outgoing.TryTake(out var first, timeoutMs))
            {
                frames.Add(first);
                while (frames.Count < 16 && Outgoing.TryTake(out var more))
                {
                    frames.Add(more);
                }
            }
            return frames;
        }

        public void Dispose()
        {
        }
    }
}
