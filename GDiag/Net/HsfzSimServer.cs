using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace GDiag.Net
{
    // Serves a SimVehicle over real HSFZ on TCP (6801) and answers discovery on UDP (6811), so the ENET
    // code can be tried end to end without a car: "GDiag --sim-server", then connect to 127.0.0.1.
    public class HsfzSimServer : IDisposable
    {
        readonly SimVehicle Car;
        readonly TcpListener Listener;
        readonly UdpClient Discovery;
        volatile bool running = true;

        public int TcpPort => ((IPEndPoint)Listener.LocalEndpoint).Port;
        public int UdpPort => Discovery is null ? 0 : ((IPEndPoint)Discovery.Client.LocalEndPoint).Port;

        // sends an alive check this often, to exercise the client's answer (0 = never)
        public int AliveCheckIntervalMs = 5000;

        public HsfzSimServer(SimVehicle car, int tcpPort = Hsfz.DiagPort, int udpPort = Hsfz.DiscoveryPort, IPAddress bind = null)
        {
            Car = car;
            Listener = new TcpListener(bind ?? IPAddress.Loopback, tcpPort);
            Listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "HSFZ sim accept" }.Start();
            try
            {
                // same address as the TCP side, so the address discovery reports is one that can be connected to
                Discovery = new UdpClient(new IPEndPoint(bind ?? IPAddress.Loopback, udpPort));
                new Thread(DiscoveryLoop) { IsBackground = true, Name = "HSFZ sim discovery" }.Start();
            }
            catch (SocketException ex)
            {
                Console.WriteLine($"Simulator discovery port {udpPort} is not available ({ex.Message}), connect by address instead.");
            }
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = Listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                new Thread(() => Serve(client)) { IsBackground = true, Name = "HSFZ sim client" }.Start();
            }
        }

        void Serve(TcpClient client)
        {
            Console.WriteLine($"Simulator: tester connected from {client.Client.RemoteEndPoint}");
            var sendLock = new object();
            using (client)
            {
                var stream = client.GetStream();
                void Write(byte[] frame)
                {
                    lock (sendLock)
                    {
                        stream.Write(frame, 0, frame.Length);
                    }
                }

                var alive = AliveCheckIntervalMs > 0 ? new Timer(_ =>
                {
                    try
                    {
                        Write(Hsfz.Frame(Hsfz.ControlAliveCheck, null));
                    }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException)
                    {
                        // client went away, the read loop notices
                    }
                }, null, AliveCheckIntervalMs, AliveCheckIntervalMs) : null;

                try
                {
                    while (running && Hsfz.ReadFrame(stream, out ushort control, out byte[] payload))
                    {
                        if (control == Hsfz.ControlAliveCheckResponse)
                        {
                            continue;
                        }
                        if (control != Hsfz.ControlDiag)
                        {
                            Write(Hsfz.Frame(Hsfz.ControlBadControlWord, null));
                            continue;
                        }
                        if (payload.Length < 3)
                        {
                            Write(Hsfz.Frame(Hsfz.ControlBadFormat, payload));
                            continue;
                        }
                        byte source = payload[0];
                        byte target = payload[1];
                        if (target != Addresses.Functional && !Car.Has(target))
                        {
                            Write(Hsfz.Frame(Hsfz.ControlBadTargetAddress, new[] { source, target }));
                            continue;
                        }
                        // the gateway first acknowledges by echoing the request
                        Write(Hsfz.Frame(Hsfz.ControlAck, payload));
                        var request = new byte[payload.Length - 2];
                        Buffer.BlockCopy(payload, 2, request, 0, request.Length);
                        foreach (var (from, data) in Car.Handle(target, request))
                        {
                            Write(Hsfz.DiagFrame(from, source, data));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidDataException)
                {
                    // disconnected
                }
                alive?.Dispose();
            }
            Console.WriteLine("Simulator: tester disconnected");
        }

        void DiscoveryLoop()
        {
            var answer = Hsfz.Frame(Hsfz.ControlVehicleIdent, Encoding.ASCII.GetBytes(Car.IdentificationText));
            while (running)
            {
                try
                {
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] datagram = Discovery.Receive(ref from);
                    if (datagram.Length == Hsfz.HeaderSize && Hex.BigEndian(datagram, 4, 2) == Hsfz.ControlVehicleIdent)
                    {
                        Discovery.Send(answer, answer.Length, from);
                    }
                }
                catch (SocketException)
                {
                    if (!running)
                    {
                        return;
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }

        public void Dispose()
        {
            running = false;
            Listener.Stop();
            Discovery?.Close();
        }
    }
}
