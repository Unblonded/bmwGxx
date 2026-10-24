using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GDiag.Translation
{
    public static class GermanText
    {
        // lowercase + transliterate umlauts so "Tür", "Tuer" and "TUER" all hit the same glossary key
        public static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length + 4);
            foreach (char c in s.ToLowerInvariant())
            {
                switch (c)
                {
                    case 'ä': sb.Append("ae"); break;
                    case 'ö': sb.Append("oe"); break;
                    case 'ü': sb.Append("ue"); break;
                    case 'ß': sb.Append("ss"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        static readonly ConcurrentDictionary<string, bool> LooksGermanMemo = new ConcurrentDictionary<string, bool>();

        // true if the text probably still contains German that is worth sending to the online translator
        public static bool LooksGerman(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }
            return LooksGermanMemo.GetOrAdd(s, LooksGermanUncached);
        }

        static bool LooksGermanUncached(string s)
        {
            if (s.IndexOfAny(new[] { 'ä', 'ö', 'ü', 'Ä', 'Ö', 'Ü', 'ß' }) >= 0)
            {
                return true;
            }
            foreach (string word in OfflineTranslator.SplitWords(s))
            {
                string n = Normalize(word);
                if (n.Length >= 3 && GermanGlossary.Words.TryGetValue(n, out var entry) && !string.Equals(entry.English, n, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
    }

    // Dictionary based German -> English translation. Instant and works offline, but the grammar is rough;
    // the online translator (if enabled) replaces these results as they arrive.
    public static class OfflineTranslator
    {
        static readonly string[] InflectionEndings = { "en", "er", "es", "em", "e", "n", "s" };
        static readonly string[] Linkers = { "s", "es", "n", "en", "e", "er" };

        static readonly ConcurrentDictionary<string, string> Memo = new ConcurrentDictionary<string, string>();

        class Unit
        {
            public string Text;
            public bool IsVerb;
            public bool IsPunctuation;
            public bool Translated;
        }

        public static string Translate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return text ?? "";
            }
            return Memo.GetOrAdd(text, TranslateUncached);
        }

        static string TranslateUncached(string text)
        {
            List<string> tokens = Tokenize(text);
            var units = new List<Unit>();

            int i = 0;
            while (i < tokens.Count)
            {
                string token = tokens[i];
                if (!IsWord(token))
                {
                    units.Add(new Unit { Text = token, IsPunctuation = true });
                    i++;
                    continue;
                }

                // phrases first
                if (TryMatchPhrase(tokens, i, out string phraseEnglish, out int consumed))
                {
                    units.Add(new Unit { Text = phraseEnglish, Translated = true });
                    i += consumed;
                    continue;
                }

                units.Add(TranslateWord(token));
                i++;
            }

            // German puts the infinitive last ("Fehlerspeicher lesen"), BMW coding names put it after the object
            // ("SPIEGEL_ANKLAPPEN_FB"); English wants it first ("Read fault memory", "Fold mirror remote key")
            var wordUnits = units.Where(u => !u.IsPunctuation).ToList();
            Unit firstVerb = wordUnits.FirstOrDefault(u => u.IsVerb);
            if (wordUnits.Count > 1 && firstVerb != null && !wordUnits[0].IsVerb)
            {
                Unit verb = firstVerb;
                int verbIndex = units.LastIndexOf(verb);
                units.RemoveAt(verbIndex);
                // drop a dangling separator that was in front of the verb
                while (units.Count > 0 && units[units.Count - 1].IsPunctuation && units[units.Count - 1].Text.Trim().Length == 0)
                {
                    units.RemoveAt(units.Count - 1);
                }
                units.Insert(0, new Unit { Text = " ", IsPunctuation = true });
                units.Insert(0, verb);
            }

            var sb = new StringBuilder();
            bool previousWasWord = false;
            foreach (var unit in units)
            {
                if (unit.IsPunctuation)
                {
                    sb.Append(unit.Text);
                    previousWasWord = false;
                }
                else if (unit.Text.Length > 0)
                {
                    // words split from camelCase ("SecurityAccess") have no separator between them
                    if (previousWasWord)
                    {
                        sb.Append(' ');
                    }
                    sb.Append(unit.Text);
                    previousWasWord = true;
                }
            }
            return CollapseSpaces(sb.ToString());
        }

        static bool TryMatchPhrase(List<string> tokens, int start, out string english, out int consumedTokens)
        {
            english = null;
            consumedTokens = 0;

            foreach (var phrase in GermanGlossary.Phrases)
            {
                string[] phraseWords = phrase.Key.Split(' ');
                int t = start;
                int matched = 0;
                while (matched < phraseWords.Length && t < tokens.Count)
                {
                    if (!IsWord(tokens[t]))
                    {
                        // phrases may span whitespace, underscores and dots, nothing else
                        if (tokens[t].Trim().Length == 0 || tokens[t] == "." || tokens[t] == "_")
                        {
                            t++;
                            continue;
                        }
                        break;
                    }
                    if (GermanText.Normalize(tokens[t]) != phraseWords[matched])
                    {
                        break;
                    }
                    matched++;
                    t++;
                }
                if (matched == phraseWords.Length)
                {
                    english = phrase.Value;
                    consumedTokens = t - start;
                    return true;
                }
            }
            return false;
        }

        static Unit TranslateWord(string word)
        {
            // keep codes and acronyms (KI211, CAN, 0x7F, UDS) untouched
            bool hasDigit = word.Any(char.IsDigit);
            bool isAcronym = word.Length <= 5 && word.All(c => char.IsUpper(c) || char.IsDigit(c));
            string n = GermanText.Normalize(word);

            if (GermanGlossary.Words.TryGetValue(n, out var direct))
            {
                return new Unit { Text = direct.English, IsVerb = direct.IsVerb, Translated = true };
            }
            if (hasDigit || isAcronym)
            {
                return new Unit { Text = word };
            }

            // inflected form of a known word ("aktuellen" -> "aktuell")
            foreach (string ending in InflectionEndings)
            {
                if (n.Length > ending.Length + 3 && n.EndsWith(ending))
                {
                    string stem = n.Substring(0, n.Length - ending.Length);
                    if (GermanGlossary.Words.TryGetValue(stem, out var inflected) && !inflected.StandaloneOnly)
                    {
                        return new Unit { Text = inflected.English, IsVerb = inflected.IsVerb, Translated = true };
                    }
                }
            }

            // compound noun ("Fahrertuerfensterheber" -> driver door power window)
            List<GermanGlossary.Entry> parts = SplitCompound(n);
            if (parts != null)
            {
                string joined = string.Join(" ", parts.Select(p => p.English).Where(p => p.Length > 0));
                return new Unit { Text = joined, IsVerb = parts[parts.Count - 1].IsVerb && parts.Count == 1, Translated = true };
            }

            // unknown: probably already English, or a proper name
            return new Unit { Text = word };
        }

        // dynamic programming split into the fewest glossary words, allowing German linking letters (Fugen-s etc)
        static List<GermanGlossary.Entry> SplitCompound(string n)
        {
            const int MaxParts = 5;
            int len = n.Length;
            var bestCost = new double[len + 1];
            var back = new int[len + 1];
            var backEntry = new GermanGlossary.Entry[len + 1];
            for (int i = 1; i <= len; i++)
            {
                bestCost[i] = double.MaxValue;
            }
            bestCost[0] = 0;

            for (int end = 1; end <= len; end++)
            {
                for (int start = Math.Max(0, end - 32); start < end; start++)
                {
                    if (bestCost[start] == double.MaxValue)
                    {
                        continue;
                    }
                    string piece = n.Substring(start, end - start);

                    // a word from the glossary
                    if (piece.Length >= 3 && GermanGlossary.Words.TryGetValue(piece, out var entry) && !entry.StandaloneOnly)
                    {
                        Relax(start, end, 1.0, entry);
                    }
                    // a linking letter between two words, only valid after a real word
                    else if (start > 0 && backEntry[start] != null && Linkers.Contains(piece))
                    {
                        Relax(start, end, 0.4, null);
                    }
                    // an inflected final word
                    else if (end == len)
                    {
                        foreach (string ending in InflectionEndings)
                        {
                            if (piece.Length > ending.Length + 2 && piece.EndsWith(ending))
                            {
                                string stem = piece.Substring(0, piece.Length - ending.Length);
                                if (GermanGlossary.Words.TryGetValue(stem, out var stemEntry) && !stemEntry.StandaloneOnly)
                                {
                                    Relax(start, end, 1.2, stemEntry);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            void Relax(int start, int end, double cost, GermanGlossary.Entry entry)
            {
                double candidate = bestCost[start] + cost;
                if (candidate < bestCost[end])
                {
                    bestCost[end] = candidate;
                    back[end] = start;
                    backEntry[end] = entry;
                }
            }

            if (bestCost[len] == double.MaxValue)
            {
                return null;
            }

            var parts = new List<GermanGlossary.Entry>();
            int cursor = len;
            while (cursor > 0)
            {
                if (backEntry[cursor] != null)
                {
                    parts.Insert(0, backEntry[cursor]);
                }
                cursor = back[cursor];
            }
            if (parts.Count == 0 || parts.Count > MaxParts)
            {
                return null;
            }
            return parts;
        }

        // words, plus everything between them as separate tokens. Underscores count as spaces, camelCase is split.
        static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            bool inWord = false;

            void Flush()
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                bool wordChar = char.IsLetterOrDigit(c);
                if (wordChar != inWord)
                {
                    Flush();
                    inWord = wordChar;
                }
                else if (wordChar && i > 0 && char.IsLower(text[i - 1]) && char.IsUpper(c))
                {
                    // camelCase boundary
                    Flush();
                }
                current.Append(c == '_' ? ' ' : c);
            }
            Flush();
            return tokens;
        }

        public static IEnumerable<string> SplitWords(string text)
        {
            return Tokenize(text).Where(IsWord);
        }

        static bool IsWord(string token)
        {
            return token.Length > 0 && char.IsLetterOrDigit(token[0]);
        }

        static string CollapseSpaces(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool lastSpace = false;
            foreach (char c in s)
            {
                bool space = c == ' ';
                if (space && lastSpace)
                {
                    continue;
                }
                sb.Append(c);
                lastSpace = space;
            }
            return sb.ToString().Trim();
        }
    }
}
