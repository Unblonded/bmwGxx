using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace GDiag.Net
{
    // HSFZ ("High Speed Fahrzeugzugang"), the protocol BMW F, G and I series gateways speak on the ENET port.
    // Every frame is a 6 byte header followed by the payload:
    //   [length: 4 bytes, big endian, payload only] [control word: 2 bytes] [payload]
    // For diagnostic messages the payload is [source address] [target address] [UDS bytes].
    public static class Hsfz
    {
        public const int DiagPort = 6801;
        public const int DiscoveryPort = 6811;
        public const int HeaderSize = 6;
        public const int MaxPayload = 0x10000;

        public const ushort ControlDiag = 0x0001;
        public const ushort ControlAck = 0x0002;
        public const ushort ControlVehicleIdent = 0x0011;
        public const ushort ControlAliveCheck = 0x0012;
        public const ushort ControlAliveCheckResponse = 0x0013;
        public const ushort ControlBadTesterAddress = 0x0040;
        public const ushort ControlBadControlWord = 0x0041;
        public const ushort ControlBadFormat = 0x0042;
        public const ushort ControlBadTargetAddress = 0x0043;
        public const ushort ControlTooLarge = 0x0044;
        public const ushort ControlNotReady = 0x0045;
        public const ushort ControlOutOfMemory = 0x00FF;

        public static string DescribeError(ushort control)
        {
            switch (control)
            {
                case ControlBadTesterAddress: return "The gateway rejected the tester address";
                case ControlBadControlWord: return "The gateway did not understand the request type";
                case ControlBadFormat: return "The gateway says the message was malformed";
                case ControlBadTargetAddress: return "No control unit at that address";
                case ControlTooLarge: return "Message too large for the gateway";
                case ControlNotReady: return "The gateway's diagnostics are not ready (is the ignition on?)";
                case ControlOutOfMemory: return "The gateway is out of memory";
                default: return null;
            }
        }

        public static byte[] Frame(ushort control, byte[] payload)
        {
            payload ??= Array.Empty<byte>();
            var frame = new byte[HeaderSize + payload.Length];
            frame[0] = (byte)(payload.Length >> 24);
            frame[1] = (byte)(payload.Length >> 16);
            frame[2] = (byte)(payload.Length >> 8);
            frame[3] = (byte)payload.Length;
            frame[4] = (byte)(control >> 8);
            frame[5] = (byte)control;
            Buffer.BlockCopy(payload, 0, frame, HeaderSize, payload.Length);
            return frame;
        }

        public static byte[] DiagFrame(byte source, byte target, byte[] data)
        {
            var payload = new byte[2 + data.Length];
            payload[0] = source;
            payload[1] = target;
            Buffer.BlockCopy(data, 0, payload, 2, data.Length);
            return Frame(ControlDiag, payload);
        }

        // reads one whole frame, or returns false when the connection closed
        public static bool ReadFrame(Stream stream, out ushort control, out byte[] payload)
        {
            control = 0;
            payload = null;
            var header = new byte[HeaderSize];
            if (!ReadExactly(stream, header, HeaderSize))
            {
                return false;
            }
            uint length = Hex.BigEndian(header, 0, 4);
            control = (ushort)Hex.BigEndian(header, 4, 2);
            if (length > MaxPayload)
            {
                throw new InvalidDataException($"HSFZ frame claims {length} bytes, the connection is out of step");
            }
            payload = new byte[length];
            return ReadExactly(stream, payload, (int)length);
        }

        static bool ReadExactly(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer, read, count - read);
                if (n <= 0)
                {
                    return false;
                }
                read += n;
            }
            return true;
        }
    }

    // TCP connection to the gateway on port 6801. A background thread reads frames so alive checks
    // are answered even while nothing is being requested.
    public class HsfzClient : IDiagTransport
    {
        readonly TcpClient Client;
        readonly NetworkStream Stream;
        readonly Thread Reader;
        readonly BlockingCollection<DiagMessage> Incoming = new BlockingCollection<DiagMessage>();
        readonly object SendLock = new object();
        volatile bool open;

        public string Host { get; }
        public int Port { get; }
        public string Description => $"ENET {Host}:{Port}";
        public bool IsOpen => open;

        // number of acknowledge frames seen, the gateway sends one for every request it accepts
        public int AckCount { get; private set; }

        public HsfzClient(string host, int port = Hsfz.DiagPort, int connectTimeoutMs = 3000)
        {
            Host = host;
            Port = port;
            Client = new TcpClient { NoDelay = true };
            if (!Client.ConnectAsync(host, port).Wait(connectTimeoutMs))
            {
                Client.Dispose();
                throw new TimeoutException($"No answer from {host}:{port}. Check the ENET cable and that the ignition is on.");
            }
            Stream = Client.GetStream();
            open = true;
            Reader = new Thread(ReadLoop) { IsBackground = true, Name = "HSFZ reader" };
            Reader.Start();
        }

        public void Send(byte target, byte[] data)
        {
            if (!open)
            {
                throw new IOException("The connection to the car is closed");
            }
            byte[] frame = Hsfz.DiagFrame(Addresses.Tester, target, data);
            lock (SendLock)
            {
                Stream.Write(frame, 0, frame.Length);
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

        void ReadLoop()
        {
            try
            {
                while (open && Hsfz.ReadFrame(Stream, out ushort control, out byte[] payload))
                {
                    switch (control)
                    {
                        case Hsfz.ControlDiag when payload.Length >= 2:
                            var data = new byte[payload.Length - 2];
                            Buffer.BlockCopy(payload, 2, data, 0, data.Length);
                            Incoming.Add(new DiagMessage { Source = payload[0], Target = payload[1], Data = data });
                            break;
                        case Hsfz.ControlAck:
                            AckCount++;
                            break;
                        case Hsfz.ControlAliveCheck:
                            // answer so the gateway keeps the connection open
                            byte[] reply = Hsfz.Frame(Hsfz.ControlAliveCheckResponse, new byte[] { 0x00, Addresses.Tester });
                            lock (SendLock)
                            {
                                Stream.Write(reply, 0, reply.Length);
                            }
                            break;
                        default:
                            string error = Hsfz.DescribeError(control);
                            if (error != null)
                            {
                                byte target = payload.Length >= 2 ? payload[1] : (byte)0;
                                Incoming.Add(new DiagMessage { Source = target, Target = Addresses.Tester, Error = error });
                            }
                            else
                            {
                                Console.WriteLine($"Ignored HSFZ frame with control word 0x{control:X4} ({payload.Length} bytes)");
                            }
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is SocketException)
            {
                // closed under us
            }
            catch (InvalidDataException ex)
            {
                Console.WriteLine(ex.Message);
            }
            if (open)
            {
                Console.WriteLine("The car closed the ENET connection.");
            }
            open = false;
        }

        public void Dispose()
        {
            open = false;
            try
            {
                Client.Close();
            }
            catch
            {
                // already gone
            }
            // Incoming is left alive so a request still waiting on another thread just times out
        }
    }
}
