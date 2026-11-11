# Écran

Application ASP.NET Core .NET 10 avec interface Blazor interactive, catalogue TMDB et bibliothèque sur /bibliotheque et réglages Torznab sur /configuration, recherche Torznab et lecture locale.

## Structure

- `src/MediaPlayer.Web` : serveur HTTP et interface web.
- `tests/MediaPlayer.Tests` : tests d’intégration xUnit.
- `MediaPlayer.slnx` : solution .NET.

## Développement

```powershell
dotnet run --project src/MediaPlayer.Web --urls http://localhost:8080
dotnet test MediaPlayer.slnx
```

Pour lancer le serveur avec `dotnet watch` et permettre aux redémarrages demandés depuis l’interface de se faire sans confirmation interactive :

```powershell
$env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = "1"
dotnet watch --project src/MediaPlayer.Web
```

En environnement `Development`, l’interface d’accueil Blazor est disponible à la racine (`/`).

Configuration par variables d’environnement : `TMDB_READ_ACCESS_TOKEN` pour TMDB, `OPENSUBTITLES_API_KEY` pour OpenSubtitles et `MEDIA_ROOT` pour le dossier de médias locaux. Avec les secrets .NET, utilisez `TMDB:ReadAccessToken` et `OpenSubtitles:ApiKey`. Docker Compose expose le serveur sur le port 8080 et conserve les réglages locaux dans `./config/media-player`.

Dans l’interface, ajoutez l’URL API Torznab et la clé API de votre indexeur (par exemple une source configurée dans votre Prowlarr), confirmez la déclaration de droits, puis lancez une recherche. Le serveur envoie une requête Torznab `t=search` avec `q` et `apikey`, et affiche les résultats reçus du flux RSS/XML.

Les clés API configurées dans l’interface sont chiffrées localement dans `App_Data/api-credentials.json`; les secrets .NET et les variables d’environnement sont prioritaires. Tant que TMDB ou OpenSubtitles n’est pas configuré, les autres pages redirigent vers Configuration. Le cache, les clés et les réglages locaux sont exclus du dépôt Git.

La déclaration de droits est une attestation de l’utilisateur, pas une vérification indépendante des licences par l’application. Chaque source ajoutée doit être autorisée à proposer les médias concernés. TMDB fournit des métadonnées et visuels, pas les droits de diffusion; respecter ses conditions d’attribution lors de la mise en production.

## Définitions d’indexeurs

Le serveur met en cache les définitions Cardigann v11 de Prowlarr/Indexers dans `App_Data/indexer-definitions`. Il les synchronise au démarrage uniquement si le cache date d’au moins sept jours, puis vérifie à nouveau chaque semaine pendant son exécution. La fréquence peut être réglée avec `IndexerDefinitions:RefreshIntervalDays`. Le dossier est ignoré par Git; les définitions ne sont pas intégrées au build ni au dépôt. Si GitHub est indisponible, une copie locale déjà présente est conservée. La page de configuration permet toujours de lancer une synchronisation manuelle.

La page `/configuration` permet de parcourir ces définitions et d’enregistrer localement les indexeurs choisis dans `App_Data/selected-indexers.json`. Les recherches exécutent les définitions Cardigann v11 qui utilisent une réponse JSON et leurs valeurs `settings.default`. Les réponses HTML/XML, les connexions aux comptes et les réglages personnalisés par indexeur restent à prendre en charge.

Un clic sur une affiche vérifie d’abord le cache, puis recherche le titre dans les sources configurées. Les correspondances sont classées en privilégiant la VO et le 1080p, puis le nombre total de seeders et leechers; si plusieurs restent disponibles, l’utilisateur choisit avant le lancement. La page Configuration permet de modifier les trackers ajoutés aux magnets, et Ma bibliothèque affiche les transferts actifs avec une action d’annulation. Les téléchargements BitTorrent sont gérés par MonoTorrent et enregistrés dans `App_Data/media-cache` (limite par défaut de 500 Gio, réglable avec `Media:CacheMaxBytes`). Seuls les médias terminés sont affichés dans la bibliothèque. À l’approche de la limite, les médias vus les plus anciens sont supprimés en premier.

Le projet est sous licence GNU GPL v3.0 uniquement (voir `LICENSE`).


