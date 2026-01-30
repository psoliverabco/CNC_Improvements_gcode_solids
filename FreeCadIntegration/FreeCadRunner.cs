using CNC_Improvements_gcode_solids.Properties;
using System;
using System.Diagnostics;
using System.IO;

namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    internal static class FreeCadRunner
    {
        /// <summary>
        /// Uses FreeCadScript.HeadPY / ProfilePth / StepPth / BodyPY to write
        /// a UNIQUE npc_profile_revolve_###.py into %LOCALAPPDATA%\NPC_Gcode_Solids,
        /// then runs FreeCADCmd.exe with that script.
        ///
        /// Returns combined stdout/stderr log from FreeCADCmd.
        /// </summary>
        public static string RunFreeCad()
        {
            // -------------------------------
            // 1) Validate paths in script
            // -------------------------------
            string profilePath = FreeCadScript.ProfilePth;
            string stepPath = FreeCadScript.StepPth;


            if (string.IsNullOrWhiteSpace(stepPath) ||
                stepPath == "Not yet set step")
            {
                throw new InvalidOperationException(
                    "FreeCadScript.StepPth is not set.\n" +
                    "Set it before calling FreeCadRunner.RunFreeCad().");
            }




            // -------------------------------
            // 2) Build python script text
            // -------------------------------
            string Profile = @"latheProfile = " + "\"\"\"" + FreeCadScript.Profile + "\"\"\"";


            string scriptText =
                FreeCadScript.HeadPY + Environment.NewLine +
                // $"input_txt   = r\"{profilePath}\"" + Environment.NewLine +
                $"output_step = r\"{stepPath}\"" + Environment.NewLine +

                Profile + Environment.NewLine +
                FreeCadScript.BodyPY + Environment.NewLine +
                FreeCadScript.TransPY + Environment.NewLine +
                FreeCadScript.TailPy

                ;






            // -------------------------------
            // 3) Unique script path (NO overwrite races)
            // -------------------------------
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string tempDir = Path.Combine(localAppData, "NPC_Gcode_Solids");
            Directory.CreateDirectory(tempDir);

            int n = FreeCadRunSuffix.NextTurn();
            string scriptPath = Path.Combine(tempDir, $"npc_profile_revolve_{n:000}.py");

            File.WriteAllText(scriptPath, scriptText);

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


            // -------------------------------
            // 5) Run FreeCADCmd headless:
            // -------------------------------
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"\"{scriptPath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(profilePath) ?? Environment.CurrentDirectory
            };

            using (var proc = Process.Start(psi))
            {
                if (proc == null)
                    throw new InvalidOperationException("Failed to start FreeCADCmd process.");

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
                        "FreeCADCmd exited with code " + proc.ExitCode + Environment.NewLine +
                        combinedLog);
                }

                return combinedLog;
            }
        }
    }
}
