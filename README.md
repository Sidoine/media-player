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
| TMDB read access token | `TMDB_READ_ACCESS_TOKEN` | `TMDB:ReadAccessToken` | Catalog metadata and artwork |
| OpenSubtitles API key | `OPENSUBTITLES_API_KEY` | `OpenSubtitles:ApiKey` | Subtitle integration |
| Local media directory | `MEDIA_ROOT` | — | Root directory for local media |

Torznab indexer URLs and API keys are configured in the web interface. Credentials entered there are stored encrypted in `App_Data/api-credentials.json`; environment variables and .NET user secrets take precedence. Local settings, credentials, cache, and other user data belong in `App_Data` and are excluded from Git.

The server caches Cardigann v11 indexer definitions in `App_Data/indexer-definitions`. Searches support definitions using JSON responses and their default settings. HTML/XML responses, account-based indexers, and custom per-indexer settings are not supported.

Downloaded media is stored in `App_Data/media-cache`, with a default cache limit of 500 GiB. Completed media appears in the library; when the limit is approached, older viewed media is removed first. The limit can be changed with `Media:CacheMaxBytes`.

## Docker Compose

Docker Compose publishes the server on port 8080, mounts `./data/media` read-only at `/media`, and persists application data in `./config/media-player`:

```powershell
docker compose up --build
```

Set `TMDB_READ_ACCESS_TOKEN` in the environment before starting Compose to configure TMDB.

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
