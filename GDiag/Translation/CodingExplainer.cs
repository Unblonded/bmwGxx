using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using GDiag.Bmw;

namespace GDiag.Translation
{
    public enum CodingLineKind { Blank, Comment, Module, Block, Parameter, Value, Path }

    public class CodingLine
    {
        public int Indent;
        public string Original;
        public string English;
        public CodingLineKind Kind;
        // the module a path starts with, e.g. "Head unit" for HU_MGU
        public string ModuleName;
    }

    // Explains BMW coding notes in English. Accepts what people usually paste:
    //   an E-Sys FDL tree copied line by line ("3000 Exbox", "  VIDEO_FAHRT", "    nicht_aktiv"),
    //   or one-line cheat-sheet entries ("HU_MGU > 3000 Exbox > VIDEO_FAHRT = aktiv").
    // It only reads text; nothing is sent to the car.
    public static class CodingExplainer
    {
        static readonly Regex BlockNumber = new Regex(@"^([0-9A-F]{4})\s+(.*)$", RegexOptions.IgnoreCase);
        static readonly Regex ValueWord = new Regex(@"^(nicht_|kein_)?[a-z][a-z0-9_]*$|^wert_[0-9a-f]+$|^[0-9a-f]{2}( [0-9a-f]{2})*$", RegexOptions.IgnoreCase);
        static readonly string[] PathSeparators = { " > ", " -> ", " → ", " / ", ">" };
        static readonly HashSet<string> ModuleSuffixes = new HashSet<string> { "BODY", "BODY2", "MGU", "MGU2", "NBT", "NBT2", "EVO", "ENTRY", "ID5", "ID6", "ID7", "ID8", "01", "02", "2" };

        public static List<CodingLine> Explain(string text)
        {
            var lines = new List<CodingLine>();
            foreach (string raw in (text ?? "").Replace("\r", "").Split('\n'))
            {
                lines.Add(ExplainLine(raw));
            }
            // drop trailing blank lines
            while (lines.Count > 0 && lines[lines.Count - 1].Kind == CodingLineKind.Blank)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            return lines;
        }

        public static CodingLine ExplainLine(string raw)
        {
            string expanded = raw.Replace("\t", "    ");
            string line = expanded.Trim();
            var result = new CodingLine { Original = raw.TrimEnd(), Indent = expanded.Length - expanded.TrimStart().Length };
            if (line.Length == 0)
            {
                result.Kind = CodingLineKind.Blank;
                result.English = "";
                return result;
            }
            if (line.StartsWith("#") || line.StartsWith("//") || line.StartsWith(";"))
            {
                result.Kind = CodingLineKind.Comment;
                result.English = line;
                return result;
            }

            // "PARAM = value" or "PARAM: value"
            string value = null;
            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                eq = line.IndexOf(':');
            }
            if (eq > 0)
            {
                value = line.Substring(eq + 1).Trim();
                line = line.Substring(0, eq).Trim();
            }

            string[] segments = SplitPath(line);
            var english = new List<string>();
            for (int i = 0; i < segments.Length; i++)
            {
                string segment = segments[i];
                var module = i == 0 ? ModuleFor(segment) : null;
                if (module != null)
                {
                    result.ModuleName = module.Name;
                    english.Add($"{segment} ({module.Name})");
                    continue;
                }
                english.Add(TranslateSegment(segment));
            }
            result.English = string.Join(" > ", english);
            if (value != null)
            {
                result.English += " = " + TranslateValue(value);
            }

            if (segments.Length > 1 || value != null)
            {
                result.Kind = CodingLineKind.Path;
            }
            else if (result.ModuleName != null)
            {
                result.Kind = CodingLineKind.Module;
            }
            else if (BlockNumber.IsMatch(line))
            {
                result.Kind = CodingLineKind.Block;
            }
            else if (ValueWord.IsMatch(line) && line.Any(char.IsLower) || line.StartsWith("wert", StringComparison.OrdinalIgnoreCase))
            {
                result.Kind = CodingLineKind.Value;
                result.English = TranslateValue(line);
            }
            else
            {
                result.Kind = CodingLineKind.Parameter;
            }
            return result;
        }

        static string[] SplitPath(string line)
        {
            foreach (string separator in PathSeparators)
            {
                if (line.Contains(separator))
                {
                    return line.Split(new[] { separator }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
                }
            }
            return new[] { line };
        }

        static string TranslateSegment(string segment)
        {
            // "3000 Exbox": keep the block number, translate the name
            var block = BlockNumber.Match(segment);
            if (block.Success)
            {
                return $"{block.Groups[1].Value} {Words(block.Groups[2].Value)}";
            }
            return Words(segment);
        }

        static string TranslateValue(string value)
        {
            // "wert_01" is a numbered choice, "01 00 FF" are raw bytes: both are left as they are
            var numbered = Regex.Match(value, @"^wert_([0-9a-f]+)$", RegexOptions.IgnoreCase);
            if (numbered.Success)
            {
                return $"option {numbered.Groups[1].Value}";
            }
            if (Regex.IsMatch(value, @"^([0-9a-f]{2}\s*)+$", RegexOptions.IgnoreCase))
            {
                return $"raw value {value}";
            }
            return Words(value);
        }

        static string Words(string text)
        {
            string translated = OfflineTranslator.Translate(text);
            return translated.Length > 0 ? translated : text;
        }

        // "HU_MGU", "BDC_BODY", "KOMBI" -> the module it names
        static EcuInfo ModuleFor(string segment)
        {
            if (segment.Contains(' ') || segment.Length > 16)
            {
                return null;
            }
            // a parameter like "PDC_GONG_LAUTSTAERKE" also starts with a module name, so only a bare
            // name or one of the variant suffixes E-Sys uses counts
            string[] parts = segment.ToUpperInvariant().Split('_');
            if (parts.Length > 2 || parts.Length == 2 && !ModuleSuffixes.Contains(parts[1]))
            {
                return null;
            }
            return EcuCatalog.LookupShort(parts[0]);
        }
    }
}
