namespace MediaPlayer.AndroidTv;

public partial class MainPage : ContentPage
{
    private const string SavedServerKey = "media-player-server-url";

    public MainPage()
    {
        InitializeComponent();
        var savedServer = Preferences.Default.Get(SavedServerKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(savedServer))
        {
            ServerAddress.Text = savedServer;
            OpenServer(savedServer);
        }
        else
        {
            _ = FocusConnectButtonAsync();
        }
    }

    private async void OnConnectClicked(object? sender, EventArgs e)
    {
        ConnectionError.IsVisible = false;
        if (!TryNormalizeServerAddress(ServerAddress.Text, out var serverUrl, out var error))
        {
            ConnectionError.Text = error;
            ConnectionError.IsVisible = true;
            ServerAddress.Focus();
            return;
        }

        Preferences.Default.Set(SavedServerKey, serverUrl);
        ServerAddress.Text = serverUrl;
        OpenServer(serverUrl);
    }

    private void OpenServer(string serverUrl)
    {
        ConnectionPanel.IsVisible = false;
        Browser.IsVisible = true;
        ChangeServerButton.IsVisible = true;
        Browser.Source = new UrlWebViewSource { Url = serverUrl };
    }

    private async void OnChangeServerClicked(object? sender, EventArgs e)
    {
        Browser.IsVisible = false;
        ConnectionPanel.IsVisible = true;
        ConnectionError.IsVisible = false;
        await FocusConnectButtonAsync();
    }

    private void OnBrowserNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result == WebNavigationResult.Success) return;

        Browser.IsVisible = false;
        ChangeServerButton.IsVisible = false;
        ConnectionPanel.IsVisible = true;
        ConnectionError.Text = "Impossible de joindre le serveur. Vérifiez son adresse, son démarrage et le pare-feu du PC.";
        ConnectionError.IsVisible = true;
        _ = FocusConnectButtonAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        if (Browser.IsVisible && Browser.CanGoBack)
        {
            Browser.GoBack();
            return true;
        }

        if (Browser.IsVisible)
        {
            OnChangeServerClicked(this, EventArgs.Empty);
            return true;
        }

        return base.OnBackButtonPressed();
    }

    private async Task FocusConnectButtonAsync()
    {
        await Task.Delay(100);
        if (ConnectButton.IsVisible) ConnectButton.Focus();
    }

    private static bool TryNormalizeServerAddress(string? input, out string serverUrl, out string error)
    {
        serverUrl = string.Empty;
        error = string.Empty;
        var value = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Saisissez l’adresse du serveur, par exemple http://192.168.1.25:5102.";
            return false;
        }

        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.IsLoopback
            || uri.Host is "0.0.0.0" or "::")
        {
            error = "Utilisez une adresse réseau du serveur (pas localhost), par exemple http://192.168.1.25:5102.";
            return false;
        }

        serverUrl = uri.ToString().TrimEnd('/');
        return true;
    }
}
