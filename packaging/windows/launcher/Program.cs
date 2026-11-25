using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Windows.Forms;

namespace Bobine.Launcher;

internal static class Program
{
    private const string BaseUrl = "http://127.0.0.1:6050";
    private const string ListenUrl = "http://0.0.0.0:6050";

    [STAThread]
    private static async Task Main()
    {
        ApplicationConfiguration.Initialize();
        try { await StartAsync(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Bobine n’a pas pu démarrer.\n\n{ex.Message}", "Bobine", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static async Task StartAsync()
    {
        var packageRoot = AppContext.BaseDirectory;
        var version = File.ReadAllText(Path.Combine(packageRoot, "build-version.txt")).Trim();
        var supportRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bobine");
        var serverRoot = Path.Combine(supportRoot, "Server");
        var dataRoot = Path.Combine(serverRoot, "App_Data");
        Directory.CreateDirectory(supportRoot);

        if (await IsBobineRespondingAsync())
        {
            OpenBrowser(BaseUrl + "/");
            return;
        }

        InstallOrUpdateServer(packageRoot, serverRoot, dataRoot, version);

        var setupMarker = Path.Combine(dataRoot, "desktop-setup-complete");
        var mediaRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Bobine");
        if (!File.Exists(setupMarker))
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choisissez le dossier où Bobine conservera les médias et téléchargements.",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(mediaRoot) ? mediaRoot : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                ShowNewFolderButton = true
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;
            mediaRoot = dialog.SelectedPath;
        }

        Directory.CreateDirectory(mediaRoot);
        var serverExecutable = Path.Combine(serverRoot, "MediaPlayer.Web.exe");
        var startInfo = new ProcessStartInfo(serverExecutable)
        {
            WorkingDirectory = serverRoot,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(ListenUrl);
        startInfo.Environment["ASPNETCORE_URLS"] = ListenUrl;
        startInfo.Environment["Media__Root"] = mediaRoot;
        startInfo.Environment["MediaPlayer__DesktopSetup"] = "true";
        startInfo.Environment["MediaPlayer__InternalApiBaseAddress"] = BaseUrl;
        _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows n’a pas pu lancer le serveur.");

        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await IsBobineRespondingAsync())
            {
                OpenBrowser(File.Exists(setupMarker) ? BaseUrl + "/" : BaseUrl + "/bienvenue");
                return;
            }
            await Task.Delay(500);
        }

        var logPath = Path.Combine(supportRoot, "server.log");
        throw new InvalidOperationException($"Le serveur n’a pas répondu. Son journal se trouve ici : {logPath}");
    }

    private static void InstallOrUpdateServer(string packageRoot, string serverRoot, string dataRoot, string version)
    {
        var versionFile = Path.Combine(serverRoot, "desktop-package-version.txt");
        var serverExecutable = Path.Combine(serverRoot, "MediaPlayer.Web.exe");
        if (File.Exists(serverExecutable) && File.Exists(versionFile)
            && string.Equals(File.ReadAllText(versionFile).Trim(), version, StringComparison.Ordinal)) return;

        var parent = Path.GetDirectoryName(serverRoot)!;
        var stage = Path.Combine(parent, $"Server-staging-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $"Server-backup-{Guid.NewGuid():N}");
        CopyDirectory(Path.Combine(packageRoot, "Server"), stage);
        Directory.CreateDirectory(Path.Combine(stage, "App_Data"));

        var oldData = Path.Combine(serverRoot, "App_Data");
        if (Directory.Exists(oldData))
            CopyDirectory(oldData, Path.Combine(stage, "App_Data"));
        else Directory.CreateDirectory(dataRoot);
        File.WriteAllText(Path.Combine(stage, "desktop-package-version.txt"), version);

        try
        {
            if (Directory.Exists(serverRoot)) Directory.Move(serverRoot, backup);
            Directory.Move(stage, serverRoot);
        }
        catch
        {
            if (!Directory.Exists(serverRoot) && Directory.Exists(backup)) Directory.Move(backup, serverRoot);
            if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            throw;
        }
        if (Directory.Exists(backup)) Directory.Delete(backup, recursive: true);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    private static async Task<bool> IsBobineRespondingAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            using var response = await client.GetAsync(BaseUrl + "/api/users/status");
            return response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Unauthorized;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private static void OpenBrowser(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
