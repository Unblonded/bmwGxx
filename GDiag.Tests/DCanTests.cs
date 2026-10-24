using System;
using System.IO;
using System.Linq;
using GDiag.Bmw;
using GDiag.Net;
using GDiag.Uds;
using Xunit;

namespace GDiag.Tests
{
    // The D-CAN (OpenPort / J2534) transport against the simulated bus, frame by frame.
    public class DCanTests
    {
        [Fact]
        public void SingleFrameUsesBmwExtendedAddressing()
        {
            var bus = new SimCanBus(new SimVehicle());
            using var session = new CarSession(new DCanTransport(bus));
            Assert.Equal(SimVehicle.DemoVin, session.ReadVin());

            // 22 F1 90 to the gateway: id 0x6F1, [target][SF length 3][22 F1 90], padded to 8 bytes
            var request = bus.Sent.First();
            Assert.Equal(0x6F1u, request.id);
            Assert.Equal(new byte[] { 0x10, 0x03, 0x22, 0xF1, 0x90, 0x00, 0x00, 0x00 }, request.data);
            // the 20 byte VIN answer is multi-frame, so the tester sent flow control to the gateway
            Assert.Contains(bus.Sent, f => f.id == 0x6F1 && f.data[0] == 0x10 && f.data[1] == 0x30);
        }

        [Fact]
        public void ScanAndSvkOverDCan()
        {
            using var session = new CarSession(new DCanTransport(new SimCanBus(new SimVehicle())));
            var modules = session.Scan(fullScan: false);
            Assert.Equal(11, modules.Count);
            Assert.Equal("CAFD_00004C20_002_012_001", modules.Single(m => m.Address == 0x12).Cafd);
        }

