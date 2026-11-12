# MediaPlayer TV

Client Android TV natif pour Google TV. Il affiche un catalogue, des fiches médias et la bibliothèque avec des contrôles MAUI adaptés à la télécommande. Le seul réglage de l’application est l’adresse du serveur MediaPlayer.

## Démarrer le serveur sur le réseau local

Depuis le dépôt, lancer le serveur en écoutant sur toutes les interfaces réseau :

```powershell
dotnet run --project src/MediaPlayer.Web --no-launch-profile --urls http://0.0.0.0:6050
```

Autoriser MediaPlayer dans le pare-feu Windows pour le réseau privé si nécessaire. Le PC et le boîtier Google TV doivent être sur le même réseau. Au premier lancement, l’adresse `http://brique2:6050` est préremplie ; elle suppose que `brique2` résout vers le PC sur le réseau local et que le serveur écoute sur le port 6050. Remplacer cette adresse si le nom ou le port diffère. L’adresse choisie est conservée sur le boîtier.

Si l’adresse enregistrée ne répond pas, l’application recherche automatiquement les serveurs MediaPlayer présents sur le sous-réseau IPv4 local. La page de configuration propose aussi une recherche manuelle et permet de choisir parmi plusieurs serveurs trouvés. Le port 6050 et le port de l’adresse configurée sont vérifiés.

## Construire l’APK

Avec la charge de travail .NET Android, le SDK Android et JDK 21 installés :

```powershell
dotnet publish src/MediaPlayer.AndroidTv/MediaPlayer.AndroidTv.csproj -f net10.0-android -c Release -p:AndroidPackageFormat=apk
```

Le fichier APK signé de développement se trouve dans `src/MediaPlayer.AndroidTv/bin/Release/net10.0-android/`. Il peut être installé avec `adb install -r <chemin-de-l-apk>` ou transféré sur Google TV pour une installation manuelle.

Le client peut aussi être exécuté comme application Windows pour accélérer les itérations sur l’interface :

```powershell
dotnet run --project src/MediaPlayer.AndroidTv/MediaPlayer.AndroidTv.csproj -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=None
```

Les règles de mise en page responsive sont couvertes par les tests multiplateformes du client :

```powershell
dotnet test tests/MediaPlayer.AndroidTv.Tests/MediaPlayer.AndroidTv.Tests.csproj
```

## Lecture

La navigation, les fiches et le lecteur sont natifs ; aucune URL média n’est envoyée à une application externe. Le lecteur lit le flux de la bibliothèque avec MediaElement sur Android TV et Windows. La prise en charge des conteneurs et codecs dépend des décodeurs disponibles sur l’appareil.
