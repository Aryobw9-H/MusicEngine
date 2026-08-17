namespace MusicEngine.App.Ui;

using System;

public class WpfDispatcher : IDispatcher
{
    public void Run(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}