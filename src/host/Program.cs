using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

[assembly: AssemblyTitle("FTI PC Inventory Tool")]
[assembly: AssemblyDescription("Function International - Enterprise PC Hardware and Software Inventory Scanner")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Function International (FTI)")]
[assembly: AssemblyProduct("FTI PC Inventory Tool")]
[assembly: AssemblyCopyright("Copyright © Function International 2026")]
[assembly: AssemblyTrademark("FTI")]
[assembly: AssemblyCulture("")]
[assembly: AssemblyVersion("2026.11.19.4")]
[assembly: AssemblyFileVersion("2026.11.19.4")]
[assembly: AssemblyInformationalVersion("v2026.11.19.4 Enterprise")]

namespace FTI.PCInventory
{
    internal static class Program
    {
        private static string _tempScriptToClean = null;
        private static string _localAppDir = null;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                _localAppDir = Path.Combine(localAppData, "FTI_PC_Inventory_Tool");

                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string engineDir = Path.Combine(programData, "FTI_PC_Inventory_Tool");

                EnsureSecureDirectory(engineDir);

                string scriptPath = Path.Combine(engineDir, "app_engine.ps1");

                // Extract embedded PowerShell engine using hash-versioned atomic write (eliminates 0-byte truncation race)
                using (Stream resStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FTI.Engine"))
                {
                    if (resStream != null)
                    {
                        byte[] engineBytes;
                        using (MemoryStream ms = new MemoryStream())
                        {
                            resStream.CopyTo(ms);
                            engineBytes = ms.ToArray();
                        }

                        string shortHash = ComputeShortSha256(engineBytes);
                        scriptPath = Path.Combine(engineDir, string.Format("app_engine_{0}.ps1", shortHash));

                        // Reject reparse points on target script file
                        if (File.Exists(scriptPath) && (File.GetAttributes(scriptPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            try { File.Delete(scriptPath); } catch { }
                        }

                        bool targetReady = false;
                        if (File.Exists(scriptPath))
                        {
                            try
                            {
                                FileInfo fi = new FileInfo(scriptPath);
                                if (fi.Length == engineBytes.Length)
                                {
                                    targetReady = true;
                                }
                            }
                            catch { }
                        }

                        if (!targetReady)
                        {
                            string pidTmp = Path.Combine(engineDir, string.Format("app_engine_{0}_{1}.tmp.ps1", shortHash, Process.GetCurrentProcess().Id));
                            try
                            {
                                using (FileStream fs = new FileStream(pidTmp, FileMode.Create, FileAccess.Write, FileShare.None))
                                {
                                    fs.Write(engineBytes, 0, engineBytes.Length);
                                    fs.Flush(true);
                                }
                                ApplySecureFileAcl(pidTmp);

                                try
                                {
                                    if (!File.Exists(scriptPath))
                                    {
                                        File.Move(pidTmp, scriptPath);
                                        targetReady = true;
                                    }
                                    else
                                    {
                                        // Another concurrent instance just finalized the same content-addressed file
                                        try { File.Delete(pidTmp); } catch { }
                                        targetReady = true;
                                    }
                                }
                                catch (IOException)
                                {
                                    if (File.Exists(scriptPath) && new FileInfo(scriptPath).Length == engineBytes.Length)
                                    {
                                        try { File.Delete(pidTmp); } catch { }
                                        targetReady = true;
                                    }
                                    else
                                    {
                                        // Fall back to executing our process-isolated copy and clean it up on exit
                                        scriptPath = pidTmp;
                                        _tempScriptToClean = pidTmp;
                                        targetReady = true;
                                    }
                                }
                            }
                            catch (IOException)
                            {
                                // Ultimate fallback: PID-unique script
                                scriptPath = Path.Combine(engineDir, string.Format("app_engine_pid_{0}.ps1", Process.GetCurrentProcess().Id));
                                _tempScriptToClean = scriptPath;
                                using (FileStream fs = new FileStream(scriptPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                                {
                                    fs.Write(engineBytes, 0, engineBytes.Length);
                                    fs.Flush(true);
                                }
                                ApplySecureFileAcl(scriptPath);
                            }
                        }
                    }
                    else if (!File.Exists(scriptPath))
                    {
                        // Fallback for development if run directly from source folder
                        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
                        string devScript = Path.Combine(currentDir, "src", "AppServer.ps1");
                        if (!File.Exists(devScript))
                        {
                            devScript = Path.Combine(currentDir, "AppServer.ps1");
                        }
                        if (File.Exists(devScript))
                        {
                            scriptPath = devScript;
                        }
                    }
                }

                // Register cleanup hook on process exit
                AppDomain.CurrentDomain.ProcessExit += (sender, eventArgs) =>
                {
                    CleanupTempScript(_tempScriptToClean);
                    RestoreLidActionFallback(_localAppDir);
                };

                // Identify host executable location
                string hostExe = null;
                try
                {
                    hostExe = Assembly.GetExecutingAssembly().Location;
                }
                catch { }
                if (string.IsNullOrEmpty(hostExe) || !File.Exists(hostExe))
                {
                    try
                    {
                        hostExe = Process.GetCurrentProcess().MainModule.FileName;
                    }
                    catch { }
                }

                // Prepare PowerShell launcher arguments with robust Windows quoting
                StringBuilder argBuilder = new StringBuilder();
                argBuilder.Append("-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File ");
                argBuilder.Append(EscapeProcessArgument(scriptPath));

                // Forward command line arguments
                if (args != null && args.Length > 0)
                {
                    foreach (string arg in args)
                    {
                        argBuilder.Append(" ").Append(EscapeProcessArgument(arg));
                    }
                }

                string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string psExePath = Path.Combine(systemDir, @"WindowsPowerShell\v1.0\powershell.exe");
                if (!File.Exists(psExePath))
                {
                    psExePath = "powershell.exe";
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = psExePath,
                    Arguments = argBuilder.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                if (!string.IsNullOrEmpty(hostExe) && File.Exists(hostExe))
                {
                    psi.EnvironmentVariables["FTI_HOST_EXE"] = hostExe;
                }

                using (Process proc = Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit();
                        RestoreLidActionFallback(_localAppDir);
                        CleanupTempScript(_tempScriptToClean);
                        return proc.ExitCode;
                    }
                }

                RestoreLidActionFallback(_localAppDir);
                CleanupTempScript(_tempScriptToClean);
                return 0;
            }
            catch (Exception ex)
            {
                try
                {
                    RestoreLidActionFallback(_localAppDir);
                    CleanupTempScript(_tempScriptToClean);
                    if (!string.IsNullOrEmpty(_localAppDir) && !Directory.Exists(_localAppDir))
                    {
                        Directory.CreateDirectory(_localAppDir);
                    }
                    if (!string.IsNullOrEmpty(_localAppDir))
                    {
                        string logFile = Path.Combine(_localAppDir, "launcher_error.log");
                        File.AppendAllText(logFile, string.Format("[{0}] Launcher Exception: {1}\r\n", DateTime.Now, ex));
                    }
                }
                catch { }

                return 1;
            }
        }

        private static string ComputeShortSha256(byte[] data)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static bool HasOtherActiveInstance()
        {
            try
            {
                int myPid = Process.GetCurrentProcess().Id;
                string myName = Process.GetCurrentProcess().ProcessName;
                Process[] procs = Process.GetProcessesByName(myName);
                try
                {
                    foreach (Process p in procs)
                    {
                        if (p.Id != myPid)
                        {
                            return true;
                        }
                    }
                }
                finally
                {
                    foreach (Process p in procs)
                    {
                        try { p.Dispose(); } catch { }
                    }
                }
            }
            catch { }
            return false;
        }

        private static void EnsureSecureDirectory(string dirPath)
        {
            if (Directory.Exists(dirPath))
            {
                if ((File.GetAttributes(dirPath) & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(dirPath, false);
                }
            }

            DirectorySecurity dSecurity = new DirectorySecurity();
            dSecurity.SetAccessRuleProtection(true, false);

            SecurityIdentifier adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            SecurityIdentifier systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            dSecurity.SetOwner(adminSid);

            FileSystemAccessRule adminRule = new FileSystemAccessRule(
                adminSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            FileSystemAccessRule systemRule = new FileSystemAccessRule(
                systemSid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow);

            dSecurity.AddAccessRule(adminRule);
            dSecurity.AddAccessRule(systemRule);

            if (!Directory.Exists(dirPath))
            {
                try
                {
                    Directory.CreateDirectory(dirPath, dSecurity);
                }
                catch (Exception)
                {
                    Directory.CreateDirectory(dirPath);
                }
            }
            DirectoryInfo di = new DirectoryInfo(dirPath);
            try
            {
                di.SetAccessControl(dSecurity);
            }
            catch (Exception)
            {
                DirectorySecurity fallbackSec = new DirectorySecurity();
                fallbackSec.SetAccessRuleProtection(true, false);
                fallbackSec.AddAccessRule(adminRule);
                fallbackSec.AddAccessRule(systemRule);
                di.SetAccessControl(fallbackSec);
            }
        }

        private static void ApplySecureFileAcl(string filePath)
        {
            try
            {
                SecurityIdentifier adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                SecurityIdentifier systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

                FileSecurity fileSec = new FileSecurity();
                fileSec.SetAccessRuleProtection(true, false);
                fileSec.SetOwner(adminSid);

                fileSec.AddAccessRule(new FileSystemAccessRule(
                    adminSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

                fileSec.AddAccessRule(new FileSystemAccessRule(
                    systemSid,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow));

                File.SetAccessControl(filePath, fileSec);
            }
            catch (Exception)
            {
                try
                {
                    SecurityIdentifier adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    SecurityIdentifier systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

                    FileSecurity fallbackFileSec = new FileSecurity();
                    fallbackFileSec.SetAccessRuleProtection(true, false);
                    fallbackFileSec.AddAccessRule(new FileSystemAccessRule(adminSid, FileSystemRights.FullControl, AccessControlType.Allow));
                    fallbackFileSec.AddAccessRule(new FileSystemAccessRule(systemSid, FileSystemRights.FullControl, AccessControlType.Allow));
                    File.SetAccessControl(filePath, fallbackFileSec);
                }
                catch { }
            }
        }

        private static void CleanupTempScript(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && (path.Contains(".tmp.ps1") || path.Contains("app_engine_pid_")) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }

        private static string EscapeProcessArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return "\"\"";
            }

            bool needsQuotes = false;
            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == ' ' || c == '\t' || c == '"')
                {
                    needsQuotes = true;
                    break;
                }
            }

            if (!needsQuotes)
            {
                return arg;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append('"');
            int backslashCount = 0;

            for (int i = 0; i < arg.Length; i++)
            {
                char c = arg[i];
                if (c == '\\')
                {
                    backslashCount++;
                }
                else if (c == '"')
                {
                    sb.Append(new string('\\', backslashCount * 2 + 1));
                    sb.Append('"');
                    backslashCount = 0;
                }
                else
                {
                    if (backslashCount > 0)
                    {
                        sb.Append(new string('\\', backslashCount));
                        backslashCount = 0;
                    }
                    sb.Append(c);
                }
            }

            if (backslashCount > 0)
            {
                sb.Append(new string('\\', backslashCount * 2));
            }
            sb.Append('"');

            return sb.ToString();
        }

        private static void RestoreLidActionFallback(string localAppDir)
        {
            try
            {
                // Multi-instance guard: do not restore/delete lid backup while another FTI instance is still running
                if (HasOtherActiveInstance()) return;

                if (string.IsNullOrEmpty(localAppDir))
                {
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    localAppDir = Path.Combine(localAppData, "FTI_PC_Inventory_Tool");
                }
                if (!Directory.Exists(localAppDir)) return;
                string backupFile = Path.Combine(localAppDir, "lid_power_backup.json");
                if (File.Exists(backupFile))
                {
                    string content = File.ReadAllText(backupFile);
                    int acVal = 1;
                    int dcVal = 1;
                    int pAc = content.IndexOf("\"ac\":");
                    if (pAc >= 0)
                    {
                        string s = content.Substring(pAc + 5).Trim();
                        int end = s.IndexOfAny(new char[] { ',', '}', '\r', '\n' });
                        if (end > 0) int.TryParse(s.Substring(0, end).Trim(), out acVal);
                    }
                    int pDc = content.IndexOf("\"dc\":");
                    if (pDc >= 0)
                    {
                        string s = content.Substring(pDc + 5).Trim();
                        int end = s.IndexOfAny(new char[] { ',', '}', '\r', '\n' });
                        if (end > 0) int.TryParse(s.Substring(0, end).Trim(), out dcVal);
                    }

                    string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
                    string powerCfgPath = Path.Combine(systemDir, "powercfg.exe");
                    if (!File.Exists(powerCfgPath)) powerCfgPath = "powercfg.exe";

                    ProcessStartInfo psi = new ProcessStartInfo(powerCfgPath)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    };
                    psi.Arguments = string.Format("/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION {0}", acVal);
                    using (Process p = Process.Start(psi)) { if (p != null) p.WaitForExit(1000); }
                    psi.Arguments = string.Format("/setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION {0}", dcVal);
                    using (Process p = Process.Start(psi)) { if (p != null) p.WaitForExit(1000); }
                    psi.Arguments = "/setactive SCHEME_CURRENT";
                    using (Process p = Process.Start(psi)) { if (p != null) p.WaitForExit(1000); }

                    File.Delete(backupFile);
                }
            }
            catch { }
        }
    }
}
