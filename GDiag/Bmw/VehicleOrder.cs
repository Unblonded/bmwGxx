using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;

namespace GDiag.Bmw
{
    // The vehicle order (FA, "Fahrzeugauftrag"): series, type key, build date, paint, upholstery and
    // the option codes (SALAPA) the car was built with. Coding reads it to decide which features are on.
    // Loaded from an E-Sys / ISTA FA XML export, or from the FA stream text
    // "G020#0719*5U31%A96&MAMU$205$2VB-ABCD+XYZ1".
    public class VehicleOrder
    {
        public string Series = "";
        public string TypeKey = "";
        public string BuildDate = "";
        public string Paint = "";
        public string Upholstery = "";
        public string Vin = "";
        public List<string> Options = new List<string>();
        public List<string> EWords = new List<string>();
        public List<string> HoWords = new List<string>();

        public string SeriesName => SeriesNames.TryGetValue(NormalizeSeries(Series), out var name) ? name : "";

        // "07/19" from the FA's "0719"
        public string BuildDateText => BuildDate.Length == 4 ? $"{BuildDate.Substring(0, 2)}/20{BuildDate.Substring(2)}" : BuildDate;

        public static VehicleOrder Parse(string text)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("<"))
            {
                return ParseXml(text);
            }
            return ParseStream(text);
        }

        static VehicleOrder ParseXml(string xml)
        {
            var doc = XDocument.Parse(xml);
            var fa = new VehicleOrder();

            string First(params string[] names) => doc.Descendants()
                .FirstOrDefault(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase) && !e.HasElements)?.Value.Trim() ?? "";

            // a list is either repeated <salapa>205</salapa> or one <salapa> with child elements
            List<string> All(params string[] names) => doc.Descendants()
                .Where(e => names.Contains(e.Name.LocalName, StringComparer.OrdinalIgnoreCase))
                .SelectMany(e => e.HasElements ? e.Elements().Select(c => c.Value) : new[] { e.Value })
                .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList();

            fa.Series = First("series", "baureihe", "entwicklungsbaureihe");
            fa.TypeKey = First("typeKey", "typschluessel");
            fa.BuildDate = First("buildDate", "zeitkriterium");
            fa.Paint = First("lackCode", "paint", "lack");
            fa.Upholstery = First("polsterCode", "upholstery", "polster");
            fa.Vin = First("vinLong", "vin", "fgnr");
            fa.Options = All("salapa", "sa").Select(NormalizeOption).ToList();
            fa.EWords = All("eCode", "eWord", "eWort");
            fa.HoWords = All("hoCode", "hoWord", "hoWort");
            return fa;
        }

        static VehicleOrder ParseStream(string text)
        {
            var fa = new VehicleOrder();
            int hash = text.IndexOf('#');
            if (hash < 0)
            {
                throw new FormatException("This doesn't look like an FA: expected XML or text like G020#0719*5U31$205...");
            }
            fa.Series = text.Substring(0, hash).Trim();
            const string markers = "#*%&$-+";
            int i = hash;
            while (i < text.Length)
            {
                char marker = text[i];
                int end = text.IndexOfAny(markers.ToCharArray(), i + 1);
                if (end < 0)
                {
                    end = text.Length;
                }
                string value = text.Substring(i + 1, end - i - 1).Trim();
                switch (marker)
                {
                    case '#': fa.BuildDate = value; break;
                    case '*': fa.TypeKey = value; break;
                    case '%': fa.Paint = value; break;
                    case '&': fa.Upholstery = value; break;
                    case '$': if (value.Length > 0) fa.Options.Add(NormalizeOption(value)); break;
                    case '-': if (value.Length > 0) fa.EWords.Add(value); break;
                    case '+': if (value.Length > 0) fa.HoWords.Add(value); break;
                }
                i = end;
            }
            return fa;
        }

        // options are written "205", "S205" or "S205A" in different places
        public static string NormalizeOption(string code)
        {
            code = code.Trim().ToUpperInvariant();
            if (code.Length == 5 && code[0] == 'S' && code[4] == 'A')
            {
                code = code.Substring(1, 3);
            }
            else if (code.Length == 4 && code[0] == 'S')
            {
                code = code.Substring(1);
            }
            return code;
        }

        static string NormalizeSeries(string series)
        {
            // "G020" and "G20" are the same car
            series = series.Trim().ToUpperInvariant();
            if (series.Length == 4 && series[1] == '0')
            {
                series = series[0] + series.Substring(2);
            }
            return series;
        }

        public static string OptionName(string code) => OptionNames.TryGetValue(NormalizeOption(code), out var name) ? name : null;

        static readonly Dictionary<string, string> SeriesNames = new Dictionary<string, string>
        {
            ["G01"] = "X3", ["G02"] = "X4", ["G05"] = "X5", ["G06"] = "X6", ["G07"] = "X7", ["G08"] = "iX3",
            ["G09"] = "XM", ["G11"] = "7 Series", ["G12"] = "7 Series (long wheelbase)",
            ["G14"] = "8 Series Convertible", ["G15"] = "8 Series Coupé", ["G16"] = "8 Series Gran Coupé",
            ["G20"] = "3 Series Sedan", ["G21"] = "3 Series Touring", ["G22"] = "4 Series Coupé",
            ["G23"] = "4 Series Convertible", ["G26"] = "4 Series Gran Coupé", ["G29"] = "Z4",
            ["G30"] = "5 Series Sedan", ["G31"] = "5 Series Touring", ["G32"] = "6 Series Gran Turismo",
            ["G42"] = "2 Series Coupé", ["G45"] = "X3", ["G60"] = "5 Series Sedan", ["G61"] = "5 Series Touring",
            ["G70"] = "7 Series", ["G80"] = "M3 Sedan", ["G81"] = "M3 Touring", ["G82"] = "M4 Coupé",
            ["G83"] = "M4 Convertible", ["G87"] = "M2", ["G90"] = "M5 Sedan", ["G99"] = "M5 Touring",
            ["F95"] = "X5 M", ["F96"] = "X6 M", ["F97"] = "X3 M", ["F98"] = "X4 M",
            ["F40"] = "1 Series", ["F44"] = "2 Series Gran Coupé", ["U06"] = "2 Series Active Tourer", ["U11"] = "X1",
        };

        // common options only; the full list is in BMW's parts catalogue (ETK)
        static readonly Dictionary<string, string> OptionNames = new Dictionary<string, string>
        {
            ["205"] = "Automatic transmission",
            ["2TB"] = "Sport automatic transmission",
            ["2VB"] = "Tyre pressure monitoring",
            ["248"] = "Steering wheel heating",
            ["255"] = "Sport leather steering wheel",
            ["302"] = "Alarm system",
            ["316"] = "Automatic tailgate operation",
            ["319"] = "Integrated universal remote control (garage door)",
            ["322"] = "Comfort Access",
            ["337"] = "M Sport package",
            ["3AG"] = "Reversing camera",
            ["402"] = "Panorama glass roof",
            ["403"] = "Electric glass sunroof",
            ["420"] = "Sun protection glazing",
            ["430"] = "Auto-dimming interior and exterior mirrors",
            ["431"] = "Auto-dimming interior mirror",
            ["459"] = "Electric seats with memory",
            ["494"] = "Front seat heating",
            ["4UR"] = "Ambient lighting",
            ["502"] = "Headlight washers",
            ["508"] = "Park Distance Control (PDC)",
            ["534"] = "Automatic air conditioning",
            ["544"] = "Cruise control with braking function",
            ["552"] = "Adaptive LED headlights",
            ["5AC"] = "High-beam assistant",
            ["5DM"] = "Parking Assistant",
            ["609"] = "Navigation system Professional",
            ["610"] = "Head-Up Display",
            ["654"] = "DAB digital radio",
            ["676"] = "HiFi loudspeaker system",
            ["688"] = "Harman Kardon surround sound",
            ["6AC"] = "Intelligent emergency call",
            ["6AK"] = "ConnectedDrive services",
            ["6U3"] = "BMW Live Cockpit Professional",
            ["704"] = "M Sport suspension",
            ["710"] = "M leather steering wheel",
            ["715"] = "M Aerodynamics package",
            ["760"] = "High-gloss Shadow Line",
            ["775"] = "Anthracite headliner",
        };
    }
}
