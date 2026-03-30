# https://gist.github.com/MLG-SERBUR/1c33affa08c3c4e8f5bfbb40d04824e6

# 1. Ensure this is running in native Windows PowerShell 5.1
if ($PSVersionTable.PSVersion.Major -ne 5) {
    Write-Host "This script requires native Windows PowerShell 5.1. Please run using powershell.exe." -ForegroundColor Red
    exit
}

# 2. Define the apps you want to block
$BlockedApps = @(
    "microsoft.windowscommunicationsapps",
    "Microsoft.Office.OneNote",
    "GroupMe.GroupMe"
)

# 3. Explicitly load the core .NET Windows Runtime Bridge
Add-Type -AssemblyName System.Runtime.WindowsRuntime


$StoreType =[Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager, Windows.ApplicationModel.Store.Preview, ContentType=WindowsRuntime]


# 4. Create the Store Manager object
$AppManager = New-Object -TypeName Windows.ApplicationModel.Store.Preview.InstallControl.AppInstallManager

Write-Host "Watching Microsoft Store update queue... Press Ctrl+C to stop." -ForegroundColor Cyan

# 5. Background loop to monitor the live download queue
while ($true) {
    $Queue = $AppManager.AppInstallItems
    
    if ($Queue -and $Queue.Count -gt 0) {
        foreach ($Item in $Queue) {
            foreach ($Blocked in $BlockedApps) {
                
                # If an app in the queue matches your block list...
                if ($Item.PackageFamilyName -match $Blocked -or $Item.ProductId -match $Blocked) {
                    
                    Write-Host "Intercepted update for $($Item.PackageFamilyName)! Canceling..." -ForegroundColor Yellow
                    $Item.Cancel()
                }
            }
        }
    }
    
    Start-Sleep -Seconds 3
}