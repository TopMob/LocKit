using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LocKit.App.Core
{
    public static class RenpyParser
    {
        public static List<RpyDialogueLine> Parse(string filePath)
        {
            var units = new List<RpyDialogueLine>();
            if (!File.Exists(filePath)) return units;

            var lines = File.ReadAllLines(filePath);
            int i = 0;
            while (i < lines.Length)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("translate ") && line.EndsWith(":"))
                {
                    string label = ExtractTranslateLabel(line);
                    i++;

                    if (label == "strings")
                    {
                        while (i < lines.Length)
                        {
                            string inner = lines[i].Trim();
                            if (inner.StartsWith("translate ") || (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && !inner.StartsWith("#")))
                            {
                                i--;
                                break;
                            }

                            if (inner.StartsWith("old "))
                            {
                                string oldText = ExtractQuoted(inner.Substring(4));
                                i++;
                                while (i < lines.Length)
                                {
                                    string nextInner = lines[i].Trim();
                                    if (nextInner.StartsWith("translate ") || (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && !nextInner.StartsWith("#")))
                                    {
                                        i--;
                                        break;
                                    }
                                    if (nextInner.StartsWith("new "))
                                    {
                                        string newText = ExtractQuoted(nextInner.Substring(4));
                                        units.Add(new RpyDialogueLine
                                        {
                                            Key = "strings",
                                            Character = "strings",
                                            Source = oldText,
                                            Translation = newText
                                        });
                                        break;
                                    }
                                    i++;
                                }
                            }
                            i++;
                        }
                    }
                    else
                    {
                        string sourceText = "";
                        string charName = "";

                        while (i < lines.Length)
                        {
                            string inner = lines[i].Trim();
                            if (inner.StartsWith("translate ") || (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && !inner.StartsWith("#")))
                            {
                                i--;
                                break;
                            }

                            if (inner.StartsWith("#") && !Regex.IsMatch(inner, @"^#\s*.*\.rpy:\d+"))
                            {
                                string content = inner.Substring(1).Trim();
                                ParseDialogue(content, out string c, out string s);
                                if (!string.IsNullOrEmpty(s))
                                {
                                    charName = c;
                                    sourceText = s;
                                }
                            }
                            else if (!inner.StartsWith("#") && !string.IsNullOrEmpty(inner))
                            {
                                ParseDialogue(inner, out string c, out string t);
                                if (!string.IsNullOrEmpty(sourceText))
                                {
                                    units.Add(new RpyDialogueLine
                                    {
                                        Key = label,
                                        Character = charName,
                                        Source = sourceText,
                                        Translation = t
                                    });
                                    sourceText = "";
                                    charName = "";
                                }
                            }
                            i++;
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(line) && !line.StartsWith("#") && !line.StartsWith("translate ") && !line.EndsWith(":"))
                {
                    ParseDialogue(line, out string c, out string s);
                    if (!string.IsNullOrEmpty(s))
                    {
                        units.Add(new RpyDialogueLine
                        {
                            Key = $"line_{units.Count + 1:04}",
                            Character = c,
                            Source = s,
                            Translation = ""
                        });
                    }
                }
                i++;
            }
            return units;
        }

        public static string? Export(string outputPath, IEnumerable<ExportUnit> units, string language)
        {
            try
            {
                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);
                writer.WriteLine($"# {Path.GetFileName(outputPath)}");
                writer.WriteLine();

                var unitList = new List<ExportUnit>(units);
                int idx = 0;
                while (idx < unitList.Count)
                {
                    var unit = unitList[idx];
                    if (unit.Key == "strings")
                    {
                        writer.WriteLine($"translate {language} strings:");
                        while (idx < unitList.Count && unitList[idx].Key == "strings")
                        {
                            var stringUnit = unitList[idx];
                            writer.WriteLine($"    old \"{EscapeQuotes(stringUnit.Source)}\"");
                            writer.WriteLine($"    new \"{EscapeQuotes(stringUnit.Target)}\"");
                            writer.WriteLine();
                            idx++;
                        }
                        continue;
                    }
                    else
                    {
                        writer.WriteLine($"translate {language} {unit.Key}:");
                        writer.WriteLine();
                        if (!string.IsNullOrEmpty(unit.Character))
                        {
                            writer.WriteLine($"    # {unit.Character} \"{EscapeQuotes(unit.Source)}\"");
                            writer.WriteLine($"    {unit.Character} \"{EscapeQuotes(unit.Target)}\"");
                        }
                        else
                        {
                            writer.WriteLine($"    # \"{EscapeQuotes(unit.Source)}\"");
                            writer.WriteLine($"    \"{EscapeQuotes(unit.Target)}\"");
                        }
                        writer.WriteLine();
                        idx++;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        private static string ExtractTranslateLabel(string line)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                return parts[2].TrimEnd(':');
            }
            return "unknown";
        }

        private static void ParseDialogue(string line, out string character, out string text)
        {
            character = "";
            text = "";

            if (line.StartsWith("\"") && line.EndsWith("\""))
            {
                text = ExtractQuoted(line);
                return;
            }

            int firstQuote = line.IndexOf('"');
            if (firstQuote > 0)
            {
                string prefix = line.Substring(0, firstQuote).Trim();
                string rest = line.Substring(firstQuote);

                if (rest.StartsWith("\""))
                {
                    var keywords = new HashSet<string> { "if", "elif", "else", "while", "for", "with", "show", "hide",
                                    "play", "stop", "pause", "scene", "label", "menu", "call",
                                    "return", "jump", "define", "default", "init", "python",
                                    "image", "transform", "style", "nvl", "$" };

                    string[] prefixParts = prefix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (prefixParts.Length > 0)
                    {
                        string charCandidate = prefixParts[0];
                        if (!keywords.Contains(charCandidate))
                        {
                            character = prefix;
                            text = ExtractQuoted(rest);
                        }
                    }
                }
            }
        }

        private static string ExtractQuoted(string s)
        {
            s = s.Trim();
            if (s.StartsWith("\""))
            {
                int end = -1;
                bool escaped = false;
                for (int i = 1; i < s.Length; i++)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (s[i] == '\\')
                    {
                        escaped = true;
                    }
                    else if (s[i] == '"')
                    {
                        end = i;
                        break;
                    }
                }
                if (end != -1)
                {
                    return s.Substring(1, end - 1);
                }
            }
            return s;
        }

        private static string EscapeQuotes(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"')
                {
                    if (i > 0 && s[i - 1] == '\\')
                    {
                        sb.Append(c);
                    }
                    else
                    {
                        sb.Append('\\').Append(c);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }
    }
}
