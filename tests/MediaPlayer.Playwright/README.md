# Tests Playwright

Ces scénarios lancent le site sur un port local isolé, dans un environnement de test dédié. Le middleware `PlaywrightApiMockMiddleware` fournit des réponses en mémoire pour TMDB, OpenSubtitles, les indexeurs, la bibliothèque, la progression et les téléchargements. Les services de fond qui démarrent BitTorrent ou synchronisent les indexeurs sont désactivés dans cet environnement. Le navigateur bloque aussi les requêtes vers les autres hôtes.

## Exécution

```powershell
npm install
npx playwright install chromium
npm test
```

Les données de cache de test sont écrites sous `.cache/`, ignoré par Git. Les traces et rapports Playwright sont eux aussi ignorés.
