using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LocKit.App.Core
{
    public class DecompilerService
    {
        public async Task<bool> DecompileAndGenerateAsync(string gameExe, string gameRoot, string language, Action<string> logCallback)
        {
            string gameDir = Path.Combine(gameRoot, "game");
            if (!Directory.Exists(gameDir))
            {
                gameDir = gameRoot;
            }

            bool hasPython = await CheckPythonAvailableAsync();
            if (hasPython)
            {
                bool rpycdecInstalled = await EnsureRpycDecInstalledAsync(logCallback);
                if (rpycdecInstalled)
                {
                    await UnpackRpaArchivesAsync(gameDir, logCallback);
                    await DecompileRpycFilesAsync(gameDir, logCallback);
                }
            }
            else
            {
                logCallback("Python is not installed or not in PATH. Skipping RPA/RPYC decompilation step.");
            }

            return await GenerateTranslationsAsync(gameExe, gameRoot, language, logCallback);
        }

        private async Task<bool> CheckPythonAvailableAsync()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "--version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                    return process.ExitCode == 0;
                }
            }
            catch
            {
            }
            return false;
        }

        private async Task<bool> EnsureRpycDecInstalledAsync(Action<string> logCallback)
        {
            try
            {
                var checkInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-c \"import rpycdec\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(checkInfo))
                {
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        if (process.ExitCode == 0) return true;
                    }
                }

                logCallback("Installing rpycdec library via pip...");
                var installInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = "-m pip install rpycdec",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using (var process = Process.Start(installInfo))
                {
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                        return process.ExitCode == 0;
                    }
                }
            }
            catch (Exception ex)
            {
                logCallback($"Error verifying/installing rpycdec: {ex.Message}");
            }
            return false;
        }

        private async Task UnpackRpaArchivesAsync(string gameDir, Action<string> logCallback)
        {
            try
            {
                var rpaFiles = Directory.GetFiles(gameDir, "*.rpa", SearchOption.AllDirectories);
                if (rpaFiles.Length == 0) return;

                logCallback($"Found {rpaFiles.Length} RPA archive(s). Extracting...");
                foreach (string rpa in rpaFiles)
                {
                    logCallback($"Extracting {Path.GetFileName(rpa)}...");
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = "python",
                        Arguments = $"-m rpycdec unrpa \"{rpa}\" -o \"{gameDir}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using var process = Process.Start(startInfo);
                    if (process != null)
                    {
                        await process.WaitForExitAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                logCallback($"RPA extraction error: {ex.Message}");
            }
        }

        private async Task DecompileRpycFilesAsync(string gameDir, Action<string> logCallback)
        {
            try
            {
                var rpycFiles = Directory.GetFiles(gameDir, "*.rpyc", SearchOption.AllDirectories);
                if (rpycFiles.Length == 0) return;

                logCallback($"Found {rpycFiles.Length} RPYC file(s). Decompiling...");
                var startInfo = new ProcessStartInfo
                {
                    FileName = "python",
                    Arguments = $"-m rpycdec decompile \"{gameDir}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    await process.WaitForExitAsync();
                }
            }
            catch (Exception ex)
            {
                logCallback($"RPYC decompilation error: {ex.Message}");
            }
        }

        public async Task<bool> GenerateTranslationsAsync(string gameExe, string gameRoot, string language, Action<string> logCallback)
        {
            logCallback($"Starting Ren'Py native translation generator for {language}...");

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = gameExe,
                    Arguments = $"\"{gameRoot}\" translate {language}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = gameRoot
                };

                using var process = new Process { StartInfo = startInfo };
                process.Start();

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await Task.WhenAll(outputTask, errorTask);

                string output = outputTask.Result;
                string error = errorTask.Result;

                await process.WaitForExitAsync();

                bool success = process.ExitCode == 0;
                
                if (success)
                {
                    logCallback("Translation generation successful.");
                    return true;
                }
                else
                {
                    logCallback($"Translation generation failed (Exit code {process.ExitCode}).\nError: {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                logCallback($"Exception while running translation generator: {ex.Message}");
                return false;
            }
        }
    }
}
