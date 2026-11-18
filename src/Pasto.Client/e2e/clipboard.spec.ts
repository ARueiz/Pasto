import { expect, test } from '@playwright/test';
import type { Page } from '@playwright/test';
import type { Item } from '../src/api';

const room = {
  roomId: '88888888-8888-4888-8888-888888888888', code: 'ABC123', credential: 'test-credential',
  participantId: '99999999-9999-4999-8999-999999999999', isCreator: true,
};
const original = '  https://example.test/path\n<img src=x onerror="alert(1)">';
const path = `/api/rooms/${room.roomId}`;
const makeItem = (id: string, content: string, isOwn: boolean): Item => ({
  id, content, isOwn, canDelete: isOwn, createdAt: '2026-11-18T08:00:00Z', createdVersion: id,
});

async function mockApi(page: Page) {
  const state = { items: [makeItem('1', original, false)], joins: 0, reads: 0, me: 0, adds: 0, closed: false, meStatus: 200, throttle: false };
  await page.route(url => url.pathname.startsWith('/api/rooms'), async route => {
    const request = route.request();
    const url = new URL(request.url()).pathname;
    const method = request.method();
    if (url === '/api/rooms' && method === 'POST') return route.fulfill({ status: 201, json: room });
    if (url === '/api/rooms/join' && method === 'POST') {
      state.joins++;
      return route.fulfill({ status: 201, json: { ...room, isCreator: false } });
    }
    expect(request.headers().authorization).toBe(`Bearer ${room.credential}`);
    if (state.closed) return route.fulfill({ status: 404, json: { code: 'RoomUnavailable' } });
    if (url === `${path}/me`) {
      state.me++;
      return route.fulfill(state.meStatus === 401
        ? { status: 401, json: { code: 'ParticipantUnauthorized' } }
        : { json: { roomId: room.roomId, participantId: room.participantId, isCreator: state.joins === 0 } });
    }
    if (url === `${path}/items` && method === 'GET') {
      state.reads++;
      if (state.throttle) return route.fulfill({ status: 429, headers: { 'Retry-After': '17' }, json: { code: 'RateLimited' } });
      return route.fulfill({ json: { items: state.items, itemCount: state.items.length, snapshotVersion: String(state.reads), expiresAt: '2026-11-18T13:00:00Z' } });
    }
    if (url === `${path}/items` && method === 'POST') {
      state.adds++;
      state.items.unshift(makeItem(String(state.adds + 1), request.postDataJSON().content, true));
      return route.fulfill({ status: 201, json: {} });
    }
    if (url.startsWith(`${path}/items/`) && method === 'DELETE') {
      state.items = state.items.filter(item => item.id !== url.split('/').at(-1));
      return route.fulfill({ json: { changed: true } });
    }
    if (url === path && method === 'DELETE') {
      state.closed = true;
      return route.fulfill({ status: 204 });
    }
    throw Error(`Unexpected request: ${method} ${url}`);
  });
  return state;
}

