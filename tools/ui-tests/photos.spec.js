const { test, expect } = require('@playwright/test');

function watchErrors(page) {
  const errors = [];
  page.on('console', (m) => {
    if (m.type() !== 'error') return;
    const url = m.location()?.url || '';
    if (/Failed to load resource/.test(m.text()) && url && !url.startsWith('http://127.0.0.1:8781')) return;
    errors.push(m.text());
  });
  page.on('pageerror', (e) => errors.push(String(e)));
  return errors;
}

async function showPhotos(page, options = {}, asStack = false) {
  await page.goto('/panel/');
  await page.evaluate(({ options, asStack }) => {
    localStorage.clear();
    const widget = asStack
      ? { id: 'st1', type: 'stack', x: 0, y: 0, w: 12, h: 6, options: { widgets: [{ id: 'ph-child', type: 'photos', enabled: true, options }] } }
      : { id: 'ph1', type: 'photos', x: 0, y: 0, w: 12, h: 6, options };
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false },
      shortcuts: [],
      pages: [{ id: 'photos-page', title: 'Photos', icon: '📸', enabled: true, widgets: [widget] }]
    }));
  }, { options, asStack });
  await page.reload();
  await expect(page.locator('.w').first()).toBeVisible();
  await expect(page.locator('body')).not.toHaveClass(/night/);
}

test('photo frame renders blob slide, en-GB date, clock overlay, tap next and timed rotation', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await showPhotos(page, {
    provider: 'icloud',
    shareUrl: 'https://www.icloud.com/sharedalbum/#B0zDemoAlbum',
    order: 'oldest',
    interval: 5,
    fit: 'contain-blur',
    transition: 'fade',
    info: 'caption-date',
    clockOverlay: true,
    tapAction: 'next',
    title: 'Trip',
  });

  const activeImg = page.locator('.w-photos .ph-layer.on .ph-img');
  await expect(activeImg).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.w-photos .ph-title')).toHaveText('Trip');
  await expect(page.locator('.w-photos .ph-clock')).toHaveText(/^\d{2}:\d{2}$/);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');
  await expect(page.locator('.w-photos .ph-sub')).toContainText('14 Jul 2025');

  // Tap advances to second photo
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Cliffside ocean trail');
  await expect(page.locator('.w-photos .ph-sub')).toContainText('2 Aug 2025');

  // Timed rotation advances to third photo
  await page.clock.fastForward(6000);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Midnight fjord glow');
  await expect(page.locator('.w-photos .ph-sub')).toContainText('19 Nov 2025');

  expect(errors).toEqual([]);
});

test('photo frame supports pause and info tap modes and Smart Stack embedding', async ({ page }) => {
  await page.clock.install();
  const errors = watchErrors(page);

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    order: 'oldest',
    interval: 5,
    tapAction: 'pause',
  });
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-state')).toHaveText('Paused');
  await page.clock.fastForward(8000);
  await expect(page.locator('.w-photos .ph-cap')).toHaveText('Alpine ridge at sunrise');

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    info: 'caption-date',
    tapAction: 'info',
  });
  await expect(page.locator('.w-photos .ph-meta')).toBeVisible();
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-meta')).toBeHidden();
  await page.locator('.w-photos .ph-stage').click();
  await expect(page.locator('.w-photos .ph-meta')).toBeVisible();

  await showPhotos(page, {
    provider: 'local',
    folder: '~/Pictures/Favorites',
    title: 'In Stack',
  }, true);
  await expect(page.locator('.stack-item.active .w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);
  await expect(page.locator('.stack-item.active .w-photos .ph-title')).toHaveText('In Stack');

  await showPhotos(page, {
    provider: 'local',
    folder: '~/OneDrive/CloudOnly',
    downloadCloudFiles: false,
  });
  await expect(page.locator('.w-photos .empty')).toContainText('Download cloud-only files on demand');

  await showPhotos(page, {
    provider: 'local',
    folder: '~/OneDrive/CloudOnly',
    downloadCloudFiles: true,
    title: 'Cloud Ready',
  });
  await expect(page.locator('.w-photos .ph-layer.on .ph-img')).toHaveAttribute('src', /^blob:/);

  expect(errors).toEqual([]);
});

test('settings page configures Cloud photos OAuth card, Photo frame providers, cloud warning, folder browser and Smart Stack child', async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto('/settings/');
  await expect(page.locator('#cloudPhotosCard')).toBeAttached();

  // General › Cloud photos OAuth card
  await page.locator('nav a[href="#general"]').click();
  await expect(page.locator('#cloudPhotosCard')).toBeVisible();
  await expect(page.locator('#cloudPhotosProviders')).toContainText('OneDrive');
  await expect(page.locator('#cloudPhotosProviders')).toContainText('Google Drive');
  await expect(page.locator('#cloudPhotosProviders')).toContainText('Dropbox');

  // Layout Editor: blank page + add Photo frame widget
  await page.locator('nav a[href="#pages"]').click();
  await page.getByRole('button', { name: 'New page' }).click();
  await expect(page.locator('#modal')).toBeVisible();
  await page.getByRole('button', { name: /Blank page/ }).click();
  await page.locator('#palette button.pal', { hasText: 'Photo frame' }).click();

  const inspector = page.locator('#inspector .photos-inspector');
  await expect(inspector).toBeVisible();

  const providerSelect = inspector.locator('select').first();
  await providerSelect.selectOption('icloud');
  await expect(inspector.locator('input[placeholder*="icloud.com/sharedalbum"]')).toBeVisible();

  await providerSelect.selectOption('nextcloud');
  await expect(inspector.locator('input[placeholder*="cloud.example.com"]')).toBeVisible();
  await expect(inspector.locator('input[type="password"]')).toBeVisible();

  await providerSelect.selectOption('onedrive');
  await inspector.getByRole('button', { name: 'Browse…' }).click();
  await expect(page.locator('#modal')).toBeVisible();
  await page.locator('#modalBody button', { hasText: 'Select' }).first().click();
  await expect(page.locator('#modal')).toBeHidden();

  await inspector.getByRole('button', { name: '5m', exact: true }).click();
  await expect(inspector.locator('input[type="number"]')).toHaveValue('300');

  await inspector.getByRole('button', { name: 'Test source' }).click();
  await expect(inspector).toContainText('Found 3 photos');

  // Cloud-only folder warning and explicit download opt-in toggle
  await inspector.locator('.chip', { hasText: 'OneDrive › CloudOnly' }).click();
  await expect(inspector.locator('.photos-warn')).toBeVisible();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud-only files detected (3)');
  const dlCheck = inspector.getByLabel('Download cloud-only files on demand');
  await expect(dlCheck).not.toBeChecked();
  await dlCheck.check();
  await expect(inspector.locator('.photos-warn')).toContainText('Cloud download enabled');
  await expect(inspector).toContainText('photos will be downloaded onto this computer as they are shown');

  // Smart Stack child configuration
  await page.locator('#palette button.pal', { hasText: 'Smart Stack' }).click();
  await expect(page.locator('#stackQuickAdd')).toBeVisible();
  await page.locator('#stackQuickAdd').selectOption('photos');
  await page.getByRole('button', { name: '＋ Add' }).click();
  const subCfg = page.locator('.stack-sub-config .photos-inspector');
  await expect(subCfg).toBeVisible();
  await subCfg.locator('select').first().selectOption('icloud');
  await expect(subCfg.locator('input[placeholder*="icloud.com/sharedalbum"]')).toBeVisible();

  expect(errors).toEqual([]);
});
