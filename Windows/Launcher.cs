using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

internal static class Launcher
{
    private const string Version = "0.2.0";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (Stream payload = assembly.GetManifestResourceStream("LumePayload"))
            using (var archive = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                if (args.Length == 1 && args[0] == "--verify-payload")
                    return archive.GetEntry("LumeStore.exe") != null &&
                           archive.GetEntry("LumeStore.pri") != null &&
                           archive.GetEntry("Assets/Lume.ico") != null ? 0 : 1;

                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", "Lume Store");
                var target = Path.Combine(root, Version);
                var program = Path.Combine(target, "LumeStore.exe");
                using (var mutex = new Mutex(false, @"Local\LumeStoreLauncher"))
                {
                    try { mutex.WaitOne(); }
                    catch (AbandonedMutexException) { }
                    try
                    {
                        if (!File.Exists(program))
                        {
                            Directory.CreateDirectory(root);
                            var staging = Path.Combine(root, Guid.NewGuid().ToString("N"));
                            try
                            {
                                archive.ExtractToDirectory(staging);
                                Directory.Move(staging, target);
                            }
                            finally
                            {
                                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                            }
                        }
                    }
                    finally { mutex.ReleaseMutex(); }
                }
                Process.Start(new ProcessStartInfo(program) { WorkingDirectory = target, UseShellExecute = false });
            }
            return 0;
        }
        catch (Exception error)
        {
            if (Array.IndexOf(args, "--verify-payload") < 0)
                MessageBox.Show(error.Message, "Lume Store", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
