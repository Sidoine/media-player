# MediaPlayer

MediaPlayer is a self-hosted media catalog and library application. The repository contains an ASP.NET Core web server, an Android TV client, and automated tests. It is licensed under GNU GPL v3.0 only; see [LICENSE](LICENSE).

## Components

- `src/MediaPlayer.Web`: ASP.NET Core 10 server with an interactive Blazor interface, TMDB catalog metadata, local media library, Torznab and Cardigann indexer search, and BitTorrent downloads managed by MonoTorrent.
- `src/MediaPlayer.AndroidTv`: native .NET MAUI client for Android TV and Google TV. It provides catalog pages, media details, library browsing, and playback through MediaElement. On Windows hosts it can also target Windows.
- `src/MediaPlayer.Localization`: shared localization resources.
- `tests/MediaPlayer.Tests`: .NET and xUnit tests.
- `tests/MediaPlayer.Playwright`: browser tests. See its [README](tests/MediaPlayer.Playwright/README.md) for instructions.

## Requirements

- .NET 10 SDK for the web server and .NET tests.
- For the Android TV client: .NET MAUI and Android workloads, Android SDK, and JDK 21.

## Run the web server

```powershell
dotnet run --project src/MediaPlayer.Web --urls http://localhost:8080
```

In Development, the Blazor home page is available at `/`.

To make the server reachable by a TV on the local network, listen on all interfaces:

```powershell
dotnet run --project src/MediaPlayer.Web --no-launch-profile --urls http://0.0.0.0:6050
```

The client initially uses `http://brique2:6050`. Change the server address in the client if that name does not resolve to the server on your network. If its saved address is unavailable, the client can scan the local IPv4 subnet for MediaPlayer servers.

## Configuration

Set the following environment variables or use .NET user secrets:

| Setting | Environment variable | .NET user secret | Purpose |
| --- | --- | --- | --- |
| TMDB read access token | `TMDB__ReadAccessToken` | `TMDB:ReadAccessToken` | Catalog metadata and artwork |
| OpenSubtitles API key | `OpenSubtitles__ApiKey` | `OpenSubtitles:ApiKey` | Subtitle integration |
| Local media and download directory | `Media__Root` | — | Root directory for the library and downloaded media |

Torznab indexer URLs and API keys are configured in the web interface. Credentials entered there are stored encrypted in `App_Data/api-credentials.json`; environment variables and .NET user secrets take precedence. Local settings, credentials, cache, and other user data belong in `App_Data` and are excluded from Git.

The server caches Cardigann v11 indexer definitions in `App_Data/indexer-definitions`. Searches support definitions using JSON responses and their default settings. HTML/XML responses, account-based indexers, and custom per-indexer settings are not supported.

The library and downloaded media share the directory configured by `Media__Root`. Docker Compose uses `/media`; other deployments default to `App_Data/media-cache`. The default cache limit is 500 GiB and can be changed with `Media:CacheMaxBytes`. When the limit is approached, older viewed media is removed first.

## Docker Compose

Docker Compose publishes the server on port 8080, mounts `./data/media` at `/media` for the library and downloads, and persists application data in `./config/media-player`:

```powershell
docker compose up --build
```

Set `TMDB__ReadAccessToken` or `OpenSubtitles__ApiKey` in the environment before starting Compose to configure external services.

## macOS standalone app

The GitHub Actions workflow builds self-contained `.app` packages for Apple Silicon and Intel Macs. It can be run manually from the Actions tab or creates a GitHub release when a `v*` tag is pushed. A Mac and a .NET installation are not required to build the packages locally because the workflow uses macOS runners.

On the first launch, Bobine asks the user to choose a media folder, starts the server, and opens the setup page in the browser. The optional TMDB and OpenSubtitles keys can be entered there or later in Configuration. Application data and encrypted credentials are stored in `~/Library/Application Support/Bobine`; media stays in the chosen folder. The server listens on port 6050 on the local network so a TV can connect to the Mac's local IP address.

To install, download the ZIP matching the Mac, open it, and move `Bobine.app` to Applications. If macOS blocks the first launch, Control-click the app, choose **Open**, then confirm **Open** in the dialog.

The current packages are not signed or notarized by Apple. macOS may ask the user to approve opening the downloaded app in Privacy & Security. Distributing without that warning requires an Apple Developer signing identity and notarization credentials in GitHub Actions secrets. Test the downloads on both Apple Silicon and Intel hardware before recommending them to users.

## Android TV client

Build a Release APK with the Android workload installed:

```powershell
dotnet publish src/MediaPlayer.AndroidTv/MediaPlayer.AndroidTv.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

The APK is written under `src/MediaPlayer.AndroidTv/bin/Release/net10.0-android/`. For server setup, installation, Windows execution, and playback details, see the [Android TV README](src/MediaPlayer.AndroidTv/README.md).

## Tests

Run the .NET test suite with:

```powershell
dotnet test MediaPlayer.slnx
```

Playwright tests have separate setup instructions in `tests/MediaPlayer.Playwright/README.md`.

## Rights and sources

The application does not independently verify media licenses. Users are responsible for ensuring that each configured source is authorized to provide the media they access. TMDB supplies metadata and artwork, not distribution rights; deployments must follow TMDB's attribution terms.
