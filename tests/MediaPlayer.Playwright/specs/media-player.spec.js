const { test, expect } = require('@playwright/test');

test.beforeEach(async ({ page, baseURL }) => {
  // Only the local test server is allowed. Its Playwright environment serves in-memory API fixtures.
  const localHost = new URL(baseURL).host;
  await page.route('**/*', async route => {
    if (new URL(route.request().url()).host === localHost) await route.continue();
    else await route.abort();
  });
});

async function openInteractivePage(page, path) {
  const circuit = page.waitForEvent('websocket');
  await page.goto(path);
  const socket = await circuit;
  await socket.waitForEvent('framereceived');
  await page.waitForTimeout(250);
}

test('homepage shows a working resume bar and rotates its featured media', async ({ page }) => {
  await openInteractivePage(page, '/');

  await expect(page.locator('.topbar .profile')).toHaveCSS('position', 'absolute');
  await expect(page.locator('.topbar .profile')).toHaveCSS('right', '24px');
  const heroTitle = page.locator('.hero-copy h1');
  await expect(heroTitle).toHaveText("Verity");
  const verityCard = page.locator('.watching-card', { hasText: 'Verity' });
  const fill = verityCard.locator('.watching-progress span');
  await expect(fill).toHaveAttribute('style', /width:5\.78%/);
  await expect(verityCard.locator('.watching-progress')).toHaveCSS('background-color', 'rgb(255, 255, 255)');
  const downloaded = page.locator('.poster-card', { hasText: 'Verity' }).first().locator('.media-status-badge');
  const downloading = page.locator('.poster-card', { hasText: 'The Office' }).first().locator('.media-status-badge');
  const watchlisted = page.locator('.poster-card', { hasText: 'Silo' }).first().locator('.media-status-badge');
  await expect(downloaded).toHaveAttribute('aria-label', 'Téléchargé');
  await expect(downloading).toHaveAttribute('aria-label', 'Téléchargement en cours');
  await expect(watchlisted).toHaveAttribute('aria-label', 'Surveillé');
  await expect(downloaded.locator('svg')).toBeVisible();
  await expect(downloaded).toHaveCSS('right', '9px');
  await expect(page.locator('.watching-card', { hasText: 'Silo' })).toContainText('Saison 1 · Épisode 2');
  await expect(page.getByText('LES PLUS POPULAIRES')).toHaveCount(0);
  await expect(page.getByText(/Cache mis à jour|SOURCES CONFIGURÉES|ACTUALISÉES TOUTES LES/)).toHaveCount(0);

  await expect(heroTitle).not.toHaveText('Verity', { timeout: 5_000 });
});

test('a film with no matching torrent source can be added to the watchlist', async ({ page }) => {
  await openInteractivePage(page, '/');
  await page.getByRole('button', { name: 'Lire Verity' }).first().click();

  await expect(page).toHaveURL(/\/lecture\/movie\/1283515/);
  await expect(page.getByText('Aucune source ne propose de correspondance pour ce titre.')).toBeVisible();
  await page.getByRole('button', { name: /À surveiller/ }).click();
  await expect(page.getByText('Ajouté à la liste « À surveiller ».')).toBeVisible();
});

test('series information loads season episodes from the mocked catalogue', async ({ page }) => {
  await openInteractivePage(page, '/infos/tv/125988');

  await expect(page.getByRole('heading', { name: 'Silo' })).toBeVisible();
  await expect(page.getByRole('tab', { name: 'Saison 1' })).toBeVisible();
  await expect(page.getByText('01 · La Flamme')).toBeVisible();
  await expect(page.getByText('02 · Holston')).toBeVisible();
});

test('configuration saves API keys against the in-memory credential mock', async ({ page }) => {
  await openInteractivePage(page, '/configuration');
  await expect(page.getByRole('heading', { name: 'Tableau de bord' })).toBeVisible();
  await expect(page.getByText('Actualisation automatique toutes les 8 heures')).toBeVisible();
  await expect(page.getByText(/Dernière mise à jour :/)).toBeVisible();
  await page.getByLabel('TMDB Read Access Token').fill('playwright-tmdb-token');
  await page.getByLabel('Clé API OpenSubtitles').fill('playwright-opensubtitles-key');
  await page.getByRole('button', { name: 'Enregistrer les clés' }).click();

  await expect(page).toHaveURL('/');
  await expect(page.locator('.hero-copy h1')).toBeVisible();
});

test('library renders without touching disk cache or a torrent client', async ({ page }) => {
  await openInteractivePage(page, '/bibliotheque');

  await expect(page.getByRole('heading', { name: 'Ma bibliothèque' })).toBeVisible();
  await expect(page.getByText('Aucun média trouvé')).toBeVisible();
  await expect(page.getByText('Aucun téléchargement en cours')).toBeVisible();
});
