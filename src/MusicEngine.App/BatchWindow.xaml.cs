namespace MusicEngine.App;

using System.ComponentModel;
using System.Windows;
using ViewModels;

public partial class BatchWindow : Window
{
    private readonly BatchViewModel _vm;

    public BatchWindow(BatchViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Don't leave a resolve loop running against a closed window.
        _vm.CancelResolveCommand.Execute(null);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
