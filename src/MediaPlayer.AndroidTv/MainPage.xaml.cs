using System.Net.Http.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CommunityToolkit.Maui.Views;

namespace MediaPlayer.AndroidTv;

public partial class MainPage : ContentPage
{
    private const string SavedServerKey = "media-player-server-url";
    private const string DefaultServerUrl = "http://brique2:6050";
    private const string PreviousDefaultServerUrl = "http://sidotron:6050";
    private static readonly Color Panel = Color.FromArgb("#17181F");
    private static readonly Color Muted = Color.FromArgb("#B8BAC2");
    private static readonly Color Red = Color.FromArgb("#E50914");

    private string serverUrl = DefaultServerUrl;
    private string activeSection = "Accueil";
    private bool showingSettings;
    private bool showingDetails;
    private bool showingPlayer;
    private MediaElement? activePlayer;
    private LibraryItem? activeLibraryItem;
    private DateTimeOffset lastProgressSavedAt;
    private readonly SemaphoreSlim progressSaveGate = new(1, 1);
    private bool progressSavePending;
    private List<MediaItem> catalog = [];
    private List<MediaItem> popularInIndexers = [];
    private List<MediaItem> downloadedMedia = [];
    private List<MediaItem> watchlistMedia = [];
    private List<MediaItem> continueWatching = [];
    private List<GenreRow> genreRows = [];
    private CancellationTokenSource pageLifetime = new();
    private bool serverReachable;
    private bool catalogConfigured = true;
    private bool isDiscoveringServers;

    public MainPage()
    {
        InitializeComponent();
        serverUrl = Preferences.Default.Get(SavedServerKey, DefaultServerUrl);
        if (string.Equals(serverUrl, PreviousDefaultServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            serverUrl = DefaultServerUrl;
            Preferences.Default.Set(SavedServerKey, serverUrl);
        }

        BuildNavigation();
        _ = LoadCatalogAsync();
    }

    private void BuildNavigation()
    {
        SettingsButton.IsVisible = !showingPlayer;
        HeaderNavigation.Children.Clear();
        foreach (var section in new[] { "Accueil", "Films", "Séries", "Bibliothèque" })
        {
            var button = CreateButton(section, isSelected: !showingSettings && !showingDetails && activeSection == section, primary: false);
            button.Clicked += async (_, _) => await NavigateToSectionAsync(section);
            HeaderNavigation.Children.Add(button);
        }
    }

    private async Task NavigateToSectionAsync(string section)
    {
        activeSection = section;
        showingSettings = false;
        showingDetails = false;
        BuildNavigation();
        if (section == "Bibliothèque") await ShowLibraryAsync();
        else BuildHome();
    }

    private void BuildHome(string? message = null, bool resetScroll = true)
    {
        PageContent.Children.Clear();
        if (resetScroll) PageScroll.ScrollToAsync(0, 0, false);

        var visible = activeSection switch
        {
            "Films" => catalog.Where(item => item.MediaType == "movie").ToList(),
            "Séries" => catalog.Where(item => item.MediaType == "tv").ToList(),
            _ => catalog
        };

        var featured = visible.FirstOrDefault();
        Button? focusTarget = null;
        var hero = new Grid { HeightRequest = 432, BackgroundColor = Color.FromArgb("#17181D") };
        if (!string.IsNullOrWhiteSpace(featured?.Backdrop))
            hero.Children.Add(new Image { Source = featured.Backdrop, Aspect = Aspect.AspectFill, Opacity = 0.38 });
        hero.Children.Add(new BoxView { BackgroundColor = Color.FromArgb("#99090A0E") });
        var copy = new VerticalStackLayout
        {
            Spacing = 13,
            Padding = new Thickness(80, 28, 40, 34),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Start,
            MaximumWidthRequest = 900
        };
        copy.Add(Eyebrow(featured is null ? "VOTRE CINÉMA, À LA MAISON" : $"À LA UNE · {(featured.MediaType == "tv" ? "SÉRIE" : "FILM")}"));
        copy.Add(new Label { Text = featured?.Title ?? "Vos histoires, en grand.", FontSize = 46, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation });
        copy.Add(new Label
        {
            Text = message ?? featured?.Overview ?? "Découvrez les films et séries du moment, puis lancez la lecture depuis votre bibliothèque personnelle.",
            FontSize = 17, TextColor = Color.FromArgb("#D1D1D6"), LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 3, MaximumWidthRequest = 720
        });
        var actions = new HorizontalStackLayout { Spacing = 12 };
        if (featured is not null)
        {
            var details = CreateButton("ⓘ  Plus d’infos", primary: false);
            focusTarget = details;
            details.Clicked += async (_, _) => await OpenDetailsAsync(featured);
            actions.Add(details);
        }
        copy.Add(actions);
        hero.Children.Add(copy);
        PageContent.Add(hero);

        var body = new VerticalStackLayout { Padding = new Thickness(70, 28, 70, 52), Spacing = 28 };
        body.Add(new HorizontalStackLayout
        {
            Spacing = 9,
            Children = { new BoxView { Color = Red, WidthRequest = 8, HeightRequest = 8, CornerRadius = 4, VerticalOptions = LayoutOptions.Center },
                new Label { Text = catalog.Count == 0 ? "Connexion au serveur…" : "Le meilleur du moment, sélectionné pour vous", TextColor = Muted, FontSize = 15, VerticalOptions = LayoutOptions.Center } }
        });

        if (catalog.Count == 0)
        {
            body.Add(new Label { Text = string.IsNullOrWhiteSpace(message) ? "Chargement du catalogue…" : message, TextColor = Colors.White, FontSize = 22 });
        }
        else if (visible.Count == 0)
        {
            body.Add(new Label { Text = "Aucun résultat pour le moment.", TextColor = Muted, FontSize = 20 });
        }
        else
        {
            body.Add(Eyebrow(activeSection == "Films" ? "FILMS" : activeSection == "Séries" ? "SÉRIES" : "CHOISI POUR VOUS"));
            body.Add(new Label { Text = activeSection == "Films" ? "Films du moment" : activeSection == "Séries" ? "Séries du moment" : "Tendances du moment", TextColor = Colors.White, FontSize = 29, FontAttributes = FontAttributes.Bold });
            AddPosterRow(body, visible.Take(20).ToList());
        }
        PageContent.Add(body);
        if (activeSection == "Accueil")
        {
            AddOptionalCarousel(body, "Reprendre la lecture", continueWatching, "À REPRENDRE");
            AddOptionalCarousel(body, "À regarder", downloadedMedia, "DANS VOTRE CACHE");
            AddOptionalCarousel(body, "À surveiller", watchlistMedia, "VOTRE LISTE");
        }
        AddOptionalCarousel(body, "Populaires dans vos indexeurs", FilterMedia(popularInIndexers), null);
        foreach (var row in genreRows.Where(row => row.Media.Count > 0 && (activeSection == "Accueil" || row.AppliesTo(activeSection))))
            AddOptionalCarousel(body, row.Name, FilterMedia(row.Media), null);
        if (resetScroll && focusTarget is not null) MainThread.BeginInvokeOnMainThread(() => focusTarget.Focus());
    }

    private void AddOptionalCarousel(VerticalStackLayout parent, string title, List<MediaItem> items, string? eyebrow)
    {
        if (items.Count == 0) return;
        var section = new VerticalStackLayout { Spacing = 10 };
        if (!string.IsNullOrWhiteSpace(eyebrow)) section.Add(Eyebrow(eyebrow));
        section.Add(new Label { Text = title, TextColor = Colors.White, FontSize = 28, FontAttributes = FontAttributes.Bold });
        AddPosterRow(section, items.Take(20).ToList());
        parent.Add(section);
    }

    private void AddPosterRow(VerticalStackLayout parent, List<MediaItem> items)
    {
        var row = new CollectionView
        {
            ItemsSource = items,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal) { ItemSpacing = 18 },
            ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
            SelectionMode = SelectionMode.None,
            HeightRequest = 326,
            ItemTemplate = new DataTemplate(() =>
            {
                var host = new ContentView();
                host.BindingContextChanged += (_, _) =>
                {
                    if (host.BindingContext is MediaItem item) host.Content = CreatePosterCard(item);
                };
                return host;
            })
        };
        parent.Add(row);
    }

