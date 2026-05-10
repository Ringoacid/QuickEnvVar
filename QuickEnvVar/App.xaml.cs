using System.Configuration;
using System.Data;
using System.Windows;

namespace QuickEnvVar;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();

        if (e.Args.Length > 0)
        {
            // 選択されたフォルダのパスを取得
            string targetPath = e.Args[0];
            mainWindow.PathToAdd = targetPath;
        }

        mainWindow.Show();
    }
}
