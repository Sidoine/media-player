using System.Net.Http.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Maui.Views;
using MediaPlayer.Localization;

namespace MediaPlayer.AndroidTv;

public partial class MainPage : ContentPage
{
    public static MainPage? Current { get; private set; }
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
    private MediaItem? playerReturnDetails;
    private CancellationTokenSource? playerControlsFadeCancellation;
    private DateTimeOffset lastProgressSavedAt;
    private readonly SemaphoreSlim progressSaveGate = new(1, 1);
    private bool progressSavePending;
    private List<MediaItem> catalog = [];
    private List<MediaItem> popularInIndexers = [];
    private List<MediaItem> downloadedMedia = [];
    private List<MediaItem> watchlistMedia = [];
    private List<MediaItem> continueWatching = [];
    private List<GenreRow> genreRows = [];
    private readonly List<CarouselRow> carouselRows = [];
    private CarouselFocus? focusedCarouselItem;
    private CarouselFocus? requestedCarouselItem;
    private CancellationTokenSource? carouselFocusCancellation;
    private CancellationTokenSource pageLifetime = new();
    private bool serverReachable;
    private bool catalogConfigured = true;
    private bool isDiscoveringServers;

    public MainPage()
    {
        InitializeComponent();
        Current = this;
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
        foreach (var (section, key) in new[] { ("Accueil", "Nav_Home"), ("Films", "Nav_Movies"), ("Séries", "Nav_Series"), ("Bibliothèque", "Nav_Library") })
        {
            var button = CreateButton(T(key), isSelected: !showingSettings && !showingDetails && activeSection == section, primary: false);
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
        CancelCarouselFocusRequest();
        PageContent.Children.Clear();
        carouselRows.Clear();
        focusedCarouselItem = null;
        if (resetScroll) PageScroll.ScrollToAsync(0, 0, false);

        var visible = activeSection switch
        {
            "Films" => catalog.Where(item => item.MediaType == "movie").ToList(),
            "Séries" => catalog.Where(item => item.MediaType == "tv").ToList(),
            _ => catalog
        };

        var featured = visible.FirstOrDefault();
        Button? focusTarget = null;
        var heroHeight = Math.Clamp((PageScroll.Height + HeaderBar.Height) * 0.78, 625, 790);
        var hero = new Grid { HeightRequest = heroHeight, BackgroundColor = Color.FromArgb("#17181D") };
        if (!string.IsNullOrWhiteSpace(featured?.Backdrop))
            hero.Children.Add(new Image { Source = featured.Backdrop, Aspect = Aspect.AspectFill });
        hero.Children.Add(new BoxView
        {
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Colors.Black, 0),
                new(Color.FromArgb("#E6090A0E"), 0.31f),
                new(Color.FromArgb("#3B090A0E"), 0.77f),
                new(Color.FromArgb("#17090A0E"), 1)
            }, new Point(0, 0), new Point(1, 0))
        });
        hero.Children.Add(new BoxView
        {
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new(Color.FromArgb("#F0090A0E"), 0),
                new(Colors.Transparent, 0.54f),
                new(Color.FromArgb("#090A0E"), 1)
            }, new Point(0, 1), new Point(0, 0))
        });
        var copy = new VerticalStackLayout
        {
            Spacing = 14,
            Padding = new Thickness(64, 0, 32, 84),
            VerticalOptions = LayoutOptions.End,
            HorizontalOptions = LayoutOptions.Start,
            MaximumWidthRequest = 650
        };
        copy.Add(Eyebrow(featured is null ? T("Home_PersonalCinema") : T(featured.MediaType == "tv" ? "Home_FeaturedSeries" : "Home_FeaturedMovie")));
        copy.Add(new Label { Text = featured?.Title ?? message ?? T("Home_Tagline"), FontSize = 56, FontAttributes = FontAttributes.Bold, TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 2 });
        var heroOverview = new Label
        {
            Text = featured?.Overview ?? T("Home_Intro"),
            FontSize = 16,
            TextColor = Color.FromArgb("#D1D1D6"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 4,
            MaximumWidthRequest = 560
        };
        copy.Add(heroOverview);
        var actions = new HorizontalStackLayout { Spacing = 12 };
        if (featured is not null)
        {
            var resume = continueWatching.FirstOrDefault(entry => entry.Id == featured.Id
                && string.Equals(entry.MediaType, featured.MediaType, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.LibraryPath));
            var playablePath = featured.LibraryPath ?? resume?.LibraryPath;
            if (!string.IsNullOrWhiteSpace(playablePath))
            {
                var play = CreateButton($"▶  {T("Home_Play")}", primary: true);
                play.BackgroundColor = Color.FromArgb("#F4F4F6");
                play.TextColor = Color.FromArgb("#090A0E");
                play.Clicked += async (_, _) => await OpenLibraryItemAsync(
                    new LibraryItem(featured.Title, playablePath, featured.Id, featured.MediaType ?? "movie", featured.SeasonNumber,
                        featured.EpisodeNumber, featured.Poster), resume?.CurrentTimeSeconds ?? featured.CurrentTimeSeconds);
                actions.Add(play);
            }
            var details = CreateButton($"ⓘ  {T("Home_MoreInfo")}", primary: false);
            details.BackgroundColor = Color.FromArgb("#55222228");
            focusTarget = details;
            details.Clicked += async (_, _) => await OpenDetailsAsync(featured);
            actions.Add(details);
        }
        copy.Add(actions);
        hero.Children.Add(copy);
        PageContent.Add(hero);

        var body = new VerticalStackLayout { Padding = new Thickness(64, 8, 64, 24), Spacing = 10 };

        if (catalog.Count == 0)
        {
            body.Add(new Label { Text = string.IsNullOrWhiteSpace(message) ? T("Home_Loading") : message, TextColor = Colors.White, FontSize = 22 });
        }
        else if (visible.Count == 0)
        {
            body.Add(new Label { Text = T("Home_Empty"), TextColor = Muted, FontSize = 20 });
        }
        else
        {
            body.Add(new Label { Text = T(activeSection == "Films" ? "Home_TrendingMovies" : activeSection == "Séries" ? "Home_TrendingSeries" : "Home_Trending"), TextColor = Colors.White, FontSize = IsWindowsDesktop ? 27 : 22, FontAttributes = FontAttributes.Bold });
            AddPosterRow(body, visible.Take(20).ToList());
        }
        PageContent.Add(body);
        if (activeSection == "Accueil")
        {
            AddOptionalCarousel(body, T("Home_ContinueWatching"), continueWatching, "À REPRENDRE");
            AddOptionalCarousel(body, T("Home_CacheTitle"), downloadedMedia, T("Home_CacheEyebrow"));
            AddOptionalCarousel(body, T("Home_WatchlistTitle"), watchlistMedia, T("Home_WatchlistEyebrow"));
        }
        AddOptionalCarousel(body, T("Home_IndexerPicks"), FilterMedia(popularInIndexers), null);
        foreach (var row in genreRows.Where(row => row.Media.Count > 0 && (activeSection == "Accueil" || row.AppliesTo(activeSection))))
            AddOptionalCarousel(body, row.Name, FilterMedia(row.Media), null);
        if (resetScroll && focusTarget is not null) MainThread.BeginInvokeOnMainThread(() => focusTarget.Focus());
    }

    private void AddOptionalCarousel(VerticalStackLayout parent, string title, List<MediaItem> items, string? eyebrow)
    {
        if (items.Count == 0) return;
        var section = new VerticalStackLayout { Spacing = 6 };
        section.Add(new Label { Text = title, TextColor = Colors.White, FontSize = IsWindowsDesktop ? 26 : 22, FontAttributes = FontAttributes.Bold });
        AddPosterRow(section, items.Take(20).ToList());
        parent.Add(section);
    }

    private void AddPosterRow(VerticalStackLayout parent, List<MediaItem> items)
    {
        var posterWidth = GetPosterCardWidth();
        var posterHeight = posterWidth * 1.5;
        var carousel = new CarouselRow(items);
        var row = new CollectionView
        {
            ItemsSource = items,
            ItemsLayout = new LinearItemsLayout(ItemsLayoutOrientation.Horizontal) { ItemSpacing = IsWindowsDesktop ? Math.Clamp(posterWidth * 0.08, 16, 24) : 16 },
            ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
            SelectionMode = SelectionMode.None,
            HeightRequest = IsWindowsDesktop ? posterHeight + 66 : 140,
            Margin = new Thickness(0, 4, 0, 4),
            ItemTemplate = new DataTemplate(() =>
            {
                var host = new ContentView();
                Border? posterBorder = null;
                Button? focusTarget = null;
                void SetPosterFocus(bool focused)
                {
                    if (focused)
                    {
                        if (host.BindingContext is MediaItem focusedItem)
                        {
                            focusedCarouselItem = new CarouselFocus(carousel, items.IndexOf(focusedItem));
                            requestedCarouselItem ??= focusedCarouselItem;
                        }
                        posterBorder!.Stroke = Colors.White;
                        posterBorder.StrokeThickness = 4;
                        posterBorder.Scale = 1.05;
                        posterBorder.ZIndex = 1;
                    }
                    else
                    {
                        posterBorder!.Stroke = Color.FromArgb("#454752");
                        posterBorder.StrokeThickness = 1;
                        posterBorder.Scale = 1;
                        posterBorder.ZIndex = 0;
                    }
                }
                // The native focus belongs to the Button inside the item template, not its ContentView host.
                host.BindingContextChanged += (_, _) =>
                {
                    if (host.BindingContext is MediaItem item)
                    {
                        host.Content = CreatePosterCard(item, posterWidth, posterHeight, out posterBorder, out focusTarget);
                        carousel.FocusTargets[items.IndexOf(item)] = focusTarget;
                        focusTarget.Focused += (_, _) => SetPosterFocus(true);
                        focusTarget.Unfocused += (_, _) => SetPosterFocus(false);
                    }
                };
                return host;
            })
        };
        carousel.View = row;
        carouselRows.Add(carousel);
        parent.Add(row);
    }

    private List<MediaItem> FilterMedia(List<MediaItem> items) => activeSection switch
    {
        "Films" => items.Where(item => item.MediaType == "movie").ToList(),
        "Séries" => items.Where(item => item.MediaType == "tv").ToList(),
        _ => items
    };

    private View CreatePosterCard(MediaItem item, double posterWidth, double posterHeight, out Border posterBorder, out Button hitTarget)
    {
        var imageGrid = new Grid { HeightRequest = posterHeight, WidthRequest = posterWidth, BackgroundColor = Color.FromArgb("#24252D") };
        if (!string.IsNullOrWhiteSpace(item.Poster)) imageGrid.Add(new Image { Source = item.Poster, Aspect = Aspect.AspectFill });
        else imageGrid.Add(new Label { Text = item.Title, TextColor = Colors.White, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center, Padding = 12 });
        hitTarget = new Button { Text = "", BackgroundColor = Colors.Transparent, BorderWidth = 0, CornerRadius = 0, WidthRequest = posterWidth, HeightRequest = posterHeight, Padding = 0 };
        hitTarget.Clicked += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(item.LibraryPath))
                await OpenLibraryItemAsync(new LibraryItem(item.Title, item.LibraryPath, item.Id, item.MediaType ?? "movie",
                    item.SeasonNumber, item.EpisodeNumber, item.Poster), item.CurrentTimeSeconds);
            else await OpenDetailsAsync(item);
        };
        imageGrid.Add(hitTarget);

        var card = new VerticalStackLayout { WidthRequest = posterWidth, Spacing = IsWindowsDesktop ? 5 : 2, Margin = new Thickness(3, 4, 3, 4) };
        posterBorder = new Border { Stroke = Color.FromArgb("#454752"), StrokeThickness = 1, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 }, Content = imageGrid };
        card.Add(posterBorder);
        card.Add(new Label { Text = item.Title, TextColor = Colors.White, FontSize = IsWindowsDesktop ? 16 : 13, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation, MaxLines = 1 });
        card.Add(new Label { Text = item.LibraryPath is not null ? item.CurrentTimeSeconds > 0 ? T("Library_Resume") : T("Library_InLibrary") : T(item.MediaType == "tv" ? "Category_Series" : "Category_Movie"), TextColor = Muted, FontSize = IsWindowsDesktop ? 13 : 11 });
        if (item.DurationSeconds > 0)
            card.Add(new ProgressBar { Progress = Math.Clamp(item.CurrentTimeSeconds / item.DurationSeconds, 0, 1), ProgressColor = Red, BackgroundColor = Panel, HeightRequest = 4 });
        return card;
    }

    private bool IsWindowsDesktop => DeviceInfo.Platform == DevicePlatform.WinUI;

    private double GetPosterCardWidth()
    {
        if (!IsWindowsDesktop) return 62;
        var viewportWidth = PageScroll.Width > 0 ? PageScroll.Width : 1280;
        return Math.Clamp(viewportWidth * 0.12, 145, 210);
    }

    public bool HandleVerticalDpad(int direction)
    {
        if (showingSettings || showingDetails || showingPlayer) return false;
        var current = requestedCarouselItem ?? focusedCarouselItem;
        if (current is null)
        {
            if (direction < 0 || carouselRows.Count == 0 || carouselRows[0].Items.Count == 0) return false;
            RequestCarouselFocus(carouselRows[0], 0);
            return true;
        }
        var currentRowIndex = carouselRows.IndexOf(current.Row);
        var targetRowIndex = currentRowIndex + direction;
        if (currentRowIndex < 0 || targetRowIndex < 0 || targetRowIndex >= carouselRows.Count) return false;

        var targetRow = carouselRows[targetRowIndex];
        var targetIndex = Math.Min(current.ItemIndex, targetRow.Items.Count - 1);
        RequestCarouselFocus(targetRow, targetIndex);
        return true;
    }

    public bool HandleHorizontalDpad(int direction)
    {
        if (showingSettings || showingDetails || showingPlayer) return false;
        var current = requestedCarouselItem ?? focusedCarouselItem;
        if (current is null) return false;
        var row = current.Row;
        var targetIndex = current.ItemIndex + direction;
        if (targetIndex < 0 || targetIndex >= row.Items.Count) return false;
        RequestCarouselFocus(row, targetIndex);
        return true;
    }

    private void RequestCarouselFocus(CarouselRow targetRow, int itemIndex)
    {
        var previousTarget = requestedCarouselItem ?? focusedCarouselItem;
        var scrollPage = previousTarget?.Row != targetRow;
        requestedCarouselItem = new CarouselFocus(targetRow, itemIndex);
        var nextRequest = new CancellationTokenSource();
        var previousRequest = carouselFocusCancellation;
        carouselFocusCancellation = nextRequest;
        previousRequest?.Cancel();
        _ = FocusCarouselItemAsync(targetRow, itemIndex, scrollPage, nextRequest);
    }

    private void CancelCarouselFocusRequest()
    {
        var request = carouselFocusCancellation;
        carouselFocusCancellation = null;
        requestedCarouselItem = null;
        request?.Cancel();
    }

    private async Task FocusCarouselItemAsync(CarouselRow targetRow, int itemIndex, bool scrollPage, CancellationTokenSource request)
    {
        var cancellationToken = request.Token;
        try
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scrollPage)
                    await PageScroll.ScrollToAsync(targetRow.View, ScrollToPosition.Center, false);

                cancellationToken.ThrowIfCancellationRequested();
                var item = targetRow.Items[itemIndex];
                targetRow.View.ScrollTo(item, position: ScrollToPosition.Center, animate: false);
                await Task.Delay(16, cancellationToken);

                cancellationToken.ThrowIfCancellationRequested();
                if (targetRow.FocusTargets.TryGetValue(itemIndex, out var button) && button.IsVisible && button.IsEnabled)
                    button.Focus();
