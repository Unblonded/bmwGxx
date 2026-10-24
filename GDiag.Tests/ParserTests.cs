using System.Linq;
using GDiag.Bmw;
using GDiag.Translation;
using GDiag.Uds;
using Xunit;

namespace GDiag.Tests
{
    public class ParserTests
    {
        [Fact]
        public void SvkEntries()
        {
            byte[] record = { 0x01, 0x01, 0x00, 0x02, 0x05, 0x00, 0x00, 0x34, 0x1F, 5, 34, 14, 0x08, 0x00, 0x00, 0xA1, 0xB2, 3, 1, 2 };
            var parts = Svk.Parse(record);
            Assert.Equal(new[] { "CAFD_0000341F_005_034_014", "SWFL_0000A1B2_003_001_002" }, parts.Select(p => p.ToString()));
            // a count that runs past the end is not an SVK
            Assert.Null(Svk.Parse(new byte[] { 0x01, 0x01, 0x00, 0x09, 0x05 }));
        }

        [Fact]
        public void DtcReportAndStatus()
        {
            byte[] response = { 0x59, 0x02, 0xFF, 0x48, 0x0A, 0x14, 0x2F, 0x21, 0xF0, 0x1C, 0x08 };
            var dtcs = Dtc.ParseReport(response, 0x12);
            Assert.Equal(2, dtcs.Count);
            Assert.Equal("480A14", dtcs[0].CodeText);
            Assert.Equal("Active now", dtcs[0].Summary);
            Assert.Equal("Stored (not present right now)", dtcs[1].Summary);
            Assert.Contains("confirmed and stored", dtcs[1].StatusBits());
        }

        [Fact]
        public void UsUnits()
        {
            Assert.Equal((212.0, "°F"), LiveData.ToDisplayUnits(100, "°C", us: true));
            var (psi, unit) = LiveData.ToDisplayUnits(1, "bar", us: true);
            Assert.Equal("psi", unit);
            Assert.Equal(14.5, psi, 1);
            Assert.Equal("lb-ft", LiveData.ToDisplayUnits(400, "Nm", us: true).unit);
            Assert.Equal("hp", LiveData.ToDisplayUnits(100, "kW", us: true).unit);
            Assert.Equal("mi", LiveData.ToDisplayUnits(100, "km", us: true).unit);
            // metric stays as the car sent it
            Assert.Equal((1.0, "bar"), LiveData.ToDisplayUnits(1, "bar", us: false));
        }

        [Fact]
        public void DtcReportSkipsUntestedCodes()
        {
            // 40 and 50 only say "not tested yet": a G20 DME lists over a hundred of those with mask FF
            byte[] response = { 0x59, 0x02, 0xFF, 0x03, 0x4A, 0x23, 0x50, 0x03, 0x4B, 0x1E, 0x40, 0x22, 0x4F, 0x95, 0x20 };
            var dtcs = Dtc.ParseReport(response, 0x12);
            Assert.Equal("224F95", Assert.Single(dtcs).CodeText);
            Assert.Equal("Happened since last clear", dtcs[0].Summary);
        }

        [Fact]
        public void DtcSnapshotsFromG20Bdc()
        {
            // 19 04 804081 FF from a real BDC: first and latest occurrence
            byte[] response = Hex.TryParse("59 04 80 40 81 0C 01 05 17 00 00 38 95 17 01 03 26 EE F7 17 50 55 17 51 11 02 00 41 02 03 " +
                                           "02 05 17 00 00 45 DE 17 01 03 87 33 94 17 50 55 17 51 11 02 01 41 02 03", out var r) ? r : null;
            var snapshots = DtcDetails.ParseSnapshots(response);
            Assert.Equal(2, snapshots.Count);
            Assert.Equal(14485u, snapshots[0].Mileage);
            Assert.Equal(17886u, snapshots[1].Mileage);
            Assert.Equal(5, snapshots[1].Values.Count);

            var details = new DtcDetails();
            DtcDetails.ParseExtended(new byte[] { 0x59, 0x06, 0x80, 0x40, 0x81, 0x0C, 0x01, 0x00, 0x02, 0x11, 0x03, 0xFF }, details);
            Assert.Equal(17, details.Occurrences);
        }

