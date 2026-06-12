using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace LocKit.App.Core
{
    public static class TextProcessor
    {
        private static readonly Regex TagRegex = new Regex(
            @"(\[[a-zA-Z0-9_]+\]|\{[a-zA-Z0-9_=#/-]+\}|\\[a-zA-Z]+\[[0-9]+\])",
            RegexOptions.Compiled
        );

        private static readonly Regex PlaceholderRegex = new Regex(
            @"___(?:TAG|tag|Tag)\s*(\d+)\s*___",
            RegexOptions.Compiled
        );

        public static string EscapeTags(string text, out List<string> tags)
        {
            tags = new List<string>();
            if (string.IsNullOrEmpty(text)) return text;

            var localTags = tags;
            return TagRegex.Replace(text, match =>
            {
                string tag = match.Value;
                int index = localTags.Count;
                localTags.Add(tag);
                return $"___TAG{index}___";
            });
        }

        public static string UnescapeTags(string text, List<string> tags)
        {
            if (string.IsNullOrEmpty(text) || tags == null || tags.Count == 0) return text;

            return PlaceholderRegex.Replace(text, match =>
            {
                if (match.Groups.Count > 1 && int.TryParse(match.Groups[1].Value, out int index))
                {
                    if (index >= 0 && index < tags.Count)
                    {
                        return tags[index];
                    }
                }
                return match.Value;
            });
        }

        public static string WordWrap(string text, int maxChars)
        {
            if (string.IsNullOrEmpty(text) || maxChars <= 0) return text;

            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            var resultLines = new List<string>();

            foreach (var line in lines)
            {
                if (line.Length <= maxChars)
                {
                    resultLines.Add(line);
                    continue;
                }

                var words = line.Split(' ');
                var currentLine = new StringBuilder();

                foreach (var word in words)
                {
                    int wordLength = word.Length;
                    int lineLength = currentLine.Length;
                    int addedLength = wordLength + (lineLength > 0 ? 1 : 0);

                    if (lineLength + addedLength > maxChars)
                    {
                        if (lineLength > 0)
                        {
                            resultLines.Add(currentLine.ToString());
                            currentLine.Clear();
                        }
                        currentLine.Append(word);
                    }
                    else
                    {
                        if (lineLength > 0)
                        {
                            currentLine.Append(' ');
                        }
                        currentLine.Append(word);
                    }
                }

                if (currentLine.Length > 0)
                {
                    resultLines.Add(currentLine.ToString());
                }
            }

            return string.Join("\n", resultLines);
        }
    }
}
