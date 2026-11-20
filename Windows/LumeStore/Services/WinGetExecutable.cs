namespace LumeStore.Services;

public static class WinGetExecutable
{
    public static IReadOnlyList<string> Candidates()
    {
        var candidates = new List<string>();
#if WINDOWS
        try
        {
            // Resolve the registered package for the current user. Do not rely on
            // the WindowsApps execution alias, which may exist but refuse CreateProcess.
            var manager = new Windows.Management.Deployment.PackageManager();
            var packages = manager.FindPackagesForUser("")
                .Where(p => p.Id.Name == "Microsoft.DesktopAppInstaller" && !p.IsResourcePackage)
                .OrderByDescending(p => p.Id.Version.Major)
                .ThenByDescending(p => p.Id.Version.Minor)
                .ThenByDescending(p => p.Id.Version.Build)
                .ThenByDescending(p => p.Id.Version.Revision);
            foreach (var package in packages)
            {
                var executable = Path.Combine(package.InstalledLocation.Path, "winget.exe");
                if (File.Exists(executable)) candidates.Add(executable);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Restricted environments can deny package discovery. Try normal aliases next.
        }
#endif
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"));
        candidates.Add("winget.exe");
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
