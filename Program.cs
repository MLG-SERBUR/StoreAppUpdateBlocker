using System;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Store.Preview.InstallControl;

namespace StoreBlocker
{
    internal static class Program
    {
        private static readonly string[] BlockedApps = new[]
        {
            "microsoft.windowscommunicationsapps",
            "Microsoft.Office.OneNote",
            "GroupMe.GroupMe"
        };

        private static readonly object LogLock = new object();
        private static readonly object AttemptLock = new object();
        private static readonly Dictionary<string, DateTimeOffset> RecentAttempts =
            new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        private const string InstanceMutexName = @"Local\StoreAppUpdateBlocker";
        private const string InstanceInfoFileName = "active-instance.txt";
        private const int SwHide = 0;
        private const uint WmQuit = 0x0012;
        private const uint PmNoRemove = 0x0000;
        private static readonly TimeSpan DuplicateSuppressWindow = TimeSpan.FromSeconds(15);
        private static readonly TimeSpan ReplaceExistingTimeout = TimeSpan.FromSeconds(10);

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Msg
        {
            public IntPtr Hwnd;
            public uint Message;
            public UIntPtr WParam;
            public IntPtr LParam;
            public uint Time;
            public Point Pt;
            public uint LPrivate;
        }

        private enum WatchMode
        {
            EventHook,
            QueueScan
        }

        private sealed class Options
        {
            public WatchMode Mode { get; private set; } = WatchMode.EventHook;
            public TimeSpan ScanInterval { get; private set; } = TimeSpan.FromSeconds(3);
            public bool Background { get; private set; }
            public bool ReplaceExisting { get; private set; } = true;
            public bool ShowHelp { get; private set; }

            public static Options Parse(string[] args)
            {
                var options = new Options();

                for (var i = 0; i < args.Length; i++)
                {
                    var argument = args[i];

                    switch (argument.ToLowerInvariant())
                    {
                        case "--event-hook":
                            options.Mode = WatchMode.EventHook;
                            break;
                        case "--queue-scan":
                            options.Mode = WatchMode.QueueScan;
                            break;
                        case "--scan-interval":
                            if (i + 1 >= args.Length)
                            {
                                throw new ArgumentException("--scan-interval requires a value in seconds.");
                            }

                            if (!double.TryParse(args[++i], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                            {
                                throw new ArgumentException("--scan-interval must be a positive number.");
                            }

                            options.ScanInterval = TimeSpan.FromSeconds(seconds);
                            break;
                        case "--background":
                            options.Background = true;
                            break;
                        case "--replace-existing":
                            options.ReplaceExisting = true;
                            break;
                        case "--exit-if-running":
                            options.ReplaceExisting = false;
                            break;
                        case "--help":
                        case "-h":
                        case "/?":
                            options.ShowHelp = true;
                            break;
                        default:
                            throw new ArgumentException("Unknown argument: " + argument);
                    }
                }

                return options;
            }
        }

        private sealed class InstanceInfo
        {
            public int ProcessId { get; init; }
            public long StartTimeUtcTicks { get; init; }
            public string ExecutablePath { get; init; } = string.Empty;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, UIntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref Msg lpMsg);

        [STAThread]
        private static int Main(string[] args)
        {
            Options options;

            try
            {
                options = Options.Parse(args);
            }
            catch (ArgumentException ex)
            {
                WriteError(ex.Message);
                PrintHelp();
                return 1;
            }

            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (options.Background)
            {
                HideConsoleWindow();
            }

            using var instanceMutex = new Mutex(true, InstanceMutexName, out var createdNew);
            var ownsMutex = createdNew;

            if (!ownsMutex)
            {
                if (!options.ReplaceExisting)
                {
                    WriteInfo("Another instance is already running.");
                    return 0;
                }

                if (!TryReplaceExistingInstance(instanceMutex))
                {
                    return 1;
                }

                ownsMutex = true;
            }

            try
            {
                WriteInstanceInfo();
                WriteInfo("Store App Update Blocker starting.");
                WriteInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "Mode: {0}. PID: {1}. Log: {2}",
                    options.Mode,
                    Environment.ProcessId,
                    GetLogPath()));
                WriteInfo("Blocked apps: " + string.Join(", ", BlockedApps));

                AppInstallManager appManager;
                try
                {
                    appManager = new AppInstallManager();
                }
                catch (Exception ex)
                {
                    WriteError("Failed to create AppInstallManager.", ex);
                    return 1;
                }

                try
                {
                    return options.Mode == WatchMode.QueueScan
                        ? RunQueueScan(appManager, options)
                        : RunEventHook(appManager);
                }
                catch (Exception ex)
                {
                    WriteError("Unhandled fatal error.", ex);
                    return 1;
                }
            }
            finally
            {
                ClearInstanceInfo();

                if (ownsMutex)
                {
                    try
                    {
                        instanceMutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // Another shutdown path may have already released the mutex.
                    }
                }
            }
        }