test('360px light room preserves text, searches safely, copies and confirms deletion', async ({ page }, info) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await page.emulateMedia({ colorScheme: 'light' });
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { value: { writeText: async () => { throw Error('denied'); } } });
  });
  const state = await mockApi(page);
  await page.goto('/');
  await expect(page.getByRole('heading', { name: '在裝置之間傳文字' })).toBeVisible();
  await expect(page.locator('.logo-mark')).not.toHaveCSS('mask-image', 'none');
  await page.screenshot({ path: info.outputPath('entry-mobile-light.png') });
  const cards = await page.locator('.entry-card').all();
  expect((await cards[0].boundingBox())!.x).toBe((await cards[1].boundingBox())!.x);
  await page.getByRole('button', { name: '建立新房間' }).click();
  await expect(page.locator('.message-text')).toHaveText(original, { useInnerText: false });
  expect(await page.locator('.message-text').textContent()).toBe(original);
  await expect(page.locator('.message-text a, .message-text img')).toHaveCount(0);
  await expect(page.getByText('訪客', { exact: true })).toBeVisible();
  expect(await page.locator('body').evaluate(element => element.scrollWidth)).toBe(360);
  await page.screenshot({ path: info.outputPath('room-mobile-light.png') });
  await page.getByRole('button', { name: '加入 QR Code' }).click();
  await expect(page.getByRole('region', { name: '房間邀請' })).toContainText('/r/ABC123');
  await page.getByRole('button', { name: '加入 QR Code' }).click();
  await page.getByRole('button', { name: '搜尋', exact: true }).click();
  const search = page.getByRole('searchbox');
  await search.fill('IMG');
  await expect(page.locator('mark')).toHaveText('img');
  await search.fill('nothing here');
  await expect(page.getByText('找不到符合的文字')).toBeVisible();
  await search.press('Escape');
  await expect(search).toHaveCount(0);
  await page.getByRole('button', { name: '複製', exact: true }).click();
  await expect(page.getByText('無法自動複製')).toBeVisible();
  await expect(page.locator('.copy-toast[role="status"]')).toBeEmpty();
  const fallback = page.getByRole('textbox', { name: '手動複製文字' });
  expect(await fallback.evaluate(element => (element as HTMLTextAreaElement).selectionEnd)).toBe(original.length);
  await page.getByRole('button', { name: '關閉', exact: true }).click();
  const input = page.getByRole('textbox', { name: '要送出的文字' });
  await input.fill('一'.repeat(2731));
  await expect(page.getByText('8,193 / 8,192 bytes ・超過上限')).toBeVisible();
  await expect(page.getByRole('button', { name: '送出' })).toBeDisabled();
  const atLimit = '一'.repeat(2730) + 'ab';
  await input.fill(atLimit);
  await expect(page.getByRole('button', { name: '送出' })).toBeEnabled();
  await page.getByRole('button', { name: '送出' }).click();
  await expect(page.getByRole('button', { name: '刪除', exact: true })).toHaveCount(1);
  expect(state.items[0].content).toBe(atLimit);
  await page.getByRole('button', { name: '刪除', exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: '取消' }).click();
  await expect(page.locator('.message')).toHaveCount(2);
  await page.getByRole('button', { name: '刪除', exact: true }).click();
  await dialog.getByRole('button', { name: '刪除文字' }).click();
  await expect(page.locator('.message')).toHaveCount(1);
});

test('desktop dark room switches theme, refreshes manually and recovers owner identity', async ({ page }, info) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.emulateMedia({ colorScheme: 'dark' });
  const state = await mockApi(page);
  await page.goto('/');
  const cards = await page.locator('.entry-card').all();
  expect((await cards[0].boundingBox())!.y).toBe((await cards[1].boundingBox())!.y);
  await expect(page.locator('html')).toHaveCSS('color-scheme', 'dark');
  await page.screenshot({ path: info.outputPath('entry-desktop-dark.png') });
  await page.getByRole('button', { name: '建立新房間' }).click();
  await expect(page.locator('.message')).toHaveCount(1);
  await expect(page.getByText('其他裝置的新內容不會自動出現')).toBeVisible();
  state.items.unshift(makeItem('2', '另一台裝置的文字', false));
  await expect(page.getByText('另一台裝置的文字')).toHaveCount(0);
  await page.getByRole('button', { name: '重新整理' }).click();
  await expect(page.getByText('另一台裝置的文字')).toBeVisible();
  await page.screenshot({ path: info.outputPath('room-desktop-dark.png') });
  await page.getByRole('button', { name: '更多選項' }).click();
  await page.getByRole('button', { name: '淺色' }).click();
  await expect(page.locator('html')).toHaveCSS('color-scheme', 'light');
  expect(await page.evaluate(() => localStorage.getItem('pasto-theme'))).toBe('light');
  await page.getByRole('button', { name: '深色' }).click();
  await expect(page.locator('html')).toHaveCSS('color-scheme', 'dark');
  await page.getByRole('button', { name: '系統' }).click();
  expect(await page.evaluate(() => localStorage.getItem('pasto-theme'))).toBeNull();
  await page.reload();
  await expect(page.getByText('另一台裝置的文字')).toBeVisible();
  expect(state.me).toBe(1);
  expect(state.joins).toBe(0);
  await page.getByRole('button', { name: '更多選項' }).click();
  await expect(page.getByRole('button', { name: '關閉房間' })).toBeVisible();
  await expect(page.getByRole('button', { name: '離開房間' })).toHaveCount(0);
});

