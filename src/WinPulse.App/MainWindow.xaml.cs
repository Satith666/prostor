using System.Windows;
using WinPulse.App.Infrastructure;

namespace WinPulse.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }
}