        private static int RunEventHook(AppInstallManager appManager)
        {
            var threadId = GetCurrentThreadId();
            ConsoleCancelEventHandler cancelHandler = (sender, e) =>
            {
                e.Cancel = true;

                if (!PostThreadMessage(threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero))
                {
                    WriteError("Failed to stop the event hook cleanly. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                }
            };

            Console.CancelKeyPress += cancelHandler;
            appManager.ItemStatusChanged += OnItemStatusChanged;

            PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
            WriteInfo("Event hook is active. Waiting for Store status changes. Press Ctrl+C to stop.");

            try
            {
                while (true)
                {
                    var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                    if (result == 0)
                    {
                        WriteInfo("Event hook stopped.");
                        return 0;
                    }

                    if (result == -1)
                    {
                        WriteError("The event hook message loop failed. Win32 error: " + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture));
                        return 1;
                    }

                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            finally
            {
                appManager.ItemStatusChanged -= OnItemStatusChanged;
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private static int RunQueueScan(AppInstallManager appManager, Options options)
        {
            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (sender, e) =>
            {
                e.Cancel = true;
                shutdown.Cancel();
            };

            Console.CancelKeyPress += cancelHandler;
            WriteInfo(string.Format(
                CultureInfo.InvariantCulture,
                "Queue scan is active. Interval: {0:0.###} seconds. Press Ctrl+C to stop.",
                options.ScanInterval.TotalSeconds));

            try
            {
                RunQueueScanAsync(appManager, options.ScanInterval, shutdown.Token).GetAwaiter().GetResult();
                return 0;
            }
            catch (OperationCanceledException)
            {
                WriteInfo("Queue scan stopped.");
                return 0;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }

        private static async Task RunQueueScanAsync(AppInstallManager appManager, TimeSpan interval, CancellationToken cancellationToken)
        {
            ScanQueue(appManager);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                ScanQueue(appManager);
            }
        }

        private static void ScanQueue(AppInstallManager appManager)
        {
            IReadOnlyList<AppInstallItem> queue;

            try
            {
                queue = appManager.AppInstallItems;
            }
            catch (Exception ex)
            {
                WriteError("Failed to enumerate AppInstallItems.", ex);
                return;
            }

            foreach (var item in queue)
            {
                HandleInstallItem(item, "scan");
            }
        }

        private static void OnItemStatusChanged(AppInstallManager sender, AppInstallManagerItemEventArgs args)
        {
            HandleInstallItem(args.Item, "event");
        }

        private static void HandleInstallItem(AppInstallItem item, string source)
        {
            var packageFamilyName = item.PackageFamilyName ?? string.Empty;
            var productId = item.ProductId ?? string.Empty;

            if (!TryMatchBlockedApp(packageFamilyName, productId, out var matchedApp))
            {
                return;
            }

            var state = GetInstallState(item);
            if (!CanCancelInState(state) ||
                !ShouldAttemptCancel(packageFamilyName, productId, state))
            {
                return;
            }

            try
            {
                item.Cancel();
                WriteInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "Canceled {0} item. Match='{1}', State={2}, PFN='{3}', ProductId='{4}'.",
                    source,
                    matchedApp,
                    state.HasValue ? state.Value.ToString() : "unknown",
                    packageFamilyName,
                    productId));
            }
            catch (Exception ex)
            {
                WriteError(string.Format(
                    CultureInfo.InvariantCulture,
                    "Failed to cancel {0} item. Match='{1}', State={2}, PFN='{3}', ProductId='{4}'.",
                    source,
                    matchedApp,
                    state.HasValue ? state.Value.ToString() : "unknown",
                    packageFamilyName,
                    productId), ex);
            }
        }

        private static AppInstallState? GetInstallState(AppInstallItem item)
        {
            try
            {
                return item.GetCurrentStatus().InstallState;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryMatchBlockedApp(string packageFamilyName, string productId, out string matchedApp)
        {
            foreach (var blockedApp in BlockedApps)
            {
                if (!string.IsNullOrWhiteSpace(packageFamilyName) &&
                    packageFamilyName.IndexOf(blockedApp, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedApp = blockedApp;
                    return true;
                }

                if (!string.IsNullOrWhiteSpace(productId) &&
                    productId.IndexOf(blockedApp, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchedApp = blockedApp;
                    return true;
                }
            }

            matchedApp = string.Empty;
            return false;
        }

        private static bool ShouldAttemptCancel(string packageFamilyName, string productId, AppInstallState? state)
        {
            var key = string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}",
                packageFamilyName,
                productId,
                state.HasValue ? state.Value.ToString() : "unknown");
            var now = DateTimeOffset.UtcNow;

            lock (AttemptLock)
            {
                PruneRecentAttempts(now);

                if (RecentAttempts.TryGetValue(key, out var lastAttempt) &&
                    now - lastAttempt < DuplicateSuppressWindow)
                {
                    return false;
                }

                RecentAttempts[key] = now;
                return true;
            }
        }

        private static bool CanCancelInState(AppInstallState? state)
        {
            if (!state.HasValue)
            {
                return true;
            }

            return state.Value != AppInstallState.Canceled &&
                   state.Value != AppInstallState.Completed &&
                   state.Value != AppInstallState.Error;
        }

        private static void PruneRecentAttempts(DateTimeOffset now)
        {
            List<string>? expiredKeys = null;
            var cutoff = now - DuplicateSuppressWindow;

            foreach (var attempt in RecentAttempts)
            {
                if (attempt.Value < cutoff)
                {
                    expiredKeys ??= new List<string>();
                    expiredKeys.Add(attempt.Key);
                }
            }

            if (expiredKeys == null)
            {
                return;
            }

            foreach (var key in expiredKeys)
            {
                RecentAttempts.Remove(key);
            }
        }

        private static void HideConsoleWindow()
        {
            var consoleWindow = GetConsoleWindow();
            if (consoleWindow != IntPtr.Zero)
            {
                ShowWindow(consoleWindow, SwHide);
            }
        }

        private static bool TryReplaceExistingInstance(Mutex instanceMutex)
        {
            using var currentProcess = Process.GetCurrentProcess();
            var currentProcessPath = GetProcessPath(currentProcess);
            var replaceableProcesses = FindReplaceableProcesses(currentProcess.Id, currentProcessPath);

            try
            {
                if (replaceableProcesses.Count == 0)
                {
                    WriteInfo("Another instance is already running. Waiting for it to exit so this copy can take over.");
                }

                foreach (var process in replaceableProcesses)
                {
                    if (!TryTerminateProcess(process))
                    {
                        return false;
                    }
                }

                var deadline = DateTimeOffset.UtcNow + ReplaceExistingTimeout;
                foreach (var process in replaceableProcesses)
                {
                    if (!WaitForProcessExit(process, deadline))
                    {
                        return false;
                    }
                }

                if (WaitForMutexOwnership(instanceMutex, deadline))
                {
                    WriteInfo("Existing blocker instance replaced successfully.");
                    return true;
                }

                WriteError("Timed out waiting for the previous blocker instance to release the single-instance lock.");
                return false;
            }
            finally
            {
                foreach (var process in replaceableProcesses)
                {
                    process.Dispose();
                }
            }
        }

        private static List<Process> FindReplaceableProcesses(int currentProcessId, string currentProcessPath)
        {
            var matches = new List<Process>();
            var seenProcessIds = new HashSet<int>();

            TryAddProcessFromInstanceInfo(matches, seenProcessIds, currentProcessId);

            if (string.IsNullOrWhiteSpace(currentProcessPath))
            {
                return matches;
            }

            var currentProcessName = Path.GetFileNameWithoutExtension(currentProcessPath);
            if (string.IsNullOrWhiteSpace(currentProcessName))
            {
                return matches;
            }

            foreach (var process in Process.GetProcessesByName(currentProcessName))
            {
                if (process.Id == currentProcessId || !seenProcessIds.Add(process.Id))
                {
                    process.Dispose();
                    continue;
                }

                var processPath = GetProcessPath(process);
                if (!string.Equals(processPath, currentProcessPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Dispose();
                    continue;
                }

                matches.Add(process);
            }

            return matches;
        }

        private static void TryAddProcessFromInstanceInfo(List<Process> matches, HashSet<int> seenProcessIds, int currentProcessId)
        {
            if (!TryReadInstanceInfo(out var instanceInfo) || instanceInfo.ProcessId == currentProcessId)
            {
                return;
            }

            Process? process = null;

            try
            {
                process = Process.GetProcessById(instanceInfo.ProcessId);
            }
            catch
            {
                return;
            }

            var processPath = GetProcessPath(process);
            if (!TryGetProcessStartTimeUtcTicks(process, out var startTimeUtcTicks) ||
                startTimeUtcTicks != instanceInfo.StartTimeUtcTicks ||
                (!string.IsNullOrWhiteSpace(instanceInfo.ExecutablePath) &&
                 !string.Equals(processPath, instanceInfo.ExecutablePath, StringComparison.OrdinalIgnoreCase)) ||
                !seenProcessIds.Add(process.Id))
            {
                process.Dispose();
                return;
            }

            matches.Add(process);
        }

        private static bool TryTerminateProcess(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    return true;
                }

                WriteInfo(string.Format(
                    CultureInfo.InvariantCulture,
                    "Stopping running blocker instance PID {0}.",
                    process.Id));
                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Exception ex)
            {
                WriteError(string.Format(
                    CultureInfo.InvariantCulture,
                    "Failed to stop running blocker instance PID {0}.",
                    process.Id), ex);
                return false;
            }
        }

        private static bool WaitForProcessExit(Process process, DateTimeOffset deadline)
        {
            try
            {
                if (process.HasExited)
                {
                    return true;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return false;
                }

                if (process.WaitForExit((int)Math.Ceiling(remaining.TotalMilliseconds)))
                {
                    return true;
                }

                WriteError(string.Format(
                    CultureInfo.InvariantCulture,
                    "Timed out waiting for blocker instance PID {0} to exit.",
                    process.Id));
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
            catch (Exception ex)
            {
                WriteError(string.Format(
                    CultureInfo.InvariantCulture,
                    "Failed while waiting for blocker instance PID {0} to exit.",
                    process.Id), ex);
                return false;
            }
        }

        private static bool WaitForMutexOwnership(Mutex instanceMutex, DateTimeOffset deadline)
        {
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            try
            {
                return instanceMutex.WaitOne(remaining);
            }
            catch (AbandonedMutexException)
            {
                return true;
            }
        }

        private static void WriteInstanceInfo()
        {
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                var startTimeUtcTicks = currentProcess.StartTime.ToUniversalTime().Ticks;
                var instanceInfoPath = GetInstanceInfoPath();
                var appDataDirectory = GetAppDataDirectory();

                Directory.CreateDirectory(appDataDirectory);
                File.WriteAllLines(instanceInfoPath, new[]
                {
                    "PID=" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    "StartTimeUtcTicks=" + startTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                    "Path=" + GetProcessPath(currentProcess)
                });
            }
            catch (Exception ex)
            {
                WriteError("Failed to write active instance metadata.", ex);
            }
        }

        private static void ClearInstanceInfo()
        {
            try
            {
                if (!TryReadInstanceInfo(out var instanceInfo))
                {
                    return;
                }

                if (instanceInfo.ProcessId != Environment.ProcessId)
                {
                    return;
                }

                using var currentProcess = Process.GetCurrentProcess();
                if (!TryGetProcessStartTimeUtcTicks(currentProcess, out var currentStartTimeUtcTicks) ||
                    currentStartTimeUtcTicks != instanceInfo.StartTimeUtcTicks)
                {
                    return;
                }

                File.Delete(GetInstanceInfoPath());
            }
            catch
            {
                // Shutdown cleanup should never take the process down.
            }
        }

        private static bool TryReadInstanceInfo(out InstanceInfo instanceInfo)
        {
            instanceInfo = new InstanceInfo();

            try
            {
                var instanceInfoPath = GetInstanceInfoPath();
                if (!File.Exists(instanceInfoPath))
                {
                    return false;
                }

                var lines = File.ReadAllLines(instanceInfoPath);
                var processId = -1;
                long startTimeUtcTicks = 0;
                var executablePath = string.Empty;

                foreach (var line in lines)
                {
                    if (line.StartsWith("PID=", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = int.TryParse(
                            line["PID=".Length..],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out processId);
                    }
                    else if (line.StartsWith("StartTimeUtcTicks=", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = long.TryParse(
                            line["StartTimeUtcTicks=".Length..],
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out startTimeUtcTicks);
                    }
                    else if (line.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                    {
                        executablePath = line["Path=".Length..];
                    }
                }

                if (processId <= 0 || startTimeUtcTicks <= 0)
                {
                    return false;
                }

                instanceInfo = new InstanceInfo
                {
                    ProcessId = processId,
                    StartTimeUtcTicks = startTimeUtcTicks,
                    ExecutablePath = executablePath
                };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetProcessStartTimeUtcTicks(Process process, out long startTimeUtcTicks)
        {
            try
            {
                startTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                return true;
            }
            catch
            {
                startTimeUtcTicks = 0;
                return false;
            }
        }

        private static string GetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Usage: StoreAppUpdateBlocker.exe [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  --event-hook            Use the AppInstallManager.ItemStatusChanged hook (default).");
            Console.WriteLine("  --queue-scan            Poll AppInstallItems instead of waiting for events.");
            Console.WriteLine("  --scan-interval <secs>  Queue scan interval in seconds. Default: 3.");
            Console.WriteLine("  --background            Hide the console window after startup.");
            Console.WriteLine("  --replace-existing      Replace a running blocker instance (default behavior).");
            Console.WriteLine("  --exit-if-running       Exit instead of replacing an existing blocker instance.");
            Console.WriteLine("  --help                  Show this help.");
        }

        private static void WriteInfo(string message)
        {
            WriteLog("INFO", message);
        }

        private static void WriteError(string message)
        {
            WriteLog("ERROR", message);
        }

        private static void WriteError(string message, Exception ex)
        {
            WriteLog("ERROR", message + " " + ex.GetType().Name + ": " + ex.Message);
        }

        private static void WriteLog(string level, string message)
        {
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}",
                DateTime.Now,
                level,
                message);

            lock (LogLock)
            {
                try
                {
                    var logPath = GetLogPath();
                    var logDirectory = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(logDirectory))
                    {
                        Directory.CreateDirectory(logDirectory);
                    }

                    File.AppendAllText(logPath, line + Environment.NewLine);
                }
                catch
                {
                    // Logging should never take the process down.
                }
            }

            try
            {
                Console.WriteLine(line);
            }
            catch
            {
                // Background mode may not have a usable console.
            }
        }

        private static string GetLogPath()
        {
            return Path.Combine(GetAppDataDirectory(), "StoreAppUpdateBlocker.log");
        }

        private static string GetInstanceInfoPath()
        {
            return Path.Combine(GetAppDataDirectory(), InstanceInfoFileName);
        }

        private static string GetAppDataDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StoreAppUpdateBlocker");
        }
    }
}
