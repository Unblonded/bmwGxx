using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace GDiag.Net
{
    public class DiscoveredCar
    {
        public string Address;
        public string Vin;
        public string Mac;
        public string DiagAddress;
        public string Raw;

        public override string ToString() => string.IsNullOrEmpty(Vin) ? Address : $"{Vin} at {Address}";
    }

    // Finds gateways on the ENET network: a vehicle identification request (HSFZ control word 0x0011,
    // no payload) broadcast to UDP 6811. Each gateway answers with text like
    // "DIAGADR10BMWMAC001A37AABBCCBMWVINWBA12345678901234".
    public static class Discovery
    {
        static readonly Regex VinPattern = new Regex("BMWVIN([A-HJ-NPR-Z0-9]{17})");
        static readonly Regex MacPattern = new Regex("BMWMAC([0-9A-Fa-f]{12})");
        static readonly Regex DiagPattern = new Regex("DIAGADR([0-9]+)");

        public static List<DiscoveredCar> Find(int listenMs = 1500, int port = Hsfz.DiscoveryPort)
        {
            var found = new Dictionary<string, DiscoveredCar>();
            byte[] request = Hsfz.Frame(Hsfz.ControlVehicleIdent, null);

            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            foreach (var target in BroadcastTargets())
            {
                try
                {
                    udp.Send(request, request.Length, new IPEndPoint(target, port));
                }
                catch (SocketException ex)
                {
                    Console.WriteLine($"Could not send discovery to {target}: {ex.Message}");
                }
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
            while (DateTime.UtcNow < deadline)
            {
                int left = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (left <= 0)
                {
                    break;
                }
                var receive = udp.ReceiveAsync();
                if (!receive.Wait(left))
                {
                    break;
                }
                var result = receive.Result;
                var car = Parse(result.Buffer, result.RemoteEndPoint.Address.ToString());
                if (car != null)
                {
                    found[car.Address] = car;
                }
            }
            return found.Values.ToList();
        }

        public static DiscoveredCar Parse(byte[] datagram, string fromAddress)
        {
            if (datagram.Length < Hsfz.HeaderSize || Hex.BigEndian(datagram, 4, 2) != Hsfz.ControlVehicleIdent)
            {
                return null;
            }
            // our own broadcast comes back on some systems: it has no payload
            if (datagram.Length == Hsfz.HeaderSize)
            {
                return null;
            }
            string text = Encoding.ASCII.GetString(datagram, Hsfz.HeaderSize, datagram.Length - Hsfz.HeaderSize);
            return new DiscoveredCar
            {
                Address = fromAddress,
                Vin = VinPattern.Match(text) is { Success: true } v ? v.Groups[1].Value : "",
                Mac = MacPattern.Match(text) is { Success: true } m ? m.Groups[1].Value : "",
                DiagAddress = DiagPattern.Match(text) is { Success: true } d ? d.Groups[1].Value : "",
                Raw = text,
            };
        }

        // the car gets a link-local 169.254.x.x address, so broadcast on every interface's subnet,
        // plus the loopback for the built-in simulator ("GDiag --sim-server")
        static IEnumerable<IPAddress> BroadcastTargets()
        {
            var targets = new List<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }
                    foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.IPv4Mask is null)
                        {
                            continue;
                        }
                        byte[] ip = unicast.Address.GetAddressBytes();
                        byte[] mask = unicast.IPv4Mask.GetAddressBytes();
                        var broadcast = new byte[4];
                        for (int i = 0; i < 4; i++)
                        {
                            broadcast[i] = (byte)(ip[i] | ~mask[i]);
                        }
                        targets.Add(new IPAddress(broadcast));
                    }
                }
            }
            catch (NetworkInformationException ex)
            {
                Console.WriteLine($"Could not list network adapters: {ex.Message}");
            }
            return targets.Distinct();
        }
    }
}
