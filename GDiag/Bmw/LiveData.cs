using System;
using System.Collections.Generic;
using System.Globalization;

namespace GDiag.Bmw
{
    // a value read with 22 xx xx and turned into a number: raw * Scale + Offset
    public class LiveValue
    {
        public string Id;
        public string Name;
        public byte Module;
        public ushort Did;
        public int ByteOffset;
        public int ByteCount = 1;
        public bool Signed;
        public double Scale = 1;
        public double Offset;
        public string Unit = "";
        // gauge range for the dashboard
        public double Min;
        public double Max = 100;
        public string Icon;
        public bool Custom;

        public double? Decode(byte[] record)
        {
            if (record is null || record.Length < ByteOffset + ByteCount)
            {
                return null;
            }
            long raw = Hex.BigEndian(record, ByteOffset, ByteCount);
            if (Signed)
            {
                long signBit = 1L << (ByteCount * 8 - 1);
                if ((raw & signBit) != 0)
                {
                    raw -= signBit << 1;
                }
            }
            return raw * Scale + Offset;
        }

        // "address|did|name|scale|offset|unit|bytes|signed", see AppSettings.CustomDids
        public string Serialize() => string.Join("|", Module.ToString("X2"), Did.ToString("X4"), Name.Replace("|", "/"),
            Scale.ToString(CultureInfo.InvariantCulture), Offset.ToString(CultureInfo.InvariantCulture), Unit.Replace("|", "/"), ByteCount, Signed ? 1 : 0);

        public static LiveValue Deserialize(string text)
        {
            string[] p = text.Split('|');
            if (p.Length < 8 || !Hex.TryParseNumber(p[0], out uint module) || !Hex.TryParseNumber(p[1], out uint did))
            {
                return null;
            }
            var value = new LiveValue
            {
                Module = (byte)module,
                Did = (ushort)did,
                Name = p[2],
                Scale = double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : 1,
                Offset = double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double o) ? o : 0,
                Unit = p[5],
                ByteCount = int.TryParse(p[6], out int n) ? Math.Clamp(n, 1, 4) : 1,
                Signed = p[7] == "1",
                Custom = true,
            };
            value.Id = $"custom:{text}";
            value.Max = Math.Max(1, (Math.Pow(256, value.ByteCount) - 1) * Math.Abs(value.Scale) + value.Offset);
            return value;
        }
    }

    public static class LiveData
    {
        // The engine computer's emissions values, read as UDS DIDs F4xx (the OBD-II mode 01 PIDs mapped onto
        // UDS by SAE J1979 / ISO 27145). Not every DME answers these over ENET; a value it refuses shows as "-".
        public static readonly LiveValue[] Engine =
        {
            Obd("rpm", "Engine speed", 0x0C, 2, 0.25, 0, "rpm", 0, 7500),
            Obd("speed", "Vehicle speed", 0x0D, 1, 1, 0, "km/h", 0, 260),
            Obd("coolant", "Coolant temperature", 0x05, 1, 1, -40, "°C", -40, 130),
            Obd("oil", "Engine oil temperature", 0x5C, 1, 1, -40, "°C", -40, 150),
            Obd("intake", "Intake air temperature", 0x0F, 1, 1, -40, "°C", -40, 80),
            Obd("load", "Calculated engine load", 0x04, 1, 100.0 / 255, 0, "%", 0, 100),
            Obd("throttle", "Throttle position", 0x11, 1, 100.0 / 255, 0, "%", 0, 100),
            Obd("voltage", "Control module voltage", 0x42, 2, 0.001, 0, "V", 10, 16),
            Obd("map", "Intake manifold pressure", 0x0B, 1, 1, 0, "kPa", 0, 255),
            Obd("baro", "Barometric pressure", 0x33, 1, 1, 0, "kPa", 70, 110),
            Obd("maf", "Air mass flow", 0x10, 2, 0.01, 0, "g/s", 0, 400),
            Obd("timing", "Ignition timing advance", 0x0E, 1, 0.5, -64, "°", -20, 50),
            Obd("fuel", "Fuel level", 0x2F, 1, 100.0 / 255, 0, "%", 0, 100),
            Obd("ambient", "Outside temperature", 0x46, 1, 1, -40, "°C", -40, 60),
            Obd("runtime", "Time since engine start", 0x1F, 2, 1, 0, "s", 0, 3600),
        };

        static LiveValue Obd(string id, string name, byte pid, int bytes, double scale, double offset, string unit, double min, double max) =>
            new LiveValue { Id = id, Name = name, Module = 0x12, Did = (ushort)(0xF400 | pid), ByteCount = bytes, Scale = scale, Offset = offset, Unit = unit, Min = min, Max = max };

        // ISO 14229 identification DIDs, read from every module on the Vehicle tab
        public static readonly (ushort did, string name, bool ascii)[] Identification =
        {
            (0xF190, "VIN", true),
            (0xF187, "Part number", true),
            (0xF18A, "Supplier", true),
            (0xF18B, "Manufacturing date", false),
            (0xF18C, "Serial number", true),
            (0xF191, "Hardware number", true),
            (0xF195, "Software version", true),
            (0xF197, "System name", true),
            (0xF186, "Active session", false),
        };

        // converts metric readings for the dashboard when US units are chosen
        public static (double value, string unit) ToDisplayUnits(double value, string unit, bool us)
        {
            if (!us)
            {
                return (value, unit);
            }
            switch (unit)
            {
                case "°C": return (value * 9 / 5 + 32, "°F");
                case "km/h": return (value * 0.621371, "mph");
                case "kPa": return (value * 0.145038, "psi");
                default: return (value, unit);
            }
        }

        public static List<LiveValue> Custom(IEnumerable<string> serialized)
        {
            var list = new List<LiveValue>();
            foreach (string text in serialized)
            {
                var value = LiveValue.Deserialize(text);
                if (value != null)
                {
                    list.Add(value);
                }
            }
            return list;
        }
    }
}
