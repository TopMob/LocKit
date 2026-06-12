using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
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

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr parse_rpy_file(string path);

        [LibraryImport(DllName, StringMarshalling = StringMarshalling.Utf8)]
        private static partial IntPtr export_tl_file(string outputPath, string unitsJson, string language);

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
            try
            {
                IntPtr ptr = parse_rpy_file(filePath);
                if (ptr == IntPtr.Zero)
                    return new List<RpyDialogueLine>();

                string json = Marshal.PtrToStringAnsi(ptr) ?? "[]";
                free_string(ptr);

                if (json.StartsWith("{\"error\""))
                    return new List<RpyDialogueLine>();

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<List<RpyDialogueLine>>(json, options)
                    ?? new List<RpyDialogueLine>();
            }
            catch (Exception)
            {
                return new List<RpyDialogueLine>();
            }
        }

        public static string? ExportTlFile(string outputPath, IEnumerable<ExportUnit> units, string language = "russian")
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                string unitsJson = JsonSerializer.Serialize(units.ToList(), options);

                IntPtr result = export_tl_file(outputPath, unitsJson, language);
                if (result == IntPtr.Zero)
                    return null;

                string error = Marshal.PtrToStringAnsi(result) ?? "Unknown error";
                free_string(result);
                return error;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
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
    }

    public class ExportUnit
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;

        [JsonPropertyName("target")]
        public string Target { get; set; } = string.Empty;
    }
}