test('restore 401 waits for new identity and does not rejoin on reload', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.locator('.message')).toHaveCount(1);
  expect(state.joins).toBe(1);
  state.meStatus = 401;
  await page.reload();
  await expect(page.getByText('這個分頁的身分已失效')).toBeVisible();
  await page.reload();
  await expect(page.getByText('這個分頁的身分已失效')).toBeVisible();
  expect(state.joins).toBe(1);
  await page.getByRole('button', { name: '以新身分加入' }).click();
  await expect(page.locator('.message')).toHaveCount(1);
  expect(state.joins).toBe(2);
  await page.getByRole('button', { name: '更多選項' }).click();
  await expect(page.getByRole('button', { name: '關閉房間' })).toHaveCount(0);
});

test('a visitor joins by a pasted share URL and can leave without closing the room', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/');
  const input = page.getByRole('textbox', { name: '六位房碼' });
  await input.evaluate(element => {
    const clipboardData = new DataTransfer();
    clipboardData.setData('text/plain', 'https://pasto.example/r/abc123');
    element.dispatchEvent(new ClipboardEvent('paste', { clipboardData, bubbles: true, cancelable: true }));
  });
  await expect(input).toHaveValue('ABC123');
  await page.getByRole('button', { name: '加入', exact: true }).click();
  await expect(page).toHaveURL('/r/ABC123');
  await expect(page.locator('.message')).toHaveCount(1);
  expect(state.joins).toBe(1);
  await page.getByRole('button', { name: '更多選項' }).click();
  await expect(page.getByRole('button', { name: '關閉房間' })).toHaveCount(0);
  await page.getByRole('button', { name: '離開房間' }).click();
  await page.getByRole('dialog').getByRole('button', { name: '離開房間' }).click();
  await expect(page).toHaveURL('/');
  expect(await page.evaluate(() => sessionStorage.length)).toBe(0);
  expect(state.closed).toBe(false);
});

for (const view of [
  { width: 360, height: 740, colorScheme: 'light' },
  { width: 1280, height: 800, colorScheme: 'dark' },
] as const) {
  test(`${view.width}px ${view.colorScheme}: copy toast, composer focus and role-based menus`, async ({ page }, info) => {
    await page.setViewportSize({ width: view.width, height: view.height });
    await page.emulateMedia({ colorScheme: view.colorScheme });
    await page.clock.install();
    await page.addInitScript(() => {
      Object.defineProperty(navigator, 'clipboard', { value: { writeText: async (text: string) => {
        document.documentElement.dataset.testClipboard = text;
      } } });
    });
    await mockApi(page);
    await page.goto('/');
    await page.getByRole('button', { name: '建立新房間' }).click();
    await expect(page.locator('.message')).toHaveCount(1);

    const toast = page.locator('.copy-toast[role="status"]');
    const composer = page.locator('.composer-input');
    const chat = page.locator('.chat-scroll');
    const composerBefore = await composer.boundingBox();
    const chatBefore = await chat.boundingBox();
    await page.getByRole('button', { name: '複製邀請' }).click();
    await expect(toast).toHaveText('已複製邀請');
    const invite = page.url();
    expect(await page.locator('html').getAttribute('data-test-clipboard')).toBe(invite);
    expect(await composer.boundingBox()).toEqual(composerBefore);
    expect(await chat.boundingBox()).toEqual(chatBefore);
    expect((await toast.boundingBox())!.y + (await toast.boundingBox())!.height).toBeLessThan(composerBefore!.y);
    await expect(page.getByRole('textbox', { name: '手動複製文字' })).toHaveCount(0);

    await page.clock.runFor(1200);
    await page.getByRole('button', { name: '複製邀請' }).click();
    await expect(toast).toHaveText('已複製邀請\u00a0');
    expect(await toast.textContent()).toBe('已複製邀請\u00a0');
    await page.clock.runFor(1000);
    await expect(toast).toHaveText('已複製邀請');
    await page.clock.runFor(1100);
    await expect(toast).toBeEmpty();

    await page.getByRole('button', { name: '複製', exact: true }).click();
    await expect(toast).toHaveText('已複製文字');
    expect(await page.locator('html').getAttribute('data-test-clipboard')).toBe(original);
    await page.getByRole('button', { name: '重新整理' }).focus();
    await page.keyboard.press('Tab');
    const input = page.getByRole('textbox', { name: '要送出的文字' });
    await expect(input).toBeFocused();
    await expect(input).toHaveCSS('outline-style', 'none');
    const accent = view.colorScheme === 'light' ? 'rgb(13, 148, 136)' : 'rgb(20, 184, 166)';
    await expect(composer).toHaveCSS('border-top-color', accent);
    await expect(composer).toHaveCSS('box-shadow', `${accent} 0px 0px 0px 1px`);
    await page.screenshot({ path: info.outputPath('focus-and-copy.png') });
    await page.clock.runFor(2100);
    await expect(toast).toBeEmpty();

    await page.getByRole('button', { name: '更多選項' }).click();
    await expect(page.getByRole('button', { name: '關閉房間' })).toBeVisible();
    await expect(page.getByRole('button', { name: '離開房間' })).toHaveCount(0);
    await page.screenshot({ path: info.outputPath('owner-menu.png') });
    await page.evaluate(() => sessionStorage.clear());
    await page.goto(invite);
    await expect(page.locator('.message')).toHaveCount(1);
    await page.getByRole('button', { name: '更多選項' }).click();
    await expect(page.getByRole('button', { name: '離開房間' })).toBeVisible();
    await expect(page.getByRole('button', { name: '關閉房間' })).toHaveCount(0);
    await page.screenshot({ path: info.outputPath('visitor-menu.png') });
  });
}

