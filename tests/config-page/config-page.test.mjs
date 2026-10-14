import { test, before, after } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { chromium } from 'playwright';

let browser;
const html = await readFile(new URL('../../Configuration/configPage.html', import.meta.url), 'utf8');
const config = {
    Enabled: false, WebUiUrl: 'http://example.test:8080', Username: 'admin', Password: 'saved-password', ApiKey: '',
    MaxUploadKiBps: 1024, ThrottleDownload: false, MaxDownloadKiBps: 10240, DelaySeconds: 5,
    HeadroomPercent: 10, UnknownStreamMbps: 20, FutureSetting: 'preserve-me'
};
before(async () => { browser = await chromium.launch({ headless: true }); });
after(async () => { await browser?.close(); });

async function load({ failLoad = false, failSave = false, connectionFailure = null } = {}) {
    const page = await browser.newPage();
    await page.setContent('<html><body></body></html>');
    await page.evaluate(({ config, failLoad, failSave, connectionFailure }) => {
        window.saved = [];
        window.requests = [];
        window.loading = false;
        window.alerts = [];
        window.ApiClient = {
            getPluginConfiguration: async () => { if (failLoad) throw Error('offline'); return config; },
            updatePluginConfiguration: async (_, value) => { if (failSave) throw Error('offline'); window.saved.push(value); return {}; },
            getUrl: path => 'http://example.test/' + path,
            ajax: async request => {
                window.requests.push(request);
                if (request.type === 'POST' && connectionFailure) {
                    if (connectionFailure === 'network') throw new TypeError('Failed to fetch');
                    throw new Response(connectionFailure, { status: 400 });
                }
                return request.type === 'POST' ? { Message: 'Connected.' } : { State: 'Disabled' };
            }
        };
        window.Dashboard = {
            showLoadingMsg: () => { window.loading = true; }, hideLoadingMsg: () => { window.loading = false; },
            alert: text => window.alerts.push(text), processPluginConfigurationUpdateResult: () => {}
        };
    }, { config, failLoad, failSave, connectionFailure });
    await page.setContent(html);
    await page.locator('#TorrentThrottleConfigPage').dispatchEvent('pageshow');
    await page.waitForFunction(() => !window.loading);
    return page;
}

test('saves zero delay, optional downloads and unknown fields without losing credentials', async () => {
    const page = await load();
    try {
        assert.equal(await page.locator('#MaxDownloadKiBps').isDisabled(), true);
        await page.locator('#ThrottleDownload').check();
        await page.locator('#DelaySeconds').fill('0');
        await page.locator('#MaxUploadKiBps').fill('5000');
        await page.locator('#SaveConfiguration').click();
        await page.waitForFunction(() => window.saved.length === 1);
        const saved = await page.evaluate(() => window.saved[0]);
        assert.equal(saved.Password, 'saved-password');
        assert.equal(saved.FutureSetting, 'preserve-me');
        assert.equal(saved.DelaySeconds, 0);
        assert.equal(saved.MaxUploadKiBps, 5000);
        assert.equal(saved.ThrottleDownload, true);
        assert.equal(await page.evaluate(() => window.loading), false);
    } finally { await page.close(); }
});

test('connection test sends unsaved credentials without saving configuration', async () => {
    const page = await load();
    try {
        await page.locator('#ApiKey').fill('qbt_unsaved');
        await page.locator('#TestConnection').click();
        await page.waitForFunction(() => document.getElementById('ConnectionResult').textContent === 'Connected.');
        const state = await page.evaluate(() => ({ saved: window.saved, requests: window.requests }));
        assert.equal(state.saved.length, 0);
        const request = state.requests.find(request => request.type === 'POST');
        assert.equal(JSON.parse(request.data).ApiKey, 'qbt_unsaved');
        assert.equal(request.url, 'http://example.test/TorrentThrottle/TestConnection');
    } finally { await page.close(); }
});

test('failed configuration load prevents saving defaults and clears the spinner', async () => {
    const page = await load({ failLoad: true });
    try {
        assert.equal(await page.locator('#SaveConfiguration').isDisabled(), true);
        assert.equal(await page.evaluate(() => window.alerts.length), 1);
        assert.equal(await page.evaluate(() => window.loading), false);
    } finally { await page.close(); }
});

test('failed save keeps the form usable and clears the spinner', async () => {
    const page = await load({ failSave: true });
    try {
        await page.locator('#SaveConfiguration').click();
        await page.waitForFunction(() => window.alerts.length === 1);
        assert.equal(await page.locator('#SaveConfiguration').isDisabled(), false);
        assert.equal(await page.evaluate(() => window.loading), false);
        assert.equal(await page.locator('#Password').inputValue(), 'saved-password');
    } finally { await page.close(); }
});

for (const [label, failure, expected] of [
    ['server validation', JSON.stringify({ Message: 'qBittorrent rejected the username or password.' }), 'qBittorrent rejected the username or password.'],
    ['camel-case response', JSON.stringify({ message: 'The request timed out.' }), 'The request timed out.'],
    ['non-JSON proxy error', '<html>Bad gateway</html>', 'Connection failed. Check the Web UI URL, credentials and network access from Jellyfin.'],
    ['network error', 'network', 'Connection failed. Check the Web UI URL, credentials and network access from Jellyfin.'],
    ['HTML-looking message', JSON.stringify({ Message: '<img src=x onerror="window.injected=true">' }), '<img src=x onerror="window.injected=true">']
]) {
    test(`connection failure shows safe, useful text: ${label}`, async () => {
        const page = await load({ connectionFailure: failure });
        try {
            await page.locator('#TestConnection').click();
            await page.waitForFunction(expected => document.getElementById('ConnectionResult').textContent === expected, expected);
            assert.equal(await page.locator('#TestConnection').isDisabled(), false);
            assert.equal(await page.locator('#ConnectionResult img').count(), 0);
            assert.equal(await page.evaluate(() => !!window.injected), false);
        } finally { await page.close(); }
    });
}
