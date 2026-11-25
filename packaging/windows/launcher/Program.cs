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
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new StartupForm());
    }

    private sealed class StartupForm : Form
    {
        private readonly Label status = new()
        {
            AutoSize = false,
            Location = new Point(22, 20),
            Size = new Size(400, 82),
            Text = "Bobine démarre…"
        };
        private readonly Button retry = new()
        {
            Location = new Point(22, 119),
            Size = new Size(170, 34),
            Text = "Réessayer",
            Visible = false
        };
        private readonly Button close = new()
        {
            Location = new Point(252, 119),
            Size = new Size(170, 34),
            Text = "Fermer"
        };
        private bool starting;

        public StartupForm()
        {
            Text = "Bobine";
            ClientSize = new Size(444, 174);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(status);
            Controls.Add(retry);
            Controls.Add(close);
            retry.Click += async (_, _) => await StartAsync();
            close.Click += (_, _) => Close();
            Shown += async (_, _) => await StartAsync();
        }

        private async Task StartAsync()
        {
            if (starting) return;
            starting = true;
            retry.Visible = false;
            status.Text = "Bobine démarre…";
            try
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
                    Close();
                    return;
                }

                status.Text = "Préparation du serveur…";
                InstallOrUpdateServer(packageRoot, serverRoot, dataRoot, version);

                var setupMarker = Path.Combine(dataRoot, "desktop-setup-complete");
                status.Text = "Le serveur démarre. Vous choisirez le dossier média dans la page de configuration.";
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
                startInfo.Environment["MediaPlayer__DesktopSetup"] = "true";
                startInfo.Environment["MediaPlayer__InternalApiBaseAddress"] = BaseUrl;
                using var serverProcess = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows n’a pas pu lancer le serveur.");

                for (var attempt = 0; attempt < 60; attempt++)
                {
                    if (await IsBobineRespondingAsync())
                    {
                        OpenBrowser(File.Exists(setupMarker) ? BaseUrl + "/" : BaseUrl + "/bienvenue");
                        Close();
                        return;
                    }
                    if (serverProcess.HasExited)
                        throw new InvalidOperationException($"Le serveur s’est arrêté au démarrage (code {serverProcess.ExitCode}).");
                    status.Text = $"Démarrage du serveur… ({attempt + 1}/60)";
                    await Task.Delay(500);
                }

                throw new InvalidOperationException("Le serveur n’a pas répondu après 30 secondes.");
            }
            catch (Exception ex)
            {
                ShowError($"Bobine n’a pas pu démarrer.\n{ex.Message}");
            }
            finally { starting = false; }
        }

        private void ShowError(string message)
        {
            status.Text = message;
            retry.Visible = true;
        }
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
        if (Directory.Exists(oldData)) CopyDirectory(oldData, Path.Combine(stage, "App_Data"));
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
