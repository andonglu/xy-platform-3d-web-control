using System.Windows;

namespace MotionControl.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow(EquipmentApplication.CreateViewModel());
        MainWindow = window;
        window.Show();
    }
}
