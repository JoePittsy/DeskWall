using System.Windows;

namespace DeskWall.Designer;

/// <summary>Phase 5 Task 8 replaces this with the real startup (store, monitor, first run).</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        MainWindow = new Window { Title = "DeskWall Designer", Width = 1200, Height = 800 };
        MainWindow.Show();
    }
}
