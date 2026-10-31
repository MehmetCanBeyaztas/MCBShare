using Microsoft.UI.Dispatching;
using Velopack;

namespace MCBShare.App;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        // Kurulum ve güncelleme kancaları pencere açılmadan çalışmalı; gerekirse süreç burada biter.
        VelopackApp.Build().Run();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
