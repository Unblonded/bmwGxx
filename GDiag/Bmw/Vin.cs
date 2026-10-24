using System.Collections.Generic;

namespace GDiag.Bmw
{
    // what can be read straight out of a VIN without any BMW data
    public static class Vin
    {
        static readonly Dictionary<string, string> Makers = new Dictionary<string, string>
        {
            ["WBA"] = "BMW AG (passenger cars)",
            ["WBS"] = "BMW M GmbH",
            ["WBY"] = "BMW i",
            ["WBX"] = "BMW (SAV, built in Germany/Austria)",
            ["5UX"] = "BMW Manufacturing, Spartanburg USA (SAV)",
            ["5YM"] = "BMW M, Spartanburg USA",
            ["LBV"] = "BMW Brilliance, China",
            ["WMW"] = "MINI",
        };

        const string YearLetters = "ABCDEFGHJKLMNPRSTVWXY123456789";

        public static string Maker(string vin)
        {
            if (vin is null || vin.Length < 3)
            {
                return "";
            }
            return Makers.TryGetValue(vin.Substring(0, 3), out var maker) ? maker : "Unknown maker";
        }

        // the 10th character repeats every 30 years; anything with ENET is 2010 or later
        public static int? ModelYear(string vin)
        {
            if (vin is null || vin.Length != 17)
            {
                return null;
            }
            int index = YearLetters.IndexOf(vin[9]);
            if (index < 0)
            {
                return null;
            }
            return 2010 + index;
        }

        // BMW identifies a car by the last 7 characters (the "short VIN" on keys and in ISTA)
        public static string Short(string vin) => vin != null && vin.Length == 17 ? vin.Substring(10) : vin;
    }
}
