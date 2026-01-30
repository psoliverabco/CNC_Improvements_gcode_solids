using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Diagnostics;
using System.IO;

namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    internal static class FreeCadRunnerDrill
    {
        public static string SaveScript(string stepPath)
        {
            if (string.IsNullOrWhiteSpace(stepPath))
                throw new InvalidOperationException("STEP path is not specified.");

            if (string.IsNullOrWhiteSpace(FreeCadScriptDrill.HoleShape) ||
                FreeCadScriptDrill.HoleShape.Contains("NotYetSet_HoleShape"))
            {
                throw new InvalidOperationException("FreeCadScriptDrill.HoleShape has not been initialized.");
            }

            if (string.IsNullOrWhiteSpace(FreeCadScriptDrill.Positions) ||
                FreeCadScriptDrill.Positions.Contains("NotYetSet_Positions"))
            {
                throw new InvalidOperationException("FreeCadScriptDrill.Positions has not been initialized.");
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Unify ALL FreeCAD scripts to the SAME folder as Turning:
            string scriptDir = Path.Combine(localAppData, "NPC_Gcode_Solids");
            Directory.CreateDirectory(scriptDir);





            int n = FreeCadRunSuffix.NextDrill();
            string scriptPath = Path.Combine(scriptDir, $"npc_holes_{n:000}.py");

            string scriptText =
                $"output_step = r\"{stepPath}\"\n" +
                FreeCadScriptDrill.HeadPY + Environment.NewLine +
                FreeCadScriptDrill.HoleShape + Environment.NewLine +
                FreeCadScriptDrill.TransPY + Environment.NewLine +
                FreeCadScriptDrill.MidPY + Environment.NewLine +
                FreeCadScriptDrill.Positions + Environment.NewLine +
                FreeCadScriptDrill.TailPY;

            File.WriteAllText(scriptPath, scriptText);
            return scriptPath;
        }

        public static string RunFreeCad(string scriptPath, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                throw new FileNotFoundException("Drill Python script not found.", scriptPath);

            // -------------------------------
            // 4) Locate FreeCADCmd.exe path
            // -------------------------------
            string exePath = string.Empty;

            // First check local copy: <AppDir>\FreeCAD\bin\FreeCADCmd.exe
            try
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string localPath = System.IO.Path.Combine(appDir, "FreeCAD", "bin", "FreeCADCmd.exe");

                if (File.Exists(localPath))
                {
                    exePath = localPath;
                }
                else
                {
                    // fallback to configured path
                    string cfgPath = Settings.Default.FreeCadPath;
                    if (!string.IsNullOrWhiteSpace(cfgPath) && File.Exists(cfgPath))
                    {
                        exePath = cfgPath;
                    }
                }
            }
            catch
            {
                // fallback only to Settings
                string cfgPath = Settings.Default.FreeCadPath;
                if (!string.IsNullOrWhiteSpace(cfgPath) && File.Exists(cfgPath))
                    exePath = cfgPath;
            }

            // final validation
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                throw new InvalidOperationException(
                    "FreeCADCmd.exe could not be located.\n\n" +
                    "Tried:\n" +
                    "  • AppDir\\FreeCAD\\bin\\FreeCADCmd.exe\n" +
                    "  • Settings.Default.FreeCadPath\n\n" +
                    "Current setting:\n" + (Settings.Default.FreeCadPath ?? "<null>"));
            }


            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                workingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                    throw new InvalidOperationException("Failed to start FreeCADCmd process for drill.");

                string stdOut = proc.StandardOutput.ReadToEnd();
                string stdErr = proc.StandardError.ReadToEnd();

                proc.WaitForExit();

                string combinedLog = stdOut ?? "";
                if (!string.IsNullOrWhiteSpace(stdErr))
                {
                    combinedLog += Environment.NewLine +
                                   "---- STDERR ----" + Environment.NewLine +
                                   stdErr;
                }

                if (proc.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        "FreeCADCmd (drill) exited with code " + proc.ExitCode + Environment.NewLine +
                        combinedLog);
                }

                return combinedLog;
            }
        }
    }
}