    private List<MediaItem> FilterMedia(List<MediaItem> items) => activeSection switch
    {
        "Films" => items.Where(item => item.MediaType == "movie").ToList(),
        "Séries" => items.Where(item => item.MediaType == "tv").ToList(),
        _ => items
    };

    private View CreatePosterCard(MediaItem item)
    {
        var imageGrid = new Grid { HeightRequest = 255, WidthRequest = 172, BackgroundColor = Color.FromArgb("#24252D") };
        if (!string.IsNullOrWhiteSpace(item.Poster)) imageGrid.Add(new Image { Source = item.Poster, Aspect = Aspect.AspectFill });
        else imageGrid.Add(new Label { Text = item.Title, TextColor = Colors.White, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, Padding = 12 });
        var hitTarget = new Button { Text = "", BackgroundColor = Colors.Transparent, BorderWidth = 0, CornerRadius = 0, WidthRequest = 172, HeightRequest = 255, Padding = 0 };
        hitTarget.Clicked += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(item.LibraryPath))
                await OpenLibraryItemAsync(new LibraryItem(item.Title, item.LibraryPath), item.CurrentTimeSeconds);
            else await OpenDetailsAsync(item);
        };
        imageGrid.Add(hitTarget);

        var card = new VerticalStackLayout { WidthRequest = 172, Spacing = 7 };
        var posterBorder = new Border { Stroke = Color.FromArgb("#35363D"), StrokeThickness = 1, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 }, Content = imageGrid };
        hitTarget.Focused += (_, _) => { posterBorder.Stroke = Colors.White; posterBorder.StrokeThickness = 4; posterBorder.Scale = 1.06; };
        hitTarget.Unfocused += (_, _) => { posterBorder.Stroke = Color.FromArgb("#35363D"); posterBorder.StrokeThickness = 1; posterBorder.Scale = 1; };
        card.Add(posterBorder);
        card.Add(new Label { Text = item.Title, TextColor = Colors.White, FontSize = 15, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 });
        card.Add(new Label { Text = item.LibraryPath is not null ? item.CurrentTimeSeconds > 0 ? "Reprendre" : "Dans la bibliothèque" : item.MediaType == "tv" ? "Série" : "Film", TextColor = Muted, FontSize = 13 });
        if (item.DurationSeconds > 0)
            card.Add(new ProgressBar { Progress = Math.Clamp(item.CurrentTimeSeconds / item.DurationSeconds, 0, 1), ProgressColor = Red, BackgroundColor = Panel, HeightRequest = 5 });
        return card;
    }

    private async Task OpenDetailsAsync(MediaItem item)
    {
        showingDetails = true;
        PageContent.Clear();
        PageContent.Add(new Label { Text = "Chargement des informations…", TextColor = Muted, FontSize = 20, Margin = new Thickness(80, 60) });
        try
        {
            using var client = CreateClient();
            var type = item.MediaType == "tv" ? "tv" : "movie";
            var details = await client.GetFromJsonAsync<MediaDetails>($"api/catalog/{type}/{item.Id}", pageLifetime.Token);
            if (details is null) throw new HttpRequestException("Le serveur n’a pas renvoyé de détails.");
            PageContent.Clear();
            var layout = new Grid { HeightRequest = 650, BackgroundColor = Panel };
            if (!string.IsNullOrWhiteSpace(details.Backdrop)) layout.Add(new Image { Source = details.Backdrop, Aspect = Aspect.AspectFill, Opacity = 0.33 });
            layout.Add(new BoxView { BackgroundColor = Color.FromArgb("#CC090A0E") });
            var text = new VerticalStackLayout { Spacing = 14, Padding = new Thickness(80, 52), VerticalOptions = LayoutOptions.End, MaximumWidthRequest = 1100 };
            text.Add(Eyebrow(type == "tv" ? "SÉRIE · TMDB" : "FILM · TMDB"));
            text.Add(new Label { Text = details.Title, TextColor = Colors.White, FontSize = 46, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation });
            var facts = new List<string>();
            if (details.Year is not null) facts.Add(details.Year.Value.ToString());
            if (details.Rating > 0) facts.Add($"★ {details.Rating:0.0} / 10");
            if (details.RuntimeMinutes is not null) facts.Add($"{details.RuntimeMinutes} min");
            if (details.SeasonCount is not null) facts.Add($"{details.SeasonCount} saison(s)");
            if (facts.Count > 0) text.Add(new Label { Text = string.Join("     ·     ", facts), TextColor = Colors.White, FontSize = 16 });
            if (details.Genres is { Count: > 0 }) text.Add(new Label { Text = string.Join(" · ", details.Genres), TextColor = Muted, FontSize = 15 });
            text.Add(new Label { Text = string.IsNullOrWhiteSpace(details.Overview) ? "Aucun synopsis n’est disponible." : details.Overview, TextColor = Color.FromArgb("#D0D1D6"), FontSize = 18, LineHeight = 1.35, MaximumWidthRequest = 850, MaxLines = 5 });
            layout.Add(text);
            PageContent.Add(layout);
            FocusActiveNavigationTab();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            PageContent.Clear();
            PageContent.Add(ErrorPanel($"Impossible de charger les informations : {ex.Message}", async () => await OpenDetailsAsync(item)));
        }
    }

    private async Task ShowLibraryAsync()
    {
        PageContent.Clear();
        var body = new VerticalStackLayout { Padding = new Thickness(76, 40), Spacing = 20 };
        body.Add(Eyebrow("VOTRE COLLECTION"));
        body.Add(new Label { Text = "Ma bibliothèque", TextColor = Colors.White, FontSize = 38, FontAttributes = FontAttributes.Bold });
        body.Add(new Label { Text = "Vos médias et les films que vous souhaitez voir.", TextColor = Muted, FontSize = 17 });
        PageContent.Add(body);
        try
        {
            using var client = CreateClient();
            await Task.WhenAll(LoadContinueWatchingAsync(client), LoadLocalMediaAsync(client));
            var library = await client.GetFromJsonAsync<LibraryResponse>("api/library", pageLifetime.Token);
            var files = (library?.Items ?? []).Select(file =>
            {
                var progress = continueWatching.FirstOrDefault(item => string.Equals(item.LibraryPath, file.Path, StringComparison.Ordinal));
                return new MediaItem(0, file.Name, null, progress?.Poster, null, null, 0, null, null,
                    LibraryPath: file.Path, CurrentTimeSeconds: progress?.CurrentTimeSeconds ?? 0, DurationSeconds: progress?.DurationSeconds ?? 0);
            }).ToList();

            if (files.Count == 0 && watchlistMedia.Count == 0)
            {
                body.Add(new Label { Text = "Aucun média ni film surveillé pour le moment.", TextColor = Muted, FontSize = 19, Margin = new Thickness(0, 14) });
                return;
            }

            if (files.Count > 0) AddOptionalCarousel(body, "Vos médias", files, "DANS VOTRE BIBLIOTHÈQUE");
            AddOptionalCarousel(body, "Films à surveiller", watchlistMedia, "VOTRE LISTE");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            body.Add(new Label { Text = $"Bibliothèque indisponible : {ex.Message}", TextColor = Color.FromArgb("#FF8C92"), FontSize = 16 });
        }
    }

    private Task OpenLibraryItemAsync(LibraryItem item, double resumePositionSeconds = 0)
    {
        if (resumePositionSeconds <= 0)
            resumePositionSeconds = continueWatching.FirstOrDefault(entry => string.Equals(entry.LibraryPath, item.Path, StringComparison.Ordinal))?.CurrentTimeSeconds ?? 0;
        var sourceUrl = new Uri(CreateClient().BaseAddress!, $"api/library/play/{Uri.EscapeDataString(item.Path)}");
        showingPlayer = true;
        activeLibraryItem = item;
        lastProgressSavedAt = DateTimeOffset.MinValue;
        showingSettings = false;
        showingDetails = false;
        BuildNavigation();
        HeaderBar.IsVisible = false;
        PageScroll.IsVisible = false;
        FullScreenPlayerHost.IsVisible = true;
        PageContent.Clear();

        var player = new MediaElement
        {
            Source = MediaSource.FromUri(sourceUrl),
            ShouldAutoPlay = true,
            ShouldKeepScreenOn = true,
            ShouldShowPlaybackControls = true,
            Aspect = Aspect.AspectFit,
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        player.MediaFailed += async (_, _) => await DisplayAlertAsync("Lecture impossible", "Le lecteur natif n’a pas pu décoder ce fichier. Le conteneur ou le codec audio/vidéo n’est peut-être pas pris en charge par cet appareil.", "OK");
        player.MediaOpened += async (_, _) =>
        {
            if (resumePositionSeconds > 0) await player.SeekTo(TimeSpan.FromSeconds(resumePositionSeconds));
        };
        player.PositionChanged += (_, args) =>
        {
            if (args.Position > TimeSpan.Zero && !progressSavePending && DateTimeOffset.UtcNow - lastProgressSavedAt >= TimeSpan.FromSeconds(10))
                _ = SaveCurrentPlaybackProgressAsync(force: false);
        };
        activePlayer = player;
        FullScreenPlayerHost.Children.Clear();
        FullScreenPlayerHost.Children.Add(player);
        return Task.CompletedTask;
    }

    private async Task ReturnFromPlayerAsync()
    {
        await SaveCurrentPlaybackProgressAsync(force: true);
        activePlayer?.Stop();
        if (activePlayer is not null) activePlayer.Source = null;
        activePlayer = null;
        activeLibraryItem = null;
        FullScreenPlayerHost.Children.Clear();
        FullScreenPlayerHost.IsVisible = false;
        HeaderBar.IsVisible = true;
        PageScroll.IsVisible = true;
        showingPlayer = false;
        BuildNavigation();
        await ShowLibraryAsync();
    }

    private async Task SaveCurrentPlaybackProgressAsync(bool force)
    {
        var player = activePlayer;
        var item = activeLibraryItem;
        if (player is null || item is null || player.Duration <= TimeSpan.Zero || player.Position <= TimeSpan.Zero) return;
        if (!force && (progressSavePending || DateTimeOffset.UtcNow - lastProgressSavedAt < TimeSpan.FromSeconds(10))) return;

        progressSavePending = true;
        await progressSaveGate.WaitAsync();
        try
        {
            if (activePlayer != player || activeLibraryItem != item || player.Duration <= TimeSpan.Zero || player.Position <= TimeSpan.Zero) return;
            using var client = CreateClient();
            var request = new PlaybackProgressRequest(0, "library", item.Name, player.Position.TotalSeconds,
                player.Duration.TotalSeconds, LibraryPath: item.Path);
            lastProgressSavedAt = DateTimeOffset.UtcNow;
            using var response = await client.PostAsJsonAsync("api/watch-progress", request, pageLifetime.Token);
            if (response.IsSuccessStatusCode)
            {
                var current = player.Position.TotalSeconds;
                var duration = player.Duration.TotalSeconds;
                continueWatching = continueWatching.Where(entry => !string.Equals(entry.LibraryPath, item.Path, StringComparison.Ordinal)).ToList();
                if (current / duration < .95 && duration - current > 30)
                    continueWatching.Insert(0, new MediaItem(0, item.Name, null, null, null, "library", 0, null, null,
                        LibraryPath: item.Path, CurrentTimeSeconds: current, DurationSeconds: duration));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException) { }
        finally
        {
            progressSaveGate.Release();
            progressSavePending = false;
        }
    }

    private void FocusActiveNavigationTab()
    {
        var selectedIndex = Array.IndexOf(new[] { "Accueil", "Films", "Séries", "Bibliothèque" }, activeSection);
        if (selectedIndex >= 0 && selectedIndex < HeaderNavigation.Children.Count && HeaderNavigation.Children[selectedIndex] is Button button)
            MainThread.BeginInvokeOnMainThread(() => button.Focus());
    }

    private async Task LoadCatalogAsync(bool discoverOnFailure = true)
    {
        try
        {
            using var client = CreateClient();
            var response = await client.GetFromJsonAsync<CatalogResponse>("api/catalog", pageLifetime.Token);
            catalog = response?.Results ?? [];
            serverReachable = response is not null;
            catalogConfigured = response?.Configured != false;
            if (showingSettings) return;
            BuildHome(response?.Configured == false ? "Le catalogue TMDB n’est pas configuré sur le serveur." : null);
            _ = LoadHomeCarouselsAsync(catalogConfigured);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            serverReachable = false;
            catalog = [];
            if (!showingSettings)
            {
                BuildHome(discoverOnFailure ? "Serveur introuvable. Recherche automatique sur le réseau…" : "Impossible de joindre le serveur. Vérifiez l’adresse dans les réglages.");
                var settings = CreateButton("Configurer l’adresse du serveur", primary: true);
                settings.Clicked += OnSettingsClicked;
                PageContent.Add(settings);
                if (discoverOnFailure) _ = DiscoverOnStartupAsync();
            }
        }
    }

    private async Task DiscoverOnStartupAsync()
    {
        var found = await DiscoverServersAsync();
        if (pageLifetime.IsCancellationRequested || showingSettings || showingPlayer || showingDetails) return;
        if (found.Count == 1)
        {
            serverUrl = found[0].Url;
            Preferences.Default.Set(SavedServerKey, serverUrl);
            await LoadCatalogAsync(discoverOnFailure: false);
        }
        else if (found.Count > 1)
        {
            MainThread.BeginInvokeOnMainThread(() => ShowSettings(found));
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(() => BuildHome("Aucun serveur MediaPlayer trouvé. Vérifiez que le serveur et le téléviseur sont sur le même réseau."));
        }
    }

    private async Task<List<DiscoveredServer>> DiscoverServersAsync()
    {
        if (isDiscoveringServers) return [];
        isDiscoveringServers = true;
        try
        {
            var ports = GetDiscoveryPorts();
            var cancellationToken = pageLifetime.Token;
            var preferHttps = Uri.TryCreate(serverUrl, UriKind.Absolute, out var current) && current.Scheme == Uri.UriSchemeHttps;
            return await Task.Run(async () =>
            {
                var candidates = GetLocalSubnetAddresses();
                var found = new System.Collections.Concurrent.ConcurrentBag<DiscoveredServer>();
                using var concurrency = new SemaphoreSlim(48);
                var probes = candidates.SelectMany(address => ports.Select(port => ProbeServerAsync(address, port, preferHttps, concurrency, found, cancellationToken)));
                await Task.WhenAll(probes);
                return found.DistinctBy(server => server.Url, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(server => server.Address, StringComparer.Ordinal).ThenBy(server => server.Port).ToList();
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or OperationCanceledException) { return []; }
        finally { isDiscoveringServers = false; }
    }

    private int[] GetDiscoveryPorts()
    {
        var ports = new HashSet<int> { 6050 };
        if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var current) && current.Port is > 0 and <= 65535)
            ports.Add(current.Port);
        return ports.ToArray();
    }

    private static List<IPAddress> GetLocalSubnetAddresses()
    {
        var addresses = new HashSet<IPAddress>();
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(item =>
                     item.OperationalStatus == OperationalStatus.Up && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            IPInterfaceProperties properties;
            try { properties = network.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            foreach (var unicast in properties.UnicastAddresses.Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var bytes = unicast.Address.GetAddressBytes();
                if (!IsPrivateIpv4(bytes)) continue;
                var prefix = Math.Clamp(unicast.PrefixLength, (byte)16, (byte)32);
                // Cap large home subnets to the /24 containing this device instead of generating tens of thousands of hosts.
                var scanPrefix = Math.Max(prefix, (byte)24);
                var ip = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
                var mask = scanPrefix == 0 ? 0u : uint.MaxValue << (32 - scanPrefix);
                var networkAddress = ip & mask;
                var broadcastAddress = networkAddress | ~mask;
                for (var candidate = networkAddress + 1; candidate < broadcastAddress; candidate++)
                {
                    var candidateBytes = new byte[4];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(candidateBytes, candidate);
                    addresses.Add(new IPAddress(candidateBytes));
                }
            }
        }
        return addresses.ToList();
    }

    private static bool IsPrivateIpv4(byte[] address) => address.Length == 4 &&
        (address[0] == 10 || address[0] == 192 && address[1] == 168 || address[0] == 172 && address[1] is >= 16 and <= 31);

    private async Task ProbeServerAsync(IPAddress address, int port, bool preferHttps, SemaphoreSlim concurrency,
        System.Collections.Concurrent.ConcurrentBag<DiscoveredServer> found, CancellationToken cancellationToken)
    {
        try
        {
            await concurrency.WaitAsync(cancellationToken);
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(450));
                using var socket = new TcpClient(AddressFamily.InterNetwork);
                await socket.ConnectAsync(address, port, probeTimeout.Token);

                var schemes = preferHttps ? new[] { Uri.UriSchemeHttps, Uri.UriSchemeHttp } : new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps };
                foreach (var scheme in schemes)
                {
                    var url = $"{scheme}://{address}:{port}";
                    try
                    {
                        using var client = new HttpClient { BaseAddress = new Uri(url + "/"), Timeout = TimeSpan.FromMilliseconds(900) };
                        using var response = await client.GetAsync("api/catalog/local-status", cancellationToken);
                        if (!response.IsSuccessStatusCode) continue;
                        var state = await response.Content.ReadFromJsonAsync<LocalMediaStateResponse>(cancellationToken: cancellationToken);
                        if (state is not null) found.Add(new DiscoveredServer(address.ToString(), port, url, $"MediaPlayer · {address}:{port}"));
                        break;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or UriFormatException or System.Text.Json.JsonException) { }
                }
            }
            finally { concurrency.Release(); }
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or HttpRequestException or UriFormatException) { }
    }

    private async Task LoadHomeCarouselsAsync(bool loadGenres)
    {
        using var client = CreateClient();
        await Task.WhenAll(LoadContinueWatchingAsync(client), LoadLocalMediaAsync(client), LoadIndexerPopularAsync(client));
        RefreshHomeIfVisible();
        if (!loadGenres) return;
        await LoadGenreRowsAsync(client);
        RefreshHomeIfVisible();
    }

    private void RefreshHomeIfVisible()
    {
        if (showingSettings || showingDetails || showingPlayer || activeSection == "Bibliothèque" || pageLifetime.IsCancellationRequested) return;
        MainThread.BeginInvokeOnMainThread(() => BuildHome(resetScroll: false));
    }

    private async Task LoadContinueWatchingAsync(HttpClient client)
    {
        try
        {
            var entries = await client.GetFromJsonAsync<List<WatchProgressEntry>>("api/watch-progress", pageLifetime.Token) ?? [];
            continueWatching = entries.Where(item => !item.IsCompleted).Select(item => new MediaItem(item.TmdbId, item.Title,
                null, item.PosterUrl ?? FindCatalogPoster(item.TmdbId, item.MediaType), null, item.MediaType, 0, null, null,
                LibraryPath: item.LibraryPath, CurrentTimeSeconds: item.CurrentTimeSeconds, DurationSeconds: item.DurationSeconds)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { continueWatching = []; }
    }

    private async Task LoadLocalMediaAsync(HttpClient client)
    {
        try
        {
            var state = await client.GetFromJsonAsync<LocalMediaStateResponse>("api/catalog/local-status", pageLifetime.Token);
            downloadedMedia = (state?.Downloads ?? []).Where(item => item.Completed && item.LastViewedAt is null)
                .Select(ToMediaItem).ToList();
            watchlistMedia = (state?.Watchlist ?? []).DistinctBy(item => $"{item.MediaType}-{item.TmdbId}", StringComparer.OrdinalIgnoreCase)
                .Select(ToMediaItem).ToList();
            var targets = downloadedMedia.Concat(watchlistMedia)
                .Where(item => string.IsNullOrWhiteSpace(item.Poster) && !string.IsNullOrWhiteSpace(item.MediaType))
                .DistinctBy(item => $"{item.MediaType}-{item.Id}", StringComparer.OrdinalIgnoreCase).ToArray();
            await Task.WhenAll(targets.Select(async item =>
            {
                try
                {
                    var details = await client.GetFromJsonAsync<MediaDetails>($"api/catalog/{item.MediaType}/{item.Id}", pageLifetime.Token);
                    if (!string.IsNullOrWhiteSpace(details?.Poster)) SetLocalPoster(item.Id, item.MediaType!, details.Poster);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            }));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            downloadedMedia = [];
            watchlistMedia = [];
        }
    }

    private async Task LoadIndexerPopularAsync(HttpClient client)
    {
        try
        {
            var snapshot = await client.GetFromJsonAsync<IndexerPopularResponse>("api/catalog/indexer-popular", pageLifetime.Token);
            popularInIndexers = (snapshot?.Results ?? []).Select(item => new MediaItem(item.Id, item.Title, item.Overview,
                item.Poster, item.Backdrop, item.MediaType, item.Rating, item.OriginalLanguage, item.Year, item.TmdbPopularity)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { popularInIndexers = []; }
    }

    private async Task LoadGenreRowsAsync(HttpClient client)
    {
        var rows = await Task.WhenAll(GenreDefinitions.Select(async genre =>
        {
            var requests = new List<Task<List<MediaItem>>>();
            if (genre.MovieId is int movieGenre) requests.Add(LoadGenreMediaAsync(client, "movie", movieGenre));
            if (genre.TvId is int tvGenre) requests.Add(LoadGenreMediaAsync(client, "tv", tvGenre));
            var media = (await Task.WhenAll(requests)).SelectMany(items => items)
                .DistinctBy(item => $"{item.MediaType}-{item.Id}", StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(item => item.Popularity).Take(20).ToList();
            return new GenreRow(genre.Name, genre.MovieId.HasValue, genre.TvId.HasValue, media);
        }));
        genreRows = rows.ToList();
    }

    private static async Task<List<MediaItem>> LoadGenreMediaAsync(HttpClient client, string mediaType, int genreId)
    {
        try
        {
            var response = await client.GetFromJsonAsync<CatalogResponse>($"api/catalog/genre/{mediaType}/{genreId}");
            return response?.Results ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return []; }
    }

    private static MediaItem ToMediaItem(LocalMediaEntry item) => new(item.TmdbId, item.Title, null, null, null,
        item.MediaType, 0, item.OriginalLanguage, item.Year);

    private string? FindCatalogPoster(int id, string mediaType) => catalog.FirstOrDefault(item => item.Id == id && item.MediaType == mediaType)?.Poster;

    private void SetLocalPoster(int id, string mediaType, string poster)
    {
        downloadedMedia = SetPoster(downloadedMedia, id, mediaType, poster);
        watchlistMedia = SetPoster(watchlistMedia, id, mediaType, poster);
    }

    private static List<MediaItem> SetPoster(List<MediaItem> items, int id, string mediaType, string poster) => items
        .Select(item => item.Id == id && item.MediaType == mediaType ? item with { Poster = poster } : item).ToList();

    private HttpClient CreateClient() => new() { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(12) };

    private static View ErrorPanel(string message, Func<Task> retry)
    {
        var layout = new VerticalStackLayout { Padding = new Thickness(80, 60), Spacing = 18 };
        layout.Add(new Label { Text = message, TextColor = Color.FromArgb("#FF8C92"), FontSize = 18 });
        var button = CreateButton("Réessayer", primary: true);
        button.Clicked += async (_, _) => await retry();
        layout.Add(button);
        return layout;
    }

    private static Label Eyebrow(string text) => new() { Text = text.ToUpperInvariant(), TextColor = Muted, FontSize = 12, CharacterSpacing = 2.3, FontAttributes = FontAttributes.Bold };

    private static Button CreateButton(string text, bool isSelected = false, bool primary = true)
    {
        var button = new Button
        {
            Text = text,
            TextColor = Colors.White,
            BackgroundColor = primary ? Red : isSelected ? Red : Color.FromArgb("#191A20"),
            BorderColor = isSelected ? Red : Color.FromArgb("#555761"),
            BorderWidth = isSelected ? 2 : 1,
            FontAttributes = FontAttributes.Bold,
            FontSize = 16,
            Padding = new Thickness(20, 12),
            CornerRadius = 7,
            MinimumHeightRequest = 48
        };
        return button.WithStrongFocus();
    }

    private static bool TryNormalizeServerAddress(string? input, out string normalized, out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        var value = input?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value)) { error = $"Saisissez une adresse, par exemple {DefaultServerUrl}."; return false; }
        if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.IsLoopback || uri.Host is "0.0.0.0" or "::")
        {
            error = $"Utilisez l’adresse du serveur sur le réseau, par exemple {DefaultServerUrl}.";
            return false;
        }
        normalized = uri.ToString().TrimEnd('/');
        return true;
    }

    protected override bool OnBackButtonPressed()
    {
        if (showingPlayer) { _ = ReturnFromPlayerAsync(); return true; }
        if (showingSettings) { showingSettings = false; BuildNavigation(); BuildHome(); return true; }
        if (showingDetails) { showingDetails = false; BuildNavigation(); BuildHome(); return true; }
        if (activeSection != "Accueil") { _ = NavigateToSectionAsync("Accueil"); return true; }
        return base.OnBackButtonPressed();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        pageLifetime.Cancel();
    }

    private void OnSettingsClicked(object? sender, EventArgs e) => ShowSettings();

    private void ShowSettings(List<DiscoveredServer>? initialResults = null)
    {
        showingSettings = true;
        showingDetails = false;
        BuildNavigation();
        PageContent.Clear();
        var panel = new VerticalStackLayout { Padding = new Thickness(80, 66), Spacing = 18, MaximumWidthRequest = 900, HorizontalOptions = LayoutOptions.Start };
        panel.Add(Eyebrow("CONNEXION"));
        panel.Add(new Label { Text = "Adresse du serveur", TextColor = Colors.White, FontSize = 38, FontAttributes = FontAttributes.Bold });
        panel.Add(new Label { Text = "Indiquez l’adresse de votre serveur MediaPlayer sur le réseau local.", TextColor = Muted, FontSize = 18 });
        var address = new Entry { Text = serverUrl, Placeholder = "http://brique2:6050", TextColor = Colors.White, PlaceholderColor = Muted, BackgroundColor = Panel, FontSize = 20, ReturnType = ReturnType.Go };
        var status = new Label { TextColor = Color.FromArgb("#FF8C92"), FontSize = 15, IsVisible = false };
        var results = new VerticalStackLayout { Spacing = 8 };
        var discover = CreateButton("Détecter les serveurs sur le réseau", primary: false);
        discover.Clicked += async (_, _) =>
        {
            discover.IsEnabled = false;
            status.TextColor = Muted;
            status.Text = "Recherche sur le réseau local…";
            status.IsVisible = true;
            results.Children.Clear();
            var found = await DiscoverServersAsync();
            discover.IsEnabled = true;
            if (found.Count == 0)
            {
                status.TextColor = Color.FromArgb("#FF8C92");
                status.Text = "Aucun serveur MediaPlayer trouvé sur ce réseau.";
                return;
            }
            status.TextColor = Muted;
            status.Text = found.Count == 1 ? "Un serveur trouvé :" : $"{found.Count} serveurs trouvés :";
            AddDiscoveredServerButtons(found, results, address, status);
        };
        var connect = CreateButton("Se connecter", primary: true);
        connect.Clicked += async (_, _) => await ConnectToServerAsync(address.Text, address, status);
        panel.Add(address);
        panel.Add(discover);
        panel.Add(status);
        panel.Add(results);
        panel.Add(connect);
        PageContent.Add(panel);
        if (initialResults is { Count: > 0 })
        {
            status.TextColor = Muted;
            status.Text = initialResults.Count == 1 ? "Un serveur trouvé :" : $"{initialResults.Count} serveurs trouvés :";
            status.IsVisible = true;
            AddDiscoveredServerButtons(initialResults, results, address, status);
        }
        MainThread.BeginInvokeOnMainThread(() => address.Focus());
    }

    private void AddDiscoveredServerButtons(List<DiscoveredServer> servers, VerticalStackLayout results, Entry address, Label status)
    {
        foreach (var server in servers)
        {
            var select = CreateButton(server.DisplayName, primary: false);
            select.HorizontalOptions = LayoutOptions.Fill;
            select.Clicked += async (_, _) => await ConnectToServerAsync(server.Url, address, status);
            results.Add(select);
        }
    }

    private async Task ConnectToServerAsync(string? input, Entry address, Label status)
    {
        if (!TryNormalizeServerAddress(input, out var normalized, out var error))
        {
            status.Text = error;
            status.TextColor = Color.FromArgb("#FF8C92");
            status.IsVisible = true;
            address.Focus();
            return;
        }

        address.Text = normalized;
        serverUrl = normalized;
        Preferences.Default.Set(SavedServerKey, serverUrl);
        status.TextColor = Muted;
        status.Text = "Connexion au serveur…";
        status.IsVisible = true;
        catalog = [];
        await LoadCatalogAsync(discoverOnFailure: false);
        if (!serverReachable)
        {
            status.TextColor = Color.FromArgb("#FF8C92");
            status.Text = "Serveur inaccessible. Vérifiez l’adresse et le réseau.";
            return;
        }

        showingSettings = false;
        activeSection = "Accueil";
        BuildNavigation();
        BuildHome(catalogConfigured ? null : "Le catalogue TMDB n’est pas configuré sur le serveur.");
        _ = LoadHomeCarouselsAsync(catalogConfigured);
    }

    private sealed record CatalogResponse(bool Configured, List<MediaItem> Results);
    private sealed record MediaItem(int Id, string Title, string? Overview, string? Poster, string? Backdrop, string? MediaType, double Rating, string? OriginalLanguage, int? Year, double Popularity = 0, string? LibraryPath = null, double CurrentTimeSeconds = 0, double DurationSeconds = 0);
    private sealed record MediaDetails(string Title, string? Overview, string? Poster, string? Backdrop, string? MediaType, double Rating, int? Year, List<string>? Genres, int? RuntimeMinutes, int? SeasonCount);
    private sealed record LibraryResponse(List<LibraryItem> Items);
    private sealed record LibraryItem(string Name, string Path);
    private sealed record DiscoveredServer(string Address, int Port, string Url, string DisplayName);
    private sealed record WatchProgressEntry(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds, double DurationSeconds, double ProgressPercent, DateTimeOffset UpdatedAt, string? PosterUrl = null, int? SeasonNumber = null, int? EpisodeNumber = null, bool IsCompleted = false, string? LibraryPath = null);
    private sealed record PlaybackProgressRequest(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds, double DurationSeconds, string? PosterUrl = null, int? SeasonNumber = null, int? EpisodeNumber = null, string? LibraryPath = null);
    private sealed record LocalMediaStateResponse(List<LocalMediaEntry>? Downloads, List<LocalMediaEntry>? Watchlist);
    private sealed record LocalMediaEntry(int TmdbId, string MediaType, string Title, string? OriginalLanguage = null, int? Year = null, bool Completed = false, DateTimeOffset? LastViewedAt = null);
    private sealed record IndexerPopularResponse(List<IndexerPopularItem>? Results);
    private sealed record IndexerPopularItem(int Id, string Title, string? Overview, string? Poster, string? Backdrop, string? MediaType, double Rating, string? OriginalLanguage, int? Year, double TmdbPopularity);
    private sealed record GenreDefinition(string Name, int? MovieId, int? TvId);
    private sealed class GenreRow(string name, bool hasMovies, bool hasSeries, List<MediaItem> media)
    {
        public string Name { get; } = name;
        public bool HasMovies { get; } = hasMovies;
        public bool HasSeries { get; } = hasSeries;
        public List<MediaItem> Media { get; } = media;
        public bool AppliesTo(string section) => section == "Films" ? HasMovies : section == "Séries" && HasSeries;
    }

    private static readonly GenreDefinition[] GenreDefinitions =
    [
        new("Action & aventure", 28, 10759), new("Animation", 16, 16), new("Comédie", 35, 35),
        new("Crime", 80, 80), new("Documentaire", 99, 99), new("Drame", 18, 18),
        new("Familial", 10751, 10751), new("Science-fiction & fantastique", 878, 10765),
        new("Histoire", 36, null), new("Horreur", 27, null), new("Mystère", 9648, 9648),
        new("Romance", 10749, null), new("Thriller", 53, null), new("Guerre & politique", 10752, 10768),
        new("Western", 37, 37), new("Jeunesse", null, 10762), new("Réalité", null, 10764),
        new("Soap", null, 10766), new("Talk-show", null, 10767)
    ];
}

internal static class NativeFocusStyle
{
    public static Button WithStrongFocus(this Button button)
    {
        var normal = new VisualState { Name = "Normal" };
        normal.Setters.Add(new Setter { Property = Button.BorderColorProperty, Value = button.BorderColor });
        normal.Setters.Add(new Setter { Property = Button.BorderWidthProperty, Value = button.BorderWidth });
        normal.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1d });
        var focused = new VisualState { Name = "Focused" };
        focused.Setters.Add(new Setter { Property = Button.BorderColorProperty, Value = Colors.White });
        focused.Setters.Add(new Setter { Property = Button.BorderWidthProperty, Value = 4d });
        focused.Setters.Add(new Setter { Property = VisualElement.ScaleProperty, Value = 1.06d });
        var group = new VisualStateGroup { Name = "CommonStates" };
        group.States.Add(normal);
        group.States.Add(focused);
        VisualStateManager.SetVisualStateGroups(button, new VisualStateGroupList { group });
        return button;
    }
}
