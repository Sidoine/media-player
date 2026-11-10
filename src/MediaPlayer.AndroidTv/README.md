# MediaPlayer TV

Client Android TV pour Google TV. L’application ouvre l’interface MediaPlayer du serveur dans une WebView plein écran. Elle ajoute un lanceur visible depuis l’accueil Google TV, un écran de configuration de l’adresse du serveur et la navigation à la télécommande.

## Démarrer le serveur sur le réseau local

Depuis le dépôt, lancer le serveur en écoutant sur toutes les interfaces réseau :

```powershell
dotnet run --project src/MediaPlayer.Web --no-launch-profile --urls http://0.0.0.0:5102
```

Autoriser MediaPlayer dans le pare-feu Windows pour le réseau privé si nécessaire. Le PC et le boîtier Google TV doivent être sur le même réseau. Au premier lancement de l’application, saisir l’adresse IP locale du PC, par exemple `http://192.168.1.25:5102`. L’adresse est conservée sur le boîtier.

## Construire l’APK

Avec la charge de travail .NET Android, le SDK Android et JDK 21 installés :

```powershell
dotnet publish src/MediaPlayer.AndroidTv/MediaPlayer.AndroidTv.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Le fichier APK signé de développement se trouve dans `src/MediaPlayer.AndroidTv/bin/Release/net10.0-android/`. Il peut être installé avec `adb install -r <chemin-de-l-apk>` ou transféré sur Google TV pour une installation manuelle.

## Limite actuelle

L’interface et la lecture utilisent la WebView Android. La prise en charge des conteneurs vidéo et codecs dépend donc de la version Android et du boîtier. Une intégration du lecteur natif Android est une amélioration possible si les formats du cache ne sont pas lus correctement.
