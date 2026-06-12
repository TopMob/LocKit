using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace LocKit.App.Core
{
    public class DecompilerService
    {
        public async Task<bool> DecompileFolderIfNeededAsync(string folderPath, Action<string> logCallback)
        {
            logCallback("Starting .rpyc decompilation check...");

            // 1. Check Python
            logCallback("Checking Python installation...");
            var (pythonOk, pythonVersion) = await RunCommandAsync("python", "--version");
            if (!pythonOk)
            {
                logCallback("Error: Python is not installed or not added to system PATH. Please install Python 3.9+ to decompile .rpyc files.");
                return false;
            }
            logCallback($"Python detected: {pythonVersion.Trim()}");

            // 2. Check rpycdec
            logCallback("Checking for rpycdec tool...");
            var (rpycdecOk, _) = await RunCommandAsync("python", "-c \"import rpycdec\"");
            if (!rpycdecOk)
            {
                logCallback("rpycdec tool is not installed. Attempting to install it via pip...");
                var (pipOk, pipOutput) = await RunCommandAsync("python", "-m pip install rpycdec");
                if (!pipOk)
                {
                    logCallback($"Error: Failed to install rpycdec via pip.\nDetails:\n{pipOutput}");
                    logCallback("Please run 'pip install rpycdec' manually in your terminal.");
                    return false;
                }
                logCallback("rpycdec successfully installed!");
            }
            else
            {
                logCallback("rpycdec tool is already installed.");
            }

            // 3. Run Decompiler
            logCallback($"Decompiling .rpyc files in: {folderPath}...");
            var (decompileOk, decompileOutput) = await RunCommandAsync("python", $"-m rpycdec decompile \"{folderPath}\"");
            if (!decompileOk)
            {
                logCallback($"Error during decompilation:\n{decompileOutput}");
                return false;
            }

            logCallback("Decompilation completed successfully! All .rpyc files have been decompiled to .rpy.");
            return true;
        }

        private async Task<(bool Success, string Output)> RunCommandAsync(string fileName, string arguments)
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                bool success = process.ExitCode == 0;
                string result = success ? output : error;
                if (string.IsNullOrEmpty(result))
                {
                    result = output + "\n" + error;
                }

                return (success, result);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }
    }
}
