using Microsoft.UI.Xaml;
namespace LumeStore;
public partial class App : Application
{
    private Window? window;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
    }
    private static void Log(Exception exception)
    {
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "startup-error.log"), exception.ToString() + Environment.NewLine); } catch { }
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try { window = new MainWindow(); window.Activate(); }
        catch (Exception ex) { Log(ex); throw; }
    }
}
