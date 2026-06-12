using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace LocKit.App.Core
{
    public static partial class NativeParser
    {
        private const string DllName = "lockit_parser";

        [LibraryImport(DllName)]
        private static partial IntPtr get_parser_version();

        [LibraryImport(DllName)]
        private static partial void free_string(IntPtr ptr);

        public static string GetVersion()
        {
            try
            {
                IntPtr ptr = get_parser_version();
                if (ptr == IntPtr.Zero)
                    return "Unknown Version";

                string version = Marshal.PtrToStringAnsi(ptr) ?? "Unknown Version";
                free_string(ptr);
                return version;
            }
            catch (DllNotFoundException)
            {
                return "Parser DLL not found";
            }
            catch (Exception ex)
            {
                return $"FFI Error: {ex.Message}";
            }
        }

        public static List<RpyDialogueLine> ParseRpyFile(string filePath)
        {
            return RenpyParser.Parse(filePath);
        }

        public static string? ExportTlFile(string outputPath, IEnumerable<ExportUnit> units, string language = "russian")
        {
            return RenpyParser.Export(outputPath, units, language);
        }
    }

    public class RpyDialogueLine
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("character")]
        public string Character { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("translation")]
        public string Translation { get; set; } = string.Empty;
    }

    public class ExportUnit
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;

        [JsonPropertyName("character")]
        public string Character { get; set; } = string.Empty;
    }
}