        [Fact]
        public void MultiFrameRequestWaitsForFlowControl()
        {
            var bus = new SimCanBus(new SimVehicle());
            using var transport = new DCanTransport(bus);
            var uds = new UdsClient(transport);
            // 12 bytes: first frame with 5, then consecutive frames of 6 and 1
            var request = new byte[] { 0x31, 0x01, 0x12, 0x34, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
            var response = uds.Request(0x40, request);
            Assert.Equal(0x11, response.Nrc);

            var sent = bus.Sent.Where(f => f.id == 0x6F1 && f.data[0] == 0x40).Select(f => f.data).ToList();
            Assert.Equal(new byte[] { 0x40, 0x10, 0x0C, 0x31, 0x01, 0x12, 0x34, 0x01 }, sent[0]);
            Assert.Equal(new byte[] { 0x40, 0x21, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 }, sent[1]);
            Assert.Equal(new byte[] { 0x40, 0x22, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00 }, sent[2]);
        }

        [Fact]
        public void ClearFaultsThroughResponsePending()
        {
            using var session = new CarSession(new DCanTransport(new SimCanBus(new SimVehicle())));
            var dsc = session.Scan(false).Single(m => m.Address == 0x29);
            session.ReadDtcs(dsc);
            Assert.Single(dsc.Dtcs);
            Assert.True(session.ClearDtcs(dsc).Positive);
            Assert.Empty(dsc.Dtcs);
        }

        [Fact]
        public void MissingModuleTimesOut()
        {
            using var transport = new DCanTransport(new SimCanBus(new SimVehicle()));
            var uds = new UdsClient(transport) { TimeoutMs = 200 };
            Assert.True(uds.Request(0x55, 0x22, 0xF1, 0x90).TimedOut);
        }

        [Fact]
        public void EngineValuesFallBackToObd()
        {
            var bus = new SimCanBus(new SimVehicle());
            using var session = new CarSession(new DCanTransport(bus));
            // the simulated DME answers 22 F40C directly
            var rpm = LiveData.Engine.Single(v => v.Id == "rpm");
            Assert.NotNull(session.ReadLive(rpm, out _));

            // a standard OBD request on 0x7E0 also works
            var obd = session.Uds.Request(DCanTransport.ObdEngine, 0x01, 0x0C);
            Assert.True(obd.Positive);
            Assert.Equal(new byte[] { 0x41, 0x0C }, obd.Data.Take(2).ToArray());
            Assert.Contains(bus.Sent, f => f.id == 0x7E0 && f.data[0] == 0x02 && f.data[1] == 0x01 && f.data[2] == 0x0C);
        }

        [Fact]
        public void ObdOnlyValueUsesFallback()
        {
            using var session = new CarSession(new DCanTransport(new SimCanBus(new SimVehicle())));
            // PID 0x00 (supported PIDs) has no UDS DID in the simulator, only the OBD answer
            var supported = new LiveValue { Id = "pids", Name = "Supported PIDs", Module = 0x12, Did = 0xF400, ByteCount = 4 };
            byte[] record = session.ReadLive(supported, out var response);
            Assert.Equal(4, record.Length);
            // bit for PID 0C (engine speed)
            Assert.NotEqual(0, record[1] & 0x10);
            Assert.Equal(0x41, response.Data[0]);
        }

        [Fact]
        public void SeveralValuesShareOneRequest()
        {
            var bus = new SimCanBus(new SimVehicle());
            using var session = new CarSession(new DCanTransport(bus));
            var values = LiveData.Engine.Where(v => v.Id is "rpm" or "speed" or "coolant" or "oil" or "map").ToList();
            var records = session.ReadLiveBatch(values);
            Assert.Equal(5, records.Count);
            Assert.InRange(values.Single(v => v.Id == "oil").Decode(records[values.Single(v => v.Id == "oil")]).Value, 0, 120);
            // one 22 request went out for all five (a first frame: 11 bytes don't fit in one CAN frame)
            Assert.Single(bus.Sent, f => f.id == 0x6F1 && f.data[1] == 0x10 && f.data[3] == 0x22);
        }

        [Fact]
        public void SupportedPidsComeFromTheBitmaps()
        {
            using var session = new CarSession(new DCanTransport(new SimCanBus(new SimVehicle())));
            var pids = session.ReadSupportedPids();
            Assert.Contains((byte)0x0C, pids);
            Assert.Contains((byte)0xA6, pids);
            Assert.DoesNotContain((byte)0x5C, pids);
        }

        [Fact]
        public void CodingAndFaultDetails()
        {
            using var session = new CarSession(new DCanTransport(new SimCanBus(new SimVehicle())));
            var bdc = new ModuleState { Address = 0x40, Info = EcuCatalog.Lookup(0x40) };
            var blocks = session.ReadCoding(bdc);
            Assert.Equal(new ushort[] { 0x3000, 0x3001, 0x3002, 0x3010, 0x3020 }, blocks.Keys);

            session.ReadDtcs(bdc);
            var dtc = Assert.Single(bdc.Dtcs);
            session.ReadDtcDetails(dtc);
            Assert.Equal(new uint?[] { 21940, 24105 }, dtc.Details.Snapshots.Select(s => s.Mileage));
            Assert.Equal(4, dtc.Details.Occurrences);
        }

        [Fact]
        public void J2534DriverFileErrors()
        {
            string missing = Path.Combine(Path.GetTempPath(), "no-such-j2534.dll");
            Assert.Throws<FileNotFoundException>(() => new J2534Api(new J2534Driver { Name = "x", Library = missing, LongSize = 4 }));

            // a library added by hand is listed even without a registry entry
            string fake = Path.GetTempFileName();
            try
            {
                Assert.Contains(J2534Driver.Find(new[] { fake }), d => d.Library == fake);
                // ...but loading something that isn't a driver gives a clear error instead of crashing
                Assert.ThrowsAny<Exception>(() => new J2534Api(new J2534Driver { Name = "x", Library = fake, LongSize = IntPtr.Size == 8 && !OperatingSystem.IsWindows() ? 8 : 4 }));
            }
            finally
            {
                File.Delete(fake);
            }
        }
    }
}
