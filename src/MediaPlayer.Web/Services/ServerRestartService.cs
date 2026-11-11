using System.Diagnostics;
using System.Reflection;

namespace media_player.Services;

public sealed class ServerRestartService(
    IHostApplicationLifetime lifetime,
    IWebHostEnvironment environment,
    ILogger<ServerRestartService> logger)
{
    private int restartRequested;
    private readonly string watchMarkerPath = Path.Combine(environment.ContentRootPath, "App_Data", "server-restart-marker.cs");

    public void ScheduleRestart(HttpContext context)
    {
        if (Interlocked.Exchange(ref restartRequested, 1) != 0) return;

        context.Response.OnCompleted(async () =>
        {
            if (IsRunningInContainer())
            {
                logger.LogInformation("Stopping the server process so the container restart policy can restart it.");
            }
            else if (string.Equals(Environment.GetEnvironmentVariable("DOTNET_WATCH"), "1", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ScheduleDotnetWatchRestart();
                    logger.LogInformation("Scheduled dotnet watch to restart the server.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                    or NotSupportedException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    logger.LogWarning(ex, "Could not signal dotnet watch; restarting the server process directly.");
                    lifetime.ApplicationStopped.Register(StartReplacementProcess);
                }
            }
            else
            {
                lifetime.ApplicationStopped.Register(StartReplacementProcess);
            }

            lifetime.StopApplication();
        });
    }

    private void ScheduleDotnetWatchRestart()
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("The current process path is unavailable.");
        var startInfo = new ProcessStartInfo(processPath)
        {
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false
        };

        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssemblyPath))
                throw new InvalidOperationException("The application assembly path is unavailable.");
            startInfo.ArgumentList.Add(entryAssemblyPath);
        }

        startInfo.ArgumentList.Add("--signal-dotnet-watch");
        startInfo.ArgumentList.Add(watchMarkerPath);
        var signalProcess = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The dotnet watch restart signal process could not be started.");
        signalProcess.Dispose();
    }

    private static bool IsRunningInContainer() =>
        IsEnabled(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"))
        || IsEnabled(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINERS"));

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private void StartReplacementProcess()
    {
        try
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("The current process path is unavailable.");
            var startInfo = new ProcessStartInfo(processPath)
            {
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = false
            };

            if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(entryAssemblyPath))
                    throw new InvalidOperationException("The application assembly path is unavailable.");
                startInfo.ArgumentList.Add(entryAssemblyPath);
            }

            foreach (var argument in Environment.GetCommandLineArgs().Skip(1))
                startInfo.ArgumentList.Add(argument);

            var replacement = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The replacement server process could not be started.");
            logger.LogInformation("Started replacement server process {ProcessId}.", replacement.Id);
            replacement.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not restart the server process.");
        }
    }
}
