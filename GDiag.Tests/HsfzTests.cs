using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using GDiag.Net;
using GDiag.Uds;
using Xunit;

namespace GDiag.Tests
{
    // The real HSFZ client talking TCP to the simulator on a random local port.
    public class HsfzTests
    {
        static HsfzSimServer StartServer() => new HsfzSimServer(new SimVehicle(), tcpPort: 0, udpPort: 0) { AliveCheckIntervalMs = 0 };

        [Fact]
        public void FrameLayoutMatchesHsfz()
        {
            byte[] frame = Hsfz.DiagFrame(0xF4, 0x10, new byte[] { 0x22, 0xF1, 0x90 });
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x05, 0x00, 0x01, 0xF4, 0x10, 0x22, 0xF1, 0x90 }, frame);

            Assert.True(Hsfz.ReadFrame(new MemoryStream(frame), out ushort control, out byte[] payload));
            Assert.Equal(Hsfz.ControlDiag, control);
            Assert.Equal(new byte[] { 0xF4, 0x10, 0x22, 0xF1, 0x90 }, payload);
        }

        [Fact]
        public void ReadsVinOverTcp()
        {
            using var server = StartServer();
            using var session = new CarSession(new HsfzClient("127.0.0.1", server.TcpPort));
            Assert.Equal(SimVehicle.DemoVin, session.ReadVin());
            // the gateway acknowledged the request before answering
            Assert.True(((HsfzClient)session.Uds.Transport).AckCount >= 1);
        }

        [Fact]
        public void ScanFindsModulesAndParsesSvk()
        {
            using var server = StartServer();
            using var session = new CarSession(new HsfzClient("127.0.0.1", server.TcpPort));
            var modules = session.Scan(fullScan: false);
            Assert.Contains(modules, m => m.Address == 0x12 && m.Info.Short == "DME");
            Assert.Equal(11, modules.Count);
            var gateway = modules.Single(m => m.Address == 0x10);
            Assert.Equal("CAFD_0000341F_005_034_014", gateway.Cafd);
        }

        [Fact]
        public void UnknownAddressIsReportedNotTimedOut()
        {
            using var server = StartServer();
            using var client = new HsfzClient("127.0.0.1", server.TcpPort);
            var uds = new UdsClient(client);
            var response = uds.Request(0x55, 0x22, 0xF1, 0x90);
            Assert.False(response.Positive);
            Assert.Equal(Hsfz.DescribeError(Hsfz.ControlBadTargetAddress), response.Error);
        }

        [Fact]
        public void ClearWaitsThroughResponsePending()
        {
            using var server = StartServer();
            using var session = new CarSession(new HsfzClient("127.0.0.1", server.TcpPort));
            var dme = session.Scan(false).Single(m => m.Address == 0x12);
            session.ReadDtcs(dme);
            Assert.Equal(2, dme.Dtcs.Count);
            Assert.Equal("480A14", dme.Dtcs[0].CodeText);
            Assert.True(dme.Dtcs[0].TestFailed);

            var cleared = session.ClearDtcs(dme);
            Assert.True(cleared.Positive);
            Assert.Empty(dme.Dtcs);
        }

        [Fact]
        public void NegativeResponseIsDecoded()
        {
            using var server = StartServer();
            using var client = new HsfzClient("127.0.0.1", server.TcpPort);
            var response = new UdsClient(client).Request(0x40, 0x22, 0x12, 0x34);
            Assert.Equal(0x31, response.Nrc);
            Assert.Contains("out of range", response.Describe());
        }

        [Fact]
        public void AnswersAliveCheck()
        {
            using var server = new HsfzSimServer(new SimVehicle(), tcpPort: 0, udpPort: 0) { AliveCheckIntervalMs = 0 };
            // a raw socket plays the gateway side check: send an alive check to the client from a fake gateway
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new HsfzClient("127.0.0.1", port);
            using var gateway = listener.AcceptTcpClient();
            var stream = gateway.GetStream();
            byte[] alive = Hsfz.Frame(Hsfz.ControlAliveCheck, null);
            stream.Write(alive, 0, alive.Length);
            stream.ReadTimeout = 2000;
            Assert.True(Hsfz.ReadFrame(stream, out ushort control, out byte[] payload));
            Assert.Equal(Hsfz.ControlAliveCheckResponse, control);
            Assert.Equal(new byte[] { 0x00, 0xF4 }, payload);
            listener.Stop();
        }

        [Fact]
        public void ParsesDiscoveryAnswer()
        {
            string text = new SimVehicle().IdentificationText;
            byte[] datagram = Hsfz.Frame(Hsfz.ControlVehicleIdent, Encoding.ASCII.GetBytes(text));
            var car = Discovery.Parse(datagram, "169.254.10.20");
            Assert.Equal(SimVehicle.DemoVin, car.Vin);
            Assert.Equal(SimVehicle.DemoMac, car.Mac);
            Assert.Equal("10", car.DiagAddress);
            // our own empty broadcast echoed back is ignored
            Assert.Null(Discovery.Parse(Hsfz.Frame(Hsfz.ControlVehicleIdent, null), "127.0.0.1"));
        }
    }
}