        [Fact]
        public void VehicleOrderStream()
        {
            var fa = VehicleOrder.Parse("G020#0719*5U31%A96&MAMU$205$2VB$S5ACA-ABCD+XYZ1");
            Assert.Equal("G020", fa.Series);
            Assert.Equal("3 Series Sedan", fa.SeriesName);
            Assert.Equal("5U31", fa.TypeKey);
            Assert.Equal("07/2019", fa.BuildDateText);
            Assert.Equal("A96", fa.Paint);
            Assert.Equal("MAMU", fa.Upholstery);
            Assert.Equal(new[] { "205", "2VB", "5AC" }, fa.Options);
            Assert.Equal("High-beam assistant", VehicleOrder.OptionName("5AC"));
            Assert.Equal(new[] { "ABCD" }, fa.EWords);
            Assert.Equal(new[] { "XYZ1" }, fa.HoWords);
        }

        [Fact]
        public void VehicleOrderXml()
        {
            string xml = @"<?xml version=""1.0""?>
<fa xmlns=""http://bmw.com/fa""><standardFA><series>G030</series><typeKey>JA11</typeKey><buildDate>0318</buildDate>
<vinLong>WBA5R1C50KAZ00001</vinLong><salapa>205</salapa><salapa>322</salapa><eCode>E1</eCode></standardFA></fa>";
            var fa = VehicleOrder.Parse(xml);
            Assert.Equal("5 Series Sedan", fa.SeriesName);
            Assert.Equal(new[] { "205", "322" }, fa.Options);
            Assert.Equal("WBA5R1C50KAZ00001", fa.Vin);
            Assert.Equal(new[] { "E1" }, fa.EWords);
        }

        [Fact]
        public void VinFacts()
        {
            Assert.Equal("BMW AG (passenger cars)", Vin.Maker("WBA5R1C50KAZ00001"));
            Assert.Equal(2019, Vin.ModelYear("WBA5R1C50KAZ00001"));
            Assert.Equal("KAZ00001".Substring(1), Vin.Short("WBA5R1C50KAZ00001"));
        }

        [Fact]
        public void LiveValueDecode()
        {
            var rpm = LiveData.Engine.Single(v => v.Id == "rpm");
            Assert.Equal(780, rpm.Decode(new byte[] { 0x0C, 0x30 }));
            var coolant = LiveData.Engine.Single(v => v.Id == "coolant");
            Assert.Equal(50, coolant.Decode(new byte[] { 90 }));
            Assert.Null(coolant.Decode(new byte[0]));

            var custom = LiveValue.Deserialize("40|D123|Door count|0.5|0|x|2|1");
            Assert.Equal(-0.5, custom.Decode(new byte[] { 0xFF, 0xFF }));
            Assert.Equal("40|D123|Door count|0.5|0|x|2|1", custom.Serialize());
        }

        [Fact]
        public void ReadOnlyServices()
        {
            Assert.True(ServiceSafety.IsReadOnly(new byte[] { 0x22, 0xF1, 0x90 }));
            Assert.True(ServiceSafety.IsReadOnly(new byte[] { 0x10, 0x01 }));
            Assert.False(ServiceSafety.IsReadOnly(new byte[] { 0x10, 0x03 }));
            Assert.False(ServiceSafety.IsReadOnly(new byte[] { 0x2E, 0x12, 0x34, 0x00 }));
            Assert.False(ServiceSafety.IsReadOnly(new byte[] { 0x14, 0xFF, 0xFF, 0xFF }));
        }

        [Theory]
        [InlineData("SPIEGEL_ANKLAPPEN_FB", "Fold mirror remote key")]
        [InlineData("nicht_aktiv", "not active")]
        [InlineData("TFL_HELLIGKEIT", "daytime running lights brightness")]
        public void TranslatesCodingNames(string german, string english)
        {
            Assert.Equal(english, CodingExplainer.ExplainLine(german).English, ignoreCase: true);
        }

        [Fact]
        public void ExplainsCheatSheetLine()
        {
            var line = CodingExplainer.ExplainLine("HU_MGU > 3000 Exbox > VIDEO_FAHRT = aktiv");
            Assert.Equal(CodingLineKind.Path, line.Kind);
            Assert.Equal("Head unit", line.ModuleName);
            Assert.StartsWith("HU_MGU (Head unit) > 3000", line.English);
            Assert.EndsWith("= active", line.English);

            Assert.Equal(CodingLineKind.Block, CodingExplainer.ExplainLine("3000 Exbox").Kind);
            Assert.Equal(CodingLineKind.Value, CodingExplainer.ExplainLine("    wert_01").Kind);
            Assert.Equal("option 01", CodingExplainer.ExplainLine("wert_01").English);
            // a parameter that starts with a module name is still a parameter
            Assert.Equal(CodingLineKind.Parameter, CodingExplainer.ExplainLine("PDC_GONG_LAUTSTAERKE").Kind);
        }
    }
}