#if ANDROID
                if (button?.IsFocused != true && targetRow.View.Handler?.PlatformView is AndroidX.RecyclerView.Widget.RecyclerView recycler)
                {
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var holder = recycler.FindViewHolderForAdapterPosition(itemIndex);
                        var focusView = holder?.ItemView;
                        if (focusView?.Focusable != true) focusView = FindFocusableView(focusView);
                        if (focusView?.RequestFocus() == true) break;
                        await Task.Delay(16, cancellationToken);
                    }
                }
#endif
            });
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(carouselFocusCancellation, request)) carouselFocusCancellation = null;
            request.Dispose();
        }
    }

#if ANDROID
    private static Android.Views.View? FindFocusableView(Android.Views.View? view)
    {
        if (view is null) return null;
        if (view is Android.Views.ViewGroup group)
        {
            for (var index = 0; index < group.ChildCount; index++)
            {
                var match = FindFocusableView(group.GetChildAt(index));
                if (match is not null) return match;
            }
        }
        return view.Focusable && view.Enabled && view.Visibility == Android.Views.ViewStates.Visible ? view : null;
    }
#endif

    private async Task OpenDetailsAsync(MediaItem item)
    {
        CancelCarouselFocusRequest();
        showingDetails = true;
        PageContent.Clear();
        _ = PageScroll.ScrollToAsync(0, 0, false);
        PageContent.Add(new Label { Text = T("Details_Loading"), TextColor = Muted, FontSize = 20, Margin = new Thickness(80, 60) });
        try
        {
            using var client = CreateClient();
            var type = item.MediaType == "tv" ? "tv" : "movie";
            var details = await client.GetFromJsonAsync<MediaDetails>($"api/catalog/{type}/{item.Id}", pageLifetime.Token);
            if (details is null) throw new HttpRequestException(T("Details_NoResponse"));
            var playableFile = type == "movie" ? await FindLibraryItemAsync(item, details.Title, client) : null;
            PageContent.Clear();
            var layout = new Grid { HeightRequest = PageScroll.Height > 0 ? PageScroll.Height : 460, BackgroundColor = Panel };
            if (!string.IsNullOrWhiteSpace(details.Backdrop)) layout.Add(new Image { Source = details.Backdrop, Aspect = Aspect.AspectFill, Opacity = 0.33 });
            layout.Add(new BoxView { BackgroundColor = Color.FromArgb("#CC090A0E") });
            var text = new VerticalStackLayout { Spacing = 10, Padding = new Thickness(80, 34), VerticalOptions = LayoutOptions.End, MaximumWidthRequest = 1100 };
            text.Add(Eyebrow(T(type == "tv" ? "Details_Series" : "Details_Movie")));
            text.Add(new Label { Text = details.Title, TextColor = Colors.White, FontSize = 46, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation });
            var facts = new List<string>();
            if (details.Year is not null) facts.Add(details.Year.Value.ToString());
            if (details.Rating > 0) facts.Add($"★ {details.Rating:0.0} / 10");
            if (details.RuntimeMinutes is not null) facts.Add($"{details.RuntimeMinutes} min");
            if (details.SeasonCount is not null) facts.Add($"{details.SeasonCount} saison(s)");
            if (facts.Count > 0) text.Add(new Label { Text = string.Join("     ·     ", facts), TextColor = Colors.White, FontSize = 16 });
            if (details.Genres is { Count: > 0 }) text.Add(new Label { Text = string.Join(" · ", details.Genres), TextColor = Muted, FontSize = 15 });
            Button? playButton = null;
            if (playableFile is not null)
            {
                playButton = CreateButton(T("Details_Play"), primary: true);
                playButton.HorizontalOptions = LayoutOptions.Start;
                playButton.Clicked += async (_, _) =>
                {
                    var resumePosition = continueWatching.FirstOrDefault(entry => string.Equals(entry.LibraryPath, playableFile.Path, StringComparison.Ordinal))?.CurrentTimeSeconds
                        ?? (string.Equals(item.LibraryPath, playableFile.Path, StringComparison.Ordinal) ? item.CurrentTimeSeconds : 0);
                    await OpenLibraryItemAsync(playableFile, resumePosition, item);
                };
                text.Add(playButton);
            }
            text.Add(new Label { Text = string.IsNullOrWhiteSpace(details.Overview) ? T("Details_NoOverview") : details.Overview, TextColor = Color.FromArgb("#D0D1D6"), FontSize = 18, LineHeight = 1.35, MaximumWidthRequest = 850, MaxLines = 4 });
            layout.Add(text);
            PageContent.Add(layout);
            _ = PageScroll.ScrollToAsync(0, 0, false);
            if (playButton is not null) MainThread.BeginInvokeOnMainThread(() => playButton.Focus());
            else FocusActiveNavigationTab();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            PageContent.Clear();
            PageContent.Add(ErrorPanel(string.Format(CultureInfo.CurrentCulture, T("Details_LoadingError"), ex.Message), async () => await OpenDetailsAsync(item)));
        }
    }

    private async Task<LibraryItem?> FindLibraryItemAsync(MediaItem item, string detailsTitle, HttpClient client)
    {
        if (!string.IsNullOrWhiteSpace(item.LibraryPath))
            return new LibraryItem(item.Title, item.LibraryPath, item.Id, item.MediaType ?? "movie", item.SeasonNumber, item.EpisodeNumber, item.Poster);

        var progress = continueWatching.FirstOrDefault(entry => entry.Id == item.Id
            && string.Equals(entry.MediaType, item.MediaType, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entry.LibraryPath));
        if (progress?.LibraryPath is not null)
            return new LibraryItem(progress.Title, progress.LibraryPath, item.Id, item.MediaType ?? "movie", item.SeasonNumber, item.EpisodeNumber, item.Poster);

        try
        {
            var library = await client.GetFromJsonAsync<LibraryResponse>("api/library", pageLifetime.Token);
            var titles = new[] { item.Title, detailsTitle }.Select(NormalizeMediaTitle).Where(title => title.Length > 0).ToHashSet(StringComparer.Ordinal);
            return library?.Items.FirstOrDefault(file => file.TmdbId > 0 && titles.Contains(NormalizeMediaTitle(file.Name)));
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static string NormalizeMediaTitle(string value)
    {
        var fileName = value.Replace('\\', '/').Split('/').Last();
        var title = Path.GetFileNameWithoutExtension(fileName);
        title = Regex.Replace(title, @"[\s._(-]*(?:\((?:19|20)\d{2}\)|(?:19|20)\d{2})$", string.Empty);
        var normalized = title.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    private async Task ShowLibraryAsync()
    {
        CancelCarouselFocusRequest();
        carouselRows.Clear();
        focusedCarouselItem = null;
        PageContent.Clear();
        var body = new VerticalStackLayout { Padding = new Thickness(76, 40), Spacing = 20 };
        body.Add(Eyebrow(T("Library_Collection")));
        body.Add(new Label { Text = T("Library_Title"), TextColor = Colors.White, FontSize = 38, FontAttributes = FontAttributes.Bold });
        body.Add(new Label { Text = T("Library_Description"), TextColor = Muted, FontSize = 17 });
        PageContent.Add(body);
        try
        {
            using var client = CreateClient();
            await Task.WhenAll(LoadContinueWatchingAsync(client), LoadLocalMediaAsync(client));
            var library = await client.GetFromJsonAsync<LibraryResponse>("api/library", pageLifetime.Token);
            var files = (library?.Items ?? []).Select(file =>
            {
                var progress = continueWatching.FirstOrDefault(item => item.Id == file.TmdbId
                    && string.Equals(item.MediaType, file.MediaType, StringComparison.OrdinalIgnoreCase)
                    && item.SeasonNumber == file.SeasonNumber && item.EpisodeNumber == file.EpisodeNumber)
                    ?? continueWatching.FirstOrDefault(item => string.Equals(item.LibraryPath, file.Path, StringComparison.Ordinal));
                return new MediaItem(file.TmdbId, file.Name, null, progress?.Poster ?? file.PosterUrl, null, file.MediaType, 0, null, null,
                    LibraryPath: file.Path, CurrentTimeSeconds: progress?.CurrentTimeSeconds ?? 0, DurationSeconds: progress?.DurationSeconds ?? 0,
                    SeasonNumber: file.SeasonNumber, EpisodeNumber: file.EpisodeNumber);
            }).ToList();

            if (files.Count == 0 && watchlistMedia.Count == 0)
            {
                body.Add(new Label { Text = T("Library_Empty"), TextColor = Muted, FontSize = 19, Margin = new Thickness(0, 14) });
                return;
            }

            if (files.Count > 0) AddOptionalCarousel(body, T("Library_MediaTitle"), files, T("Library_Collection"));
            AddOptionalCarousel(body, T("Library_WatchlistTitle"), watchlistMedia, T("Home_WatchlistEyebrow"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            body.Add(new Label { Text = string.Format(CultureInfo.CurrentCulture, T("Library_Unavailable"), ex.Message), TextColor = Color.FromArgb("#FF8C92"), FontSize = 16 });
        }
    }

    private Task OpenLibraryItemAsync(LibraryItem item, double resumePositionSeconds = 0, MediaItem? returnToDetails = null)
    {
        if (resumePositionSeconds <= 0)
            resumePositionSeconds = continueWatching.FirstOrDefault(entry => string.Equals(entry.LibraryPath, item.Path, StringComparison.Ordinal))?.CurrentTimeSeconds ?? 0;
        var sourceUrl = new Uri(CreateClient().BaseAddress!, $"api/library/play/{Uri.EscapeDataString(item.Path)}");
        showingPlayer = true;
        activeLibraryItem = item;
        playerReturnDetails = returnToDetails;
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
            // Windows' native seek controls can raise SeekRequested off the UI thread and crash WinRT.
            ShouldShowPlaybackControls = !OperatingSystem.IsWindows(),
            Aspect = Aspect.AspectFit,
            BackgroundColor = Colors.Black,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        Slider? playbackSlider = null;
        Label? playbackPosition = null;
        var updatingPlaybackSlider = false;
        player.MediaFailed += async (_, _) => await DisplayAlertAsync(T("Playback_ErrorTitle"), T("Playback_ErrorMessage"), "OK");
        player.MediaOpened += async (_, _) =>
        {
            if (playbackSlider is not null)
            {
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    playbackSlider.Maximum = Math.Max(1, player.Duration.TotalSeconds);
                    playbackPosition!.Text = $"{FormatPlaybackTime(player.Position)} / {FormatPlaybackTime(player.Duration)}";
                });
            }
            if (resumePositionSeconds > 0)
                await MainThread.InvokeOnMainThreadAsync(() => player.SeekTo(TimeSpan.FromSeconds(resumePositionSeconds)));
        };
        player.PositionChanged += (_, args) =>
        {
            if (playbackSlider is not null && !updatingPlaybackSlider)
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (playbackSlider is null || updatingPlaybackSlider) return;
                    updatingPlaybackSlider = true;
                    playbackSlider.Value = Math.Clamp(args.Position.TotalSeconds, playbackSlider.Minimum, playbackSlider.Maximum);
                    playbackPosition!.Text = $"{FormatPlaybackTime(args.Position)} / {FormatPlaybackTime(player.Duration)}";
                    updatingPlaybackSlider = false;
                });
            }
            if (args.Position > TimeSpan.Zero && !progressSavePending && DateTimeOffset.UtcNow - lastProgressSavedAt >= TimeSpan.FromSeconds(10))
                _ = SaveCurrentPlaybackProgressAsync(force: false);
        };
        activePlayer = player;
        FullScreenPlayerHost.Children.Clear();
        FullScreenPlayerHost.Children.Add(player);
        if (OperatingSystem.IsWindows())
        {
            var paused = false;
            var overlay = new VerticalStackLayout
            {
                Spacing = 8,
                Padding = new Thickness(36, 22, 36, 28),
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.End,
                Background = new LinearGradientBrush(new GradientStopCollection
                {
                    new(Colors.Transparent, 0),
                    new(Color.FromArgb("#E6090A0E"), 0.35f),
                    new(Color.FromArgb("#F5090A0E"), 1)
                }, new Point(0, 0), new Point(0, 1)),
                ZIndex = 2
            };
            playbackSlider = new Slider { Minimum = 0, Maximum = 1, Value = 0, MinimumTrackColor = Red, MaximumTrackColor = Color.FromArgb("#77FFFFFF"), ThumbColor = Colors.White, HeightRequest = 28 };
            playbackPosition = new Label { Text = "00:00 / 00:00", TextColor = Color.FromArgb("#E7E7EA"), FontSize = 13, VerticalTextAlignment = TextAlignment.Center };
            var seekDebounce = new CancellationTokenSource();
            playbackSlider.ValueChanged += (_, args) =>
            {
                if (updatingPlaybackSlider) return;
                seekDebounce.Cancel();
                seekDebounce.Dispose();
                seekDebounce = new CancellationTokenSource();
                var token = seekDebounce.Token;
                _ = SeekSliderAfterDelayAsync(player, args.NewValue, token);
                ShowPlayerControls(overlay, player);
            };

            var buttonRow = new HorizontalStackLayout { Spacing = 10, VerticalOptions = LayoutOptions.Center };
            var backButton = CreatePlayerControlButton(returnToDetails is null ? T("Playback_BackToLibrary") : T("Playback_BackToDetails"));
            backButton.Clicked += async (_, _) => await ReturnFromPlayerAsync();
            var rewindButton = CreatePlayerControlButton(T("Playback_Rewind10"));
            rewindButton.Clicked += async (_, _) => await SeekPlayerByAsync(player, -10);
            var pauseButton = CreatePlayerControlButton(T("Playback_Pause"));
            pauseButton.Clicked += (_, _) =>
            {
                paused = !paused;
                if (paused)
                {
                    player.Pause();
                    pauseButton.Text = T("Playback_Resume");
                }
                else
                {
                    player.Play();
                    pauseButton.Text = T("Playback_Pause");
                }
                ShowPlayerControls(overlay, player);
            };
            var forwardButton = CreatePlayerControlButton(T("Playback_Forward10"));
            forwardButton.Clicked += async (_, _) => await SeekPlayerByAsync(player, 10);
            buttonRow.Children.Add(backButton);
            buttonRow.Children.Add(rewindButton);
            buttonRow.Children.Add(pauseButton);
            buttonRow.Children.Add(forwardButton);
            buttonRow.Children.Add(playbackPosition);
            overlay.Children.Add(playbackSlider);
            overlay.Children.Add(buttonRow);
            FullScreenPlayerHost.Children.Add(overlay);

            var pointer = new PointerGestureRecognizer();
            pointer.PointerMoved += (_, _) => ShowPlayerControls(overlay, player);
            overlay.GestureRecognizers.Add(pointer);
            player.GestureRecognizers.Add(pointer);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => ShowPlayerControls(overlay, player);
            player.GestureRecognizers.Add(tap);
            ShowPlayerControls(overlay, player);
        }
        else
        {
            var backButton = CreatePlayerControlButton(returnToDetails is null ? T("Playback_BackToLibrary") : T("Playback_BackToDetails"));
            backButton.HorizontalOptions = LayoutOptions.Start;
            backButton.VerticalOptions = LayoutOptions.Start;
            backButton.Margin = new Thickness(28, 24);
            backButton.Clicked += async (_, _) => await ReturnFromPlayerAsync();
            FullScreenPlayerHost.Children.Add(backButton);
        }
        return Task.CompletedTask;
    }

    private static Button CreatePlayerControlButton(string text) => new()
    {
        Text = text,
        FontSize = 15,
        FontAttributes = FontAttributes.Bold,
        TextColor = Colors.White,
        BackgroundColor = Color.FromArgb("#CC202127"),
        BorderColor = Color.FromArgb("#66FFFFFF"),
        BorderWidth = 1,
        CornerRadius = 8,
        Padding = new Thickness(16, 10)
    };

    private static string T(string key) => SharedText.Get(key);

    private static string FormatPlaybackTime(TimeSpan time) => time.TotalHours >= 1
        ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
        : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private async Task SeekSliderAfterDelayAsync(MediaElement player, double seconds, CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (activePlayer == player)
                await MainThread.InvokeOnMainThreadAsync(() => player.SeekTo(TimeSpan.FromSeconds(seconds)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SeekPlayerByAsync(MediaElement player, double seconds)
    {
        if (player.Duration <= TimeSpan.Zero) return;
        var position = TimeSpan.FromSeconds(Math.Clamp((player.Position + TimeSpan.FromSeconds(seconds)).TotalSeconds, 0, player.Duration.TotalSeconds));
        await MainThread.InvokeOnMainThreadAsync(() => player.SeekTo(position));
    }

    private void ShowPlayerControls(VisualElement controls, MediaElement player)
    {
        playerControlsFadeCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        playerControlsFadeCancellation = cancellation;
        controls.InputTransparent = false;
        controls.CancelAnimations();
        _ = controls.FadeToAsync(1, 180);
        _ = HidePlayerControlsAfterDelayAsync(controls, player, cancellation.Token);
    }

    private async Task HidePlayerControlsAfterDelayAsync(VisualElement controls, MediaElement player, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), token);
            if (activePlayer != player) return;
            await controls.FadeToAsync(0, 450);
            controls.InputTransparent = true;
        }
        catch (OperationCanceledException) { }
    }

    private async Task ReturnFromPlayerAsync()
    {
        playerControlsFadeCancellation?.Cancel();
        playerControlsFadeCancellation = null;
        await SaveCurrentPlaybackProgressAsync(force: true);
        activePlayer?.Stop();
        if (activePlayer is not null) activePlayer.Source = null;
        activePlayer = null;
        activeLibraryItem = null;
        var returnToDetails = playerReturnDetails;
        playerReturnDetails = null;
        FullScreenPlayerHost.Children.Clear();
        FullScreenPlayerHost.IsVisible = false;
        HeaderBar.IsVisible = true;
        PageScroll.IsVisible = true;
        showingPlayer = false;
        BuildNavigation();
        if (returnToDetails is not null)
            await OpenDetailsAsync(returnToDetails);
        else
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
            var context = playerReturnDetails;
            var tmdbId = context?.Id is > 0 ? context.Id : item.TmdbId;
            var mediaType = context?.MediaType is "movie" or "tv" ? context.MediaType : item.MediaType;
            if (tmdbId <= 0 || mediaType is not ("movie" or "tv")) return;
            var title = context?.Title ?? item.Name;
            var poster = context?.Poster ?? item.PosterUrl;
            var seasonNumber = context?.SeasonNumber ?? item.SeasonNumber;
            var episodeNumber = context?.EpisodeNumber ?? item.EpisodeNumber;
            var request = new PlaybackProgressRequest(tmdbId, mediaType, title, player.Position.TotalSeconds,
                player.Duration.TotalSeconds, poster, seasonNumber, episodeNumber, item.Path);
            lastProgressSavedAt = DateTimeOffset.UtcNow;
            using var response = await client.PostAsJsonAsync("api/watch-progress", request, pageLifetime.Token);
            if (response.IsSuccessStatusCode)
            {
                var current = player.Position.TotalSeconds;
                var duration = player.Duration.TotalSeconds;
                continueWatching = continueWatching.Where(entry => !(entry.Id == tmdbId
                    && string.Equals(entry.MediaType, mediaType, StringComparison.OrdinalIgnoreCase)
                    && entry.SeasonNumber == seasonNumber && entry.EpisodeNumber == episodeNumber)).ToList();
                if (current / duration < .95 && duration - current > 30)
                    continueWatching.Insert(0, new MediaItem(tmdbId, title, null, poster, null, mediaType, 0, null, null,
                        LibraryPath: item.Path, CurrentTimeSeconds: current, DurationSeconds: duration,
                        SeasonNumber: seasonNumber, EpisodeNumber: episodeNumber));
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
            BuildHome(response?.Configured == false ? T("Server_CatalogNotConfigured") : null);
            _ = LoadHomeCarouselsAsync(catalogConfigured);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            serverReachable = false;
            catalog = [];
            if (!showingSettings)
            {
                BuildHome(discoverOnFailure ? T("Server_NotFoundSearching") : T("Server_CannotReach"));
                var settings = CreateButton(T("Settings_ConfigureServer"), primary: true);
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
            MainThread.BeginInvokeOnMainThread(() => BuildHome(T("Server_NoServers")));
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
                        if (state is not null) found.Add(new DiscoveredServer(address.ToString(), port, url, $"Bobine · {address}:{port}"));
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
                LibraryPath: item.LibraryPath, CurrentTimeSeconds: item.CurrentTimeSeconds, DurationSeconds: item.DurationSeconds,
                SeasonNumber: item.SeasonNumber, EpisodeNumber: item.EpisodeNumber)).ToList();
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
            return new GenreRow(T(genre.ResourceKey), genre.MovieId.HasValue, genre.TvId.HasValue, media);
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

    private HttpClient CreateClient()
    {
        var client = new HttpClient { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(12) };
        var language = CultureInfo.CurrentUICulture.Name;
        if (!string.IsNullOrWhiteSpace(language)) client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(language);
        return client;
    }

    private static View ErrorPanel(string message, Func<Task> retry)
    {
        var layout = new VerticalStackLayout { Padding = new Thickness(80, 60), Spacing = 18 };
        layout.Add(new Label { Text = message, TextColor = Color.FromArgb("#FF8C92"), FontSize = 18 });
        var button = CreateButton(T("Common_Retry"), primary: true);
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
            error = string.Format(CultureInfo.CurrentCulture, T("Server_InvalidAddress"), DefaultServerUrl);
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
        CancelCarouselFocusRequest();
        pageLifetime.Cancel();
    }

    private void OnSettingsClicked(object? sender, EventArgs e) => ShowSettings();

    private void ShowSettings(List<DiscoveredServer>? initialResults = null)
    {
        CancelCarouselFocusRequest();
        showingSettings = true;
        showingDetails = false;
        BuildNavigation();
        PageContent.Clear();
        var panel = new VerticalStackLayout { Padding = new Thickness(80, 66), Spacing = 18, MaximumWidthRequest = 900, HorizontalOptions = LayoutOptions.Start };
        panel.Add(Eyebrow("CONNEXION"));
        panel.Add(new Label { Text = T("Settings_ServerAddress"), TextColor = Colors.White, FontSize = 38, FontAttributes = FontAttributes.Bold });
        panel.Add(new Label { Text = T("Settings_ServerDescription"), TextColor = Muted, FontSize = 18 });
        var address = new Entry { Text = serverUrl, Placeholder = "http://brique2:6050", TextColor = Colors.White, PlaceholderColor = Muted, BackgroundColor = Panel, FontSize = 20, ReturnType = ReturnType.Go };
        var status = new Label { TextColor = Color.FromArgb("#FF8C92"), FontSize = 15, IsVisible = false };
        var results = new VerticalStackLayout { Spacing = 8 };
        var discover = CreateButton(T("Settings_DiscoverServers"), primary: false);
        discover.Clicked += async (_, _) =>
        {
            discover.IsEnabled = false;
            status.TextColor = Muted;
            status.Text = T("Settings_SearchingNetwork");
            status.IsVisible = true;
            results.Children.Clear();
            var found = await DiscoverServersAsync();
            discover.IsEnabled = true;
            if (found.Count == 0)
            {
                status.TextColor = Color.FromArgb("#FF8C92");
                status.Text = T("Settings_NoServer");
                return;
            }
            status.TextColor = Muted;
            status.Text = string.Format(CultureInfo.CurrentCulture, T("Settings_ServersFound"), found.Count);
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
            status.Text = string.Format(CultureInfo.CurrentCulture, T("Settings_ServersFound"), initialResults.Count);
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
        status.Text = T("Settings_Connecting");
        status.IsVisible = true;
        catalog = [];
        await LoadCatalogAsync(discoverOnFailure: false);
        if (!serverReachable)
        {
            status.TextColor = Color.FromArgb("#FF8C92");
            status.Text = T("Settings_ServerUnavailable");
            return;
        }

        showingSettings = false;
        activeSection = "Accueil";
        BuildNavigation();
        BuildHome(catalogConfigured ? null : T("Server_CatalogNotConfigured"));
        _ = LoadHomeCarouselsAsync(catalogConfigured);
    }

    private sealed record CatalogResponse(bool Configured, List<MediaItem> Results);
    private sealed record MediaItem(int Id, string Title, string? Overview, string? Poster, string? Backdrop, string? MediaType, double Rating, string? OriginalLanguage, int? Year, double Popularity = 0, string? LibraryPath = null, double CurrentTimeSeconds = 0, double DurationSeconds = 0, int? SeasonNumber = null, int? EpisodeNumber = null);
    private sealed record MediaDetails(string Title, string? Overview, string? Poster, string? Backdrop, string? MediaType, double Rating, int? Year, List<string>? Genres, int? RuntimeMinutes, int? SeasonCount);
    private sealed record LibraryResponse(List<LibraryItem> Items);
    private sealed record LibraryItem(string Name, string Path, int TmdbId, string MediaType, int? SeasonNumber = null, int? EpisodeNumber = null, string? PosterUrl = null);
    private sealed record CarouselFocus(CarouselRow Row, int ItemIndex);
    private sealed class CarouselRow(List<MediaItem> items)
    {
        public CollectionView View { get; set; } = null!;
        public List<MediaItem> Items { get; } = items;
        public Dictionary<int, Button> FocusTargets { get; } = [];
    }
    private sealed record DiscoveredServer(string Address, int Port, string Url, string DisplayName);
    private sealed record WatchProgressEntry(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds, double DurationSeconds, double ProgressPercent, DateTimeOffset UpdatedAt, string? PosterUrl = null, int? SeasonNumber = null, int? EpisodeNumber = null, bool IsCompleted = false, string? LibraryPath = null);
    private sealed record PlaybackProgressRequest(int TmdbId, string MediaType, string Title, double CurrentTimeSeconds, double DurationSeconds, string? PosterUrl = null, int? SeasonNumber = null, int? EpisodeNumber = null, string? LibraryPath = null);
    private sealed record LocalMediaStateResponse(List<LocalMediaEntry>? Downloads, List<LocalMediaEntry>? Watchlist);
    private sealed record LocalMediaEntry(int TmdbId, string MediaType, string Title, string? OriginalLanguage = null, int? Year = null, bool Completed = false, DateTimeOffset? LastViewedAt = null);
    private sealed record IndexerPopularResponse(List<IndexerPopularItem>? Results);
    private sealed record IndexerPopularItem(int Id, string Title, string? Overview, string? Poster, string? Backdrop, string? MediaType, double Rating, string? OriginalLanguage, int? Year, double TmdbPopularity);
    private sealed record GenreDefinition(string ResourceKey, int? MovieId, int? TvId);
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
        new("Genre_ActionAdventure", 28, 10759), new("Genre_Animation", 16, 16), new("Genre_Comedy", 35, 35),
        new("Genre_Crime", 80, 80), new("Genre_Documentary", 99, 99), new("Genre_Drama", 18, 18),
        new("Genre_Family", 10751, 10751), new("Genre_ScienceFantasy", 878, 10765),
        new("Genre_History", 36, null), new("Genre_Horror", 27, null), new("Genre_Mystery", 9648, 9648),
        new("Genre_Romance", 10749, null), new("Genre_Thriller", 53, null), new("Genre_WarPolitics", 10752, 10768),
        new("Genre_Western", 37, 37), new("Genre_Youth", null, 10762), new("Genre_Reality", null, 10764),
        new("Genre_Soap", null, 10766), new("Genre_TalkShow", null, 10767)
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
