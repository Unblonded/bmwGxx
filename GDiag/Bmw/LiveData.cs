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
        public string Group = "Engine";
        public int Decimals = 1;
        // what it means and what is normal, shown on hover
        public string Help;
        // a value worked out from others (by Id) instead of read from the car
        public string[] Inputs;
        public Func<Func<string, double>, double> Compute;

        // whole record length when known, so several values can share one request (0 = read alone)
        public int RecordLength;

        public bool IsComputed => Compute != null;
        public bool IsObd => Module == 0x12 && (Did & 0xFF00) == 0xF400 && !IsComputed;
        // several of these from one module can share a request
        public bool Batchable => !IsComputed && RecordLength > 0;

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
                Group = "Your values",
                Decimals = 2,
            };
            value.Id = $"custom:{text}";
            value.Max = Math.Max(1, (Math.Pow(256, value.ByteCount) - 1) * Math.Abs(value.Scale) + value.Offset);
            return value;
        }
    }

    public static class LiveData
    {
        public const string Boost = "boost";

        // record length of the mode 01 PIDs used here (SAE J1979-DA), needed to split a reply that carries several
        static readonly Dictionary<byte, int> PidLength = new Dictionary<byte, int>
        {
            [0x04] = 1, [0x05] = 1, [0x06] = 1, [0x07] = 1, [0x0A] = 1, [0x0B] = 1, [0x0C] = 2, [0x0D] = 1, [0x0E] = 1,
            [0x0F] = 1, [0x10] = 2, [0x11] = 1, [0x15] = 2, [0x1F] = 2, [0x21] = 2, [0x23] = 2, [0x2E] = 1, [0x2F] = 1,
            [0x30] = 1, [0x31] = 2, [0x33] = 1, [0x34] = 4, [0x3C] = 2, [0x3E] = 2, [0x42] = 2, [0x43] = 2, [0x44] = 2,
            [0x45] = 1, [0x46] = 1, [0x49] = 1, [0x4A] = 1, [0x4C] = 1, [0x5C] = 1, [0x62] = 1, [0x63] = 2, [0x67] = 3, [0x68] = 7,
            [0x8E] = 1, [0x9D] = 4, [0xA6] = 4,
        };

        // (declared before the tables that use it: static fields are set in order)

        // The engine computer's values, read as UDS DIDs F4xx (the OBD-II mode 01 PIDs mapped onto UDS by
        // SAE J1979 / ISO 27145). Over D-CAN a DME that refuses one is asked as plain OBD-II mode 01.
        // Which ones a car has is read from the support bitmaps (PIDs 00, 20, 40 ...) when live data starts.
        public static readonly LiveValue[] Engine =
        {
            // engine
            Obd("rpm", "Engine speed", 0x0C, 2, 0.25, 0, "rpm", 0, 7500, "Engine", 0),
            Obd("load", "Calculated engine load", 0x04, 1, 100.0 / 255, 0, "%", 0, 100, "Engine"),
            Obd("absload", "Absolute engine load", 0x43, 2, 100.0 / 255, 0, "%", 0, 300, "Engine"),
            Obd("torque", "Actual engine torque", 0x62, 1, 1, -125, "%", -25, 130, "Engine", 0,
                "Percent of the reference torque (PID 63). Above 100 % on overboost."),
            Computed("torquenm", "Engine torque", "Nm", -50, 600, "Engine", new[] { "torque", "reftorque" },
                v => v("torque") * v("reftorque") / 100,
                "Actual torque (percent) times the engine's reference torque. The DME's own estimate, not a dyno figure."),
            Computed("power", "Engine power (estimate)", "kW", 0, 300, "Engine", new[] { "torque", "reftorque", "rpm" },
                v => Math.Max(0, v("torque") * v("reftorque") / 100 * v("rpm") / 9549),
                "Torque times engine speed. 1 kW = 1.34 hp."),
            Obd("reftorque", "Reference torque", 0x63, 2, 1, 0, "Nm", 0, 1000, "Engine", 0,
                "The engine's 100 % torque figure, fixed for each engine."),
            Obd("friction", "Engine friction torque", 0x8E, 1, 1, -125, "%", 0, 30, "Engine", 0),
            Obd("timing", "Ignition timing advance", 0x0E, 1, 0.5, -64, "°", -20, 50, "Engine", 1,
                "Before top dead centre, cylinder 1. Drops when the engine pulls timing for knock."),
            Obd("runtime", "Time since engine start", 0x1F, 2, 1, 0, "s", 0, 3600, "Engine", 0),

            // air and boost
            Computed(Boost, "Boost pressure", "bar", -1, 1.6, "Air & boost", new[] { "map", "baro" },
                v => (v("map") - v("baro")) / 100,
                "Manifold pressure minus the outside air pressure, so 0 is atmospheric and negative is vacuum. " +
                "From standard OBD, which tops out at 2.55 bar absolute (about 1.6 bar of boost)."),
            Obd("map", "Intake manifold pressure (absolute)", 0x0B, 1, 1, 0, "kPa", 0, 255, "Air & boost", 0,
                "Absolute pressure after the throttle. With Valvetronic it stays near the outside pressure at idle."),
            Obd("baro", "Barometric pressure", 0x33, 1, 1, 0, "kPa", 70, 110, "Air & boost", 0),
            Obd("maf", "Air mass flow", 0x10, 2, 0.01, 0, "g/s", 0, 400, "Air & boost"),
            Obd("intake", "Intake air temperature", 0x0F, 1, 1, -40, "°C", -40, 80, "Air & boost", 0),
            Obd("intake2", "Charge air temperature (sensor A)", 0x68, 1, 1, -40, "°C", -40, 100, "Air & boost", 0, null, 1),
            Obd("pedal", "Accelerator pedal", 0x49, 1, 100.0 / 255, 0, "%", 0, 100, "Air & boost", 1,
                "Pedal sensor D. About 15 % at rest and about 80 % at full pedal: the sensor never reads 0 or 100."),
            Obd("pedal2", "Accelerator pedal (sensor E)", 0x4A, 1, 100.0 / 255, 0, "%", 0, 100, "Air & boost", 1),
            Obd("throttle", "Throttle opening", 0x45, 1, 100.0 / 255, 0, "%", 0, 100, "Air & boost", 1,
                "Relative throttle position: 0 % closed, 100 % wide open."),
            Obd("throttleabs", "Throttle sensor (absolute)", 0x11, 1, 100.0 / 255, 0, "%", 0, 100, "Air & boost", 1,
                "Raw sensor value. Reads about 12 % with the throttle closed, which is normal."),
            Obd("throttlecmd", "Throttle commanded", 0x4C, 1, 100.0 / 255, 0, "%", 0, 100, "Air & boost", 1),

            // fuel
            Obd("fuel", "Fuel level", 0x2F, 1, 100.0 / 255, 0, "%", 0, 100, "Fuel", 0),
            Obd("railpressure", "Fuel rail pressure (high pressure)", 0x23, 2, 0.1, 0, "bar", 0, 350, "Fuel", 0,
                "Direct injection pressure, up to 350 bar on current BMW petrol engines."),
            Obd("lowpressure", "Fuel pressure (low side)", 0x0A, 1, 0.03, 0, "bar", 0, 7.65, "Fuel", 2,
                "From the in-tank pump, about 5 bar."),
            Obd("stft", "Short term fuel trim", 0x06, 1, 100.0 / 128, -100, "%", -25, 25, "Fuel", 1,
                "How much the DME is correcting the mixture right now. Positive = adding fuel (running lean)."),
            Obd("ltft", "Long term fuel trim", 0x07, 1, 100.0 / 128, -100, "%", -25, 25, "Fuel", 1,
                "Learned correction. Beyond about ±10 % points to a vacuum leak, injector or sensor problem."),
            Obd("lambdacmd", "Commanded lambda", 0x44, 2, 2.0 / 65536, 0, "λ", 0.7, 1.3, "Fuel", 3,
                "1.00 is stoichiometric. Below 1 is rich, which the engine does on full load to cool the turbo."),
            Obd("lambda", "Measured lambda (pre-cat sensor)", 0x34, 2, 2.0 / 65536, 0, "λ", 0.7, 1.3, "Fuel", 3),
            Obd("o2post", "Post-cat O2 sensor", 0x15, 1, 0.005, 0, "V", 0, 1.275, "Fuel", 3,
                "Should sit steady around 0.6-0.8 V once warm; a sensor that swings like the front one means a tired catalyst."),
            Obd("purge", "Evap purge commanded", 0x2E, 1, 100.0 / 255, 0, "%", 0, 100, "Fuel", 0),
            Obd("fuelrate", "Fuel flow", 0x9D, 2, 0.02, 0, "g/s", 0, 30, "Fuel", 2),
            Computed("consumption", "Fuel consumption", "l/h", 0, 40, "Fuel", new[] { "fuelrate" },
                v => v("fuelrate") * 3600 / 745,
                "Fuel flow in litres per hour (petrol at 745 g/l). About 0.6-1 l/h at idle."),

            // temperatures
            Obd("coolant", "Coolant temperature", 0x05, 1, 1, -40, "°C", -40, 130, "Temperatures", 0,
                "BMWs run hot on purpose: up to about 110 °C when cruising is normal."),
            // most BMW DMEs don't give the oil temperature as standard OBD, so it is read the BMW way
            new LiveValue
            {
                Id = "oil", Name = "Engine oil temperature", Module = 0x12, Did = 0x4D80, ByteCount = 2, RecordLength = 2,
                Scale = 0.1, Offset = -273.15, Unit = "°C", Min = -40, Max = 150, Group = "Temperatures", Decimals = 0,
                Help = "BMW DME value 4D80 (tenths of a kelvin), found on a G20 with the B48 engine; other engines may use another DID. " +
                       "Normal is about 90-110 °C once warm. Don't use full throttle below about 70 °C.",
            },
            Obd("oilobd", "Engine oil temperature (OBD)", 0x5C, 1, 1, -40, "°C", -40, 150, "Temperatures", 0),
            Obd("coolant2", "Coolant temperature (sensor A)", 0x67, 1, 1, -40, "°C", -40, 130, "Temperatures", 0, null, 1),
            Obd("cat1", "Catalyst temperature (front)", 0x3C, 2, 0.1, -40, "°C", 0, 1000, "Temperatures", 0),
            Obd("cat2", "Catalyst temperature (rear)", 0x3E, 2, 0.1, -40, "°C", 0, 1000, "Temperatures", 0),
            Obd("ambient", "Outside temperature", 0x46, 1, 1, -40, "°C", -40, 60, "Temperatures", 0),

            // electrical and driving
            Obd("voltage", "Control module voltage", 0x42, 2, 0.001, 0, "V", 10, 16, "Electrical & driving", 2,
                "About 12.4-12.7 V with the engine off, 13.5-15 V running. Below 12 V: charge the battery before coding."),
            Obd("speed", "Vehicle speed", 0x0D, 1, 1, 0, "km/h", 0, 260, "Electrical & driving", 0),
            Obd("odometer", "Odometer", 0xA6, 4, 0.1, 0, "km", 0, 500000, "Electrical & driving", 0),
            Obd("sinceclear", "Distance since faults were cleared", 0x31, 2, 1, 0, "km", 0, 65535, "Electrical & driving", 0),
            Obd("warmups", "Warm-ups since faults were cleared", 0x30, 1, 1, 0, "", 0, 255, "Electrical & driving", 0),
            Obd("milkm", "Distance with check engine light on", 0x21, 2, 1, 0, "km", 0, 65535, "Electrical & driving", 0),
        };

        public static IEnumerable<LiveValue> All => Engine;

        // the gauges shown when the user hasn't picked any
        public static readonly string[] DefaultGauges = { "rpm", Boost, "coolant", "oil", "intake", "pedal", "voltage", "speed" };

        static LiveValue Obd(string id, string name, byte pid, int bytes, double scale, double offset, string unit, double min, double max,
            string group, int decimals = 1, string help = null, int byteOffset = 0) =>
            new LiveValue
            {
                Id = id, Name = name, Module = 0x12, Did = (ushort)(0xF400 | pid), ByteCount = bytes, ByteOffset = byteOffset,
                Scale = scale, Offset = offset, Unit = unit, Min = min, Max = max, Group = group, Decimals = decimals, Help = help,
                RecordLength = PidLength.TryGetValue(pid, out int length) ? length : 0,
            };


        static LiveValue Computed(string id, string name, string unit, double min, double max, string group, string[] inputs,
            Func<Func<string, double>, double> compute, string help) =>
            new LiveValue { Id = id, Name = name, Unit = unit, Min = min, Max = max, Group = group, Inputs = inputs, Compute = compute, Help = help, Decimals = 2 };

        // the OBD PIDs a support bitmap (the record of PID 00, 20, 40 ...) says the engine has
        public static IEnumerable<byte> SupportedPids(byte basePid, byte[] bitmap)
        {
            for (int i = 0; bitmap != null && i < 32 && i / 8 < bitmap.Length; i++)
            {
                if ((bitmap[i / 8] & (0x80 >> (i % 8))) != 0)
                {
                    yield return (byte)(basePid + i + 1);
                }
            }
        }

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
                case "bar": return (value * 14.5038, "psi");
                case "km": return (value * 0.621371, "mi");
                case "Nm": return (value * 0.737562, "lb-ft");
                case "kW": return (value * 1.34102, "hp");
                case "l/h": return (value * 0.264172, "gal/h");
                case "g/s": return (value * 0.132277, "lb/min");
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
