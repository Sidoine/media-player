const { defineConfig, devices } = require('@playwright/test');
const path = require('node:path');

const baseURL = 'http://127.0.0.1:5187';

module.exports = defineConfig({
  testDir: './specs',
  fullyParallel: true,
  forbidOnly: Boolean(process.env.CI),
  retries: process.env.CI ? 1 : 0,
  workers: process.env.CI ? 2 : undefined,
  reporter: 'list',
  use: {
    baseURL,
    trace: 'retain-on-failure',
    ...devices['Desktop Chrome'],
  },
  webServer: {
    command: 'dotnet run --project ../../src/MediaPlayer.Web/MediaPlayer.Web.csproj --no-build --no-launch-profile --urls http://127.0.0.1:5187',
    url: baseURL,
    reuseExistingServer: false,
    timeout: 120_000,
    env: {
      ASPNETCORE_ENVIRONMENT: 'Development',
      Playwright__Enabled: 'true',
      TMDB_READ_ACCESS_TOKEN: 'playwright-test-token',
      OPENSUBTITLES_API_KEY: 'playwright-test-key',
      Media__CacheRoot: path.resolve(__dirname, '.cache'),
      Playwright__HeroRotationMilliseconds: '600',
    },
  },
});