test('missing Clipboard API falls back to selected invite text', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { value: undefined });
  });
  await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.locator('.message')).toHaveCount(1);
  await page.getByRole('button', { name: '複製邀請' }).click();
  await expect(page.getByText('無法自動複製')).toBeVisible();
  const fallback = page.getByRole('textbox', { name: '手動複製文字' });
  await expect(fallback).toHaveValue(page.url());
  expect(await fallback.evaluate(element => (element as HTMLTextAreaElement).selectionEnd)).toBe(page.url().length);
  await expect(page.locator('.copy-toast[role="status"]')).toBeEmpty();
});

test('manual copy stays open without stealing focus while composing or searching', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { value: undefined });
  });
  await mockApi(page);
  await page.goto('/r/ABC123');
  await page.getByRole('button', { name: '複製', exact: true }).click();
  const fallback = page.getByRole('textbox', { name: '手動複製文字' });
  await expect(fallback).toBeFocused();
  expect(await fallback.evaluate(element => (element as HTMLTextAreaElement).selectionEnd)).toBe(original.length);

  const composer = page.getByRole('textbox', { name: '要送出的文字' });
  await composer.pressSequentially('Pasto clipboard');
  await expect(composer).toHaveValue('Pasto clipboard');
  await expect(composer).toBeFocused();

  await page.getByRole('button', { name: '搜尋', exact: true }).click();
  const search = page.getByRole('searchbox');
  await search.pressSequentially('example');
  await expect(search).toHaveValue('example');
  await expect(search).toBeFocused();
  await expect(fallback).toBeVisible();
});

test('denied sessionStorage and throttled reads show the matching notices', async ({ page }) => {
  await page.addInitScript(() => {
    Object.defineProperty(window, 'sessionStorage', { get: () => { throw new DOMException('denied', 'SecurityError'); } });
  });
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.getByText('身分只保存在這次開啟的頁面')).toBeVisible();
  await expect(page.locator('.message')).toHaveCount(1);
  state.throttle = true;
  await page.getByRole('button', { name: '重新整理' }).click();
  await expect(page.getByText('操作太頻繁')).toBeVisible();
  await expect(page.getByText('請在 17 秒後再試一次。')).toBeVisible();
});

test('room closure is confirmed and clears the room with a 404 notice', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/');
  await page.getByRole('button', { name: '建立新房間' }).click();
  await expect(page.locator('.message')).toHaveCount(1);
  await page.getByRole('button', { name: '更多選項' }).click();
  await page.getByRole('button', { name: '關閉房間' }).click();
  await page.getByRole('dialog').getByRole('button', { name: '關閉房間' }).click();
  await expect(page.getByText('這個房間已關閉或到期')).toBeVisible();
  expect(state.closed).toBe(true);
  await page.reload();
  await expect(page.getByText('這個房間已關閉或到期')).toBeVisible();
  await page.getByRole('button', { name: '回首頁' }).click();
  await expect(page).toHaveURL('/');
  await page.evaluate(() => sessionStorage.clear());
  await page.goto('/r/ABC123');
  await expect(page.getByText('這個房間已關閉或到期')).toBeVisible();
});
