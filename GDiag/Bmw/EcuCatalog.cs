using System.Collections.Generic;
using System.Linq;

namespace GDiag.Bmw
{
    public class EcuInfo
    {
        public byte Address;
        public string Short;
        public string Name;
        public string Description;
    }

    // Diagnostic addresses of the control units commonly found in F and G series cars. Which ones exist
    // depends on the model and options, so the scan asks each address; a unit that answers at an address
    // not listed here shows as "Module 0xNN".
    public static class EcuCatalog
    {
        static readonly EcuInfo[] Known =
        {
            E(0x01, "ACSM", "Crash safety module", "Airbags, belt tensioners and crash detection"),
            E(0x0D, "KAFAS", "Driver assistance camera", "Lane departure, speed limit info and emergency braking camera (older placement)"),
            E(0x0E, "SME", "High-voltage battery", "Battery management for hybrids and EVs"),
            E(0x10, "ZGW", "Central gateway", "Routes diagnostics between the ENET port and the car's buses; built into the BDC on G series"),
            E(0x12, "DME", "Engine control", "Digital Motor Electronics: fuel, ignition, boost and emissions"),
            E(0x13, "DME2", "Engine control 2", "Second engine computer on large engines and some hybrids"),
            E(0x18, "EGS", "Transmission control", "Automatic gearbox shifting and adaptation"),
            E(0x19, "VTG", "Transfer case", "xDrive front/rear torque split"),
            E(0x1C, "ICM", "Chassis management", "Combines steering, braking and suspension signals"),
            E(0x29, "DSC", "Stability control", "ABS, traction and stability control, brake pressure"),
            E(0x2A, "EMF", "Electric parking brake", "Electromechanical parking brake"),
            E(0x30, "EPS", "Electric power steering", "Steering assist motor and steering angle"),
            E(0x38, "EKPS", "Fuel pump control", "Electric fuel pump speed"),
            E(0x40, "BDC", "Body domain controller", "Locks, lights, windows, wipers, comfort access and the vehicle order (FA)"),
            E(0x56, "FZD", "Roof function centre", "Interior lights, sunroof switch, SOS button"),
            E(0x5D, "KAFAS", "Driver assistance camera", "Lane departure, speed limit info and emergency braking camera"),
            E(0x60, "KOMBI", "Instrument cluster", "Speedometer display, warnings and service intervals"),
            E(0x63, "HU", "Head unit", "iDrive head unit (NBT Evo, MGU or EntryNav): navigation, media, CarPlay"),
            E(0x64, "PDC", "Park distance control", "Parking sensors and parking assistant"),
            E(0x67, "ZBE", "iDrive controller", "The rotary controller on the centre console"),
            E(0x6D, "SPEG", "Seat module (passenger)", "Seat memory and adjustment"),
            E(0x6E, "SPEF", "Seat module (driver)", "Seat memory and adjustment"),
            E(0x71, "AHM", "Trailer module", "Tow hitch and trailer lighting"),
            E(0x72, "FLE", "Headlight electronics", "LED / laser headlight driver"),
            E(0x78, "IHKA", "Climate control", "Automatic air conditioning and heating"),
        };

        static EcuInfo E(byte address, string shortName, string name, string description) =>
            new EcuInfo { Address = address, Short = shortName, Name = name, Description = description };

        static readonly Dictionary<byte, EcuInfo> ByAddress = Known.GroupBy(e => e.Address).ToDictionary(g => g.Key, g => g.First());

        public static IEnumerable<byte> KnownAddresses => ByAddress.Keys.OrderBy(a => a);

        // older names E-Sys still shows for the same modules
        static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            ["FEM"] = "BDC", ["NBT"] = "HU", ["MGU"] = "HU", ["ENTRYNAV"] = "HU", ["KOMBI"] = "KOMBI",
        };

        // by short name ("HU", "BDC"), or null
        public static EcuInfo LookupShort(string shortName)
        {
            shortName = shortName.ToUpperInvariant();
            if (Aliases.TryGetValue(shortName, out var alias))
            {
                shortName = alias;
            }
            return Known.FirstOrDefault(e => e.Short == shortName);
        }

        public static EcuInfo Lookup(byte address)
        {
            if (ByAddress.TryGetValue(address, out var info))
            {
                return info;
            }
            return new EcuInfo { Address = address, Short = $"0x{address:X2}", Name = $"Module 0x{address:X2}", Description = "Not in the built-in list" };
        }
    }
}
