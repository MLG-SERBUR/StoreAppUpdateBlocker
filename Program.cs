using System;
using System.Threading;
using Windows.ApplicationModel.Store.Preview.InstallControl;

namespace StoreBlocker
{
    class Program
    {
        // Add or remove apps you want to block here
        static readonly string[] BlockedApps =[
            "microsoft.windowscommunicationsapps",
            "Microsoft.Office.OneNote",
            "GroupMe.GroupMe"
        ];

        static void Main()
        {
            var appManager = new AppInstallManager();

            // Subscribe to the event. The OS will instantly notify us, no polling required!
            appManager.ItemStatusChanged += (sender, args) =>
            {
                var item = args.Item;
                string pfn = item.PackageFamilyName ?? "";
                string pid = item.ProductId ?? "";

                foreach (var blocked in BlockedApps)
                {
                    if (pfn.Contains(blocked, StringComparison.OrdinalIgnoreCase) || 
                        pid.Contains(blocked, StringComparison.OrdinalIgnoreCase))
                    {
                        try 
                        { 
                            item.Cancel(); 
                        } 
                        catch { /* Ignore if it throws an error while cancelling */ }
                    }
                }
            };

            // Suspend this thread indefinitely. 
            // The program stays alive, but uses absolutely 0% CPU.
            Thread.Sleep(Timeout.Infinite);
        }
    }
}