using System.Windows;
using ZanburakVideoProcessor.Services;

namespace ZanburakVideoProcessor;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        ProtocolRegistrar.EnsureCurrentUser();
        var taskId = ProtocolRegistrar.ParseTaskId(e.Args);
        var apiBase = ProtocolRegistrar.ParseApiBase(e.Args);
        var window = new MainWindow(taskId, apiBase);
        window.Show();
    }
}
