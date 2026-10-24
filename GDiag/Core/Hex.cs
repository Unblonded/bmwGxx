using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GDiag
{
    public static class Hex
    {
        public static string Format(byte[] data, int offset = 0, int count = -1)
        {
            if (data is null)
            {
                return "";
            }
            if (count < 0)
            {
                count = data.Length - offset;
            }
            return string.Join(" ", data.Skip(offset).Take(count).Select(b => b.ToString("X2")));
        }

        // accepts "22 F1 90", "22F190", "0x22,0xF1,0x90"
        public static bool TryParse(string text, out byte[] bytes)
        {
            bytes = null;
            string clean = (text ?? "").Replace("0x", "").Replace("0X", "");
            clean = new string(clean.Where(Uri.IsHexDigit).ToArray());
            if (clean.Length == 0 || clean.Length % 2 != 0)
            {
                return false;
            }
            var result = new List<byte>();
            for (int i = 0; i < clean.Length; i += 2)
            {
                result.Add(byte.Parse(clean.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            bytes = result.ToArray();
            return true;
        }

        public static bool TryParseNumber(string text, out uint value)
        {
            text = (text ?? "").Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text.Substring(2);
            }
            return uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        public static uint BigEndian(byte[] data, int offset, int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                value = (value << 8) | data[offset + i];
            }
            return value;
        }

        // printable ASCII, used for VINs and part numbers
        public static string Ascii(byte[] data, int offset, int count = -1)
        {
            if (count < 0)
            {
                count = data.Length - offset;
            }
            var chars = data.Skip(offset).Take(count).Where(b => b >= 0x20 && b < 0x7F).Select(b => (char)b).ToArray();
            return new string(chars).Trim();
        }
    }
}
