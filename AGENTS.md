# Contexte du dépôt pour les agents

## Projet

MediaPlayer est une application personnelle sous licence GPL-3.0-only. Le dépôt contient un serveur MediaPlayer web en .NET 10, un client .NET MAUI pour Android TV/Google TV et des tests. Lire `README.md` pour les détails fonctionnels destinés aux utilisateurs et `src/MediaPlayer.AndroidTv/README.md` pour la configuration et le build du client TV.

## Architecture

- `src/MediaPlayer.Web` : serveur ASP.NET Core avec composants Blazor interactifs, endpoints API, intégrations TMDB et OpenSubtitles, recherche Torznab/Cardigann, bibliothèque et téléchargements BitTorrent via MonoTorrent.
- `src/MediaPlayer.Web/Components` : pages, layouts et composants Razor.
- `src/MediaPlayer.Web/Services` : logique métier et stockage local, notamment identifiants, indexeurs, bibliothèque et téléchargements.
- `src/MediaPlayer.Web/IndexerDefinitions` : définitions Cardigann intégrées au dépôt. Les définitions synchronisées et les données utilisateur sont conservées dans `App_Data` et ignorées par Git.
- `src/MediaPlayer.AndroidTv` : client .NET MAUI natif avec catalogue, fiches et bibliothèque, découverte automatique des serveurs sur le sous-réseau IPv4 quand l’adresse enregistrée échoue, configuration persistante de l’URL du serveur, navigation à la télécommande et lecture intégrée via MediaElement. Cibles Android TV et Windows (Windows s’ajoute sur un hôte Windows sans RID explicite). Le paquet Android est `fr.mediaplayer.androidtv`.
- `tests/MediaPlayer.Tests` : tests .NET/xUnit.
- `tests/MediaPlayer.Playwright` : tests de navigateur Playwright en JavaScript.
- `MediaPlayer.slnx` : solution .NET (Web, Android TV et tests .NET).

## Commandes de développement

Lancer le serveur localement :

```powershell
dotnet run --project src/MediaPlayer.Web --urls http://localhost:8080
```

Pour rendre le serveur accessible à Google TV sur le réseau local, écouter sur toutes les interfaces. Le client TV préremplit `http://brique2:6050` ; vérifier que ce nom résout vers le PC, ou saisir son adresse réseau à la place :

```powershell
dotnet run --project src/MediaPlayer.Web --no-launch-profile --urls http://0.0.0.0:6050
```

Construire et installer un APK Debug sur un appareil connecté avec ADB :

```powershell
dotnet build src/MediaPlayer.AndroidTv/MediaPlayer.AndroidTv.csproj -f net10.0-android -c Debug
adb install -r src/MediaPlayer.AndroidTv/bin/Debug/net10.0-android/fr.mediaplayer.androidtv-Signed.apk
```

Publier un APK Release :

```powershell
dotnet publish src/MediaPlayer.AndroidTv/MediaPlayer.AndroidTv.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Exécuter les tests .NET :

```powershell
dotnet test MediaPlayer.slnx
```

Les tests Playwright ont leurs propres instructions dans `tests/MediaPlayer.Playwright/README.md`.

## Configuration et données locales

- TMDB : variable `TMDB_READ_ACCESS_TOKEN` ou secret .NET `TMDB:ReadAccessToken`.
- OpenSubtitles : variable `OPENSUBTITLES_API_KEY` ou secret .NET `OpenSubtitles:ApiKey`.
- Médias locaux : variable `MEDIA_ROOT`.
- Les clés API et paramètres locaux sont stockés sous `App_Data`; ne pas ajouter de secrets ni de données utilisateur au dépôt.
- Docker Compose expose le serveur sur le port 8080 et persiste les réglages dans `./config/media-player`.

## Repères pour les changements

- Garder les changements cohérents avec l’architecture existante et les conventions des fichiers concernés ; privilégier les services existants à une nouvelle implémentation parallèle.
- Les endpoints, services et pages sont principalement assemblés dans `src/MediaPlayer.Web/Program.cs`; vérifier les usages et la configuration associée avant de modifier le comportement serveur.
- Le client MAUI consomme les endpoints JSON du serveur sans WebView. Il faut démarrer le serveur sur une adresse joignable depuis le téléviseur, et non utiliser `localhost` dans l’application TV. L’API de lecture expose les fichiers de bibliothèque comme flux HTTP ; le lecteur MediaElement utilise les décodeurs du système.
- Ne pas inclure de données générées (`bin`, `obj`, `App_Data`, résultats de tests), secrets ou configuration utilisateur dans les changements.
- Ne pas lancer ni ajouter de tests sans demande explicite ; si une vérification est demandée ou nécessaire, rapporter précisément ce qui a été exécuté.
