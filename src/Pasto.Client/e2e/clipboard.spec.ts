import { expect, test } from '@playwright/test';
import type { Page, WebSocketRoute } from '@playwright/test';
import type { Item } from '../src/api';

const room = {
  roomId: '88888888-8888-4888-8888-888888888888', code: 'ABC123', credential: 'test-credential',
  participantId: '99999999-9999-4999-8999-999999999999', isCreator: true, authorCode: '368F1',
};
const guest = { ...room, participantId: '77777777-7777-4777-8777-777777777777', isCreator: false, authorCode: '4FA0D' };
const otherGuest = { ...guest, participantId: '66666666-6666-4666-8666-666666666666', authorCode: '815E2' };
const original = '  https://example.test/path\n<img src=x onerror="alert(1)">';
const path = `/api/rooms/${room.roomId}`;
const makeItem = (id: string, content: string, isOwn: boolean, createdVersion = id): Item => ({
  id, content, isOwn, canDelete: isOwn, createdAt: '2026-11-18T08:00:00Z', createdVersion,
  authorCode: guest.authorCode, authorIsCreator: false,
});

async function mockApi(page: Page, hubAvailable = true, joinedIdentity = guest) {
  let socket: WebSocketRoute | undefined;
  const state = {
    items: [makeItem('1', original, false)], joins: 0, reads: 0, me: 0, adds: 0, closed: false,
    meStatus: 200, throttle: false, version: 1n, subscriptions: 0, checks: 0, lateResponses: 0,
    negotiateStatus: hubAvailable ? 200 : 403, negotiations: 0,
    beforeRead: undefined as (() => Promise<number | void>) | undefined,
    beforeNegotiate: undefined as (() => Promise<void>) | undefined,
    beforeAddResponse: undefined as ((version: bigint) => Promise<void>) | undefined,
    allowSnapshotBeforeSubscribe: false,
    notify: (version: bigint, roomId = room.roomId) => {
      socket?.send(JSON.stringify({ type: 1, target: 'RoomChanged', arguments: [{ roomId, version: String(version) }] }) + '\x1e');
    },
    disconnect: () => socket?.close({ code: 1011 }),
  };
  await page.route('**/hubs/room/negotiate*', async route => {
    expect(route.request().headers().authorization).toBe(`Bearer ${room.credential}`);
    state.negotiations++;
    await state.beforeNegotiate?.();
    if (state.negotiateStatus !== 200) return route.fulfill({ status: state.negotiateStatus, json: {} });
    return route.fulfill({ json: {
      connectionId: 'test-connection', connectionToken: 'test-token', negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
    } });
  });
  await page.routeWebSocket(/\/hubs\/room\?/, ws => {
    socket = ws;
    ws.onMessage(payload => {
      for (const frame of payload.toString().split('\x1e').filter(Boolean)) {
        const request = JSON.parse(frame);
        if (request.protocol === 'json') ws.send('{}\x1e');
        if (request.target === 'SubscribeRoom') {
          expect(request.arguments).toEqual([room.roomId]);
          state.subscriptions++;
          ws.send(JSON.stringify({ type: 3, invocationId: request.invocationId }) + '\x1e');
        }
      }
    });
  });
  await page.route(url => url.pathname.startsWith('/api/rooms'), async route => {
    const request = route.request();
    const url = new URL(request.url()).pathname;
    const method = request.method();
    if (url === '/api/rooms' && method === 'POST') return route.fulfill({ status: 201, json: room });
    if (url === '/api/rooms/join' && method === 'POST') {
      state.joins++;
      return route.fulfill({ status: 201, json: joinedIdentity });
    }
    expect(request.headers().authorization).toBe(`Bearer ${room.credential}`);
    if (state.closed) return route.fulfill({ status: 404, json: { code: 'RoomUnavailable' } });
    const { roomId, participantId, isCreator, authorCode } = state.joins ? joinedIdentity : room;
    if (url === `${path}/me`) {
      state.me++;
      return route.fulfill(state.meStatus === 401
        ? { status: 401, json: { code: 'ParticipantUnauthorized' } }
        : { json: { roomId, participantId, isCreator, authorCode } });
    }
    if (url === `${path}/items` && method === 'GET') {
      if (hubAvailable && !state.allowSnapshotBeforeSubscribe) expect(state.subscriptions).toBeGreaterThan(0);
      state.reads++;
      if (state.throttle) return route.fulfill({ status: 429, headers: { 'Retry-After': '17' }, json: { code: 'RateLimited' } });
      const snapshot = { items: [...state.items], itemCount: state.items.length, snapshotVersion: String(state.version), expiresAt: '2026-11-18T13:00:00Z' };
      const lateStatus = await state.beforeRead?.();
      if (lateStatus) {
        await route.fulfill({ status: lateStatus, json: {} });
        state.lateResponses++;
        return;
      }
      return route.fulfill({ json: snapshot });
    }
    if (url === `${path}/items` && method === 'POST') {
      state.adds++;
      const committedVersion = ++state.version;
      state.items.unshift({
        ...makeItem(String(state.adds + 1), request.postDataJSON().content, true, String(committedVersion)),
        authorCode, authorIsCreator: isCreator,
      });
      await state.beforeAddResponse?.(committedVersion);
      return route.fulfill({ status: 201, json: { committedVersion: String(committedVersion) } });
    }
    if (url.startsWith(`${path}/items/`) && method === 'DELETE') {
      state.items = state.items.filter(item => item.id !== url.split('/').at(-1));
      return route.fulfill({ json: { changed: true, committedVersion: String(++state.version) } });
    }
    if (url === path && method === 'GET') {
      state.checks++;
      return route.fulfill({ json: { version: String(state.version) } });
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
  await expect(page.locator('.speaker')).toHaveText(guest.authorCode);
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
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  state.items.unshift(makeItem('2', '另一台裝置的文字', false));
  state.version++;
  await expect(page.getByText('另一台裝置的文字')).toHaveCount(0);
  await page.getByRole('button', { name: '更多選項' }).click();
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

for (const viewer of [room, guest, otherGuest]) {
  test(`author labels and identity are correct for ${viewer.authorCode} at 360px`, async ({ page }) => {
    await page.setViewportSize({ width: 360, height: 740 });
    await page.emulateMedia({ colorScheme: 'light' });
    const state = await mockApi(page, true, viewer);
    const authors = [room, guest, otherGuest];
    state.items = authors.map((author, index) => ({
      ...makeItem(String(3 - index), `測試訊息 ${index}`, viewer === author),
      authorCode: author.authorCode, authorIsCreator: author.isCreator,
    }));
    if (viewer.isCreator) {
      await page.goto('/');
      await page.getByRole('button', { name: '建立新房間' }).click();
    } else {
      await page.goto('/r/ABC123');
    }
    const ownLabel = viewer.isCreator ? '你是 368F1(房主)' : `你是 ${viewer.authorCode}`;
    await expect(page.locator('#composer-identity')).toHaveText(ownLabel);
    await expect(page.getByRole('textbox', { name: '要送出的文字' })).toHaveAccessibleDescription(ownLabel);
    const header = page.locator('.room-header .room-row');
    expect(await header.evaluate(element => element.scrollWidth <= element.clientWidth
      && Math.abs(element.children[0].getBoundingClientRect().top - element.children[1].getBoundingClientRect().top) <= 5)).toBe(true);

    const speaker = (index: number) => page.locator('.message').filter({ hasText: `測試訊息 ${index}` }).locator('.speaker');
    const labels = viewer === room ? ['你(房主)', '4FA0D', '815E2']
      : viewer === guest ? ['368F1(房主)', '你', '815E2'] : ['368F1(房主)', '4FA0D', '你'];
    for (let index = 0; index < authors.length; index++) {
      await expect(speaker(index)).toHaveText(labels[index]);
      await expect(speaker(index)).toHaveCSS('color', viewer === authors[index] ? 'rgb(74, 97, 102)'
        : index === 2 ? 'rgb(15, 118, 110)' : 'rgb(159, 18, 57)');
    }
    await page.emulateMedia({ colorScheme: 'dark' });
    for (let index = 0; index < authors.length; index++) {
      await expect(speaker(index)).toHaveCSS('color', viewer === authors[index] ? 'rgb(157, 174, 185)'
        : index === 2 ? 'rgb(94, 234, 212)' : 'rgb(253, 164, 175)');
    }
    await expect(page.locator('#composer-identity span')).toHaveCSS('color',
      viewer === otherGuest ? 'rgb(94, 234, 212)' : 'rgb(253, 164, 175)');
  });
}

test('author grouping uses consecutive visible messages and keeps accessible names', async ({ page }) => {
  const state = await mockApi(page);
  const items = [
    { content: '搜尋 第一則', time: '08:00:00', author: room },
    { content: '搜尋 五分鐘', time: '08:05:00', author: room },
    { content: '另一人插話', time: '08:06:00', author: otherGuest },
    { content: '搜尋 回到房主', time: '08:07:00', author: room },
    { content: '搜尋 超過五分鐘', time: '08:12:01', author: room },
  ];
  state.items = items.map(({ content, time, author }, index) => ({
    ...makeItem(String(index + 1), content, false), createdAt: `2026-11-18T${time}Z`,
    authorCode: author.authorCode, authorIsCreator: author.isCreator,
  })).reverse();
  await page.goto('/r/ABC123');
  const roomOwnerLabels = page.locator('.speaker:not(.visually-hidden)').filter({ hasText: '368F1(房主)' });
  await expect(roomOwnerLabels).toHaveCount(3);
  await expect(page.locator('.speaker:not(.visually-hidden)').filter({ hasText: otherGuest.authorCode })).toHaveCount(1);
  const grouped = page.getByRole('article', { name: '368F1(房主)', exact: true }).filter({ hasText: '搜尋 五分鐘' });
  await expect(grouped.locator('.speaker.visually-hidden')).toHaveCount(1);
  await page.getByRole('button', { name: '搜尋', exact: true }).click();
  await page.getByRole('searchbox').fill('搜尋');
  await expect(roomOwnerLabels).toHaveCount(2);
  await expect(page.getByRole('article', { name: '368F1(房主)', exact: true }).filter({ hasText: '搜尋 回到房主' })
    .locator('.speaker.visually-hidden')).toHaveCount(1);
});

test.describe('date separators', () => {
  test.use({ timezoneId: 'Asia/Taipei' });

  test('the first message after local midnight shows its author again', async ({ page }) => {
    const state = await mockApi(page);
    state.version = 3n;
    state.items = [
      { content: '午夜前的文字', createdAt: '2026-11-18T15:59:00Z' },
      { content: '午夜後的第一則', createdAt: '2026-11-18T16:00:00Z' },
      { content: '午夜後的第二則', createdAt: '2026-11-18T16:02:00Z' },
    ].map(({ content, createdAt }, index) => ({
      ...makeItem(String(index + 1), content, false),
      createdAt, authorCode: room.authorCode, authorIsCreator: true,
    })).reverse();

    await page.goto('/r/ABC123');
    await expect(page.locator('.date-divider')).toHaveCount(2);
    const firstAfterMidnight = page.getByRole('article', { name: '368F1(房主)', exact: true })
      .filter({ hasText: '午夜後的第一則' });
    await expect(firstAfterMidnight.locator('.speaker:not(.visually-hidden)')).toHaveText('368F1(房主)');
    const secondAfterMidnight = page.getByRole('article', { name: '368F1(房主)', exact: true })
      .filter({ hasText: '午夜後的第二則' });
    await expect(secondAfterMidnight.locator('.speaker.visually-hidden')).toHaveCount(1);
  });
});

test('restores a saved guest identity that predates authorCode', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.locator('#composer-identity')).toHaveText('你是 4FA0D');
  await page.evaluate(id => {
    const key = `pasto:room:credential:${id}`;
    const stored = JSON.parse(sessionStorage.getItem(key)!);
    delete stored.authorCode;
    sessionStorage.setItem(key, JSON.stringify(stored));
  }, room.roomId);
  await page.reload();
  await expect(page.locator('#composer-identity')).toHaveText('你是 4FA0D');
  expect(state.me).toBe(1);
  expect(state.joins).toBe(1);
  expect(await page.evaluate(id => JSON.parse(sessionStorage.getItem(`pasto:room:credential:${id}`)!).authorCode, room.roomId))
    .toBe('4FA0D');
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

test('sync merges repeated and out-of-order hints and waits for the current snapshot before reading again', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  await page.getByRole('textbox', { name: '要送出的文字' }).fill('尚未送出的文字');
  await expect(page.getByRole('button', { name: '送出' })).toBeEnabled();

  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  state.beforeRead = () => held;
  state.items.unshift(makeItem('5', '第一批文字', false));
  state.version = 5n;
  for (let index = 0; index < 20; index++) {
    for (const version of [2n, 5n, 5n, 3n]) state.notify(version);
  }
  await expect.poll(() => state.reads).toBe(2);
  state.items.unshift(makeItem('12', '第二批文字', false));
  state.version = 12n;
  state.notify(100n, 'other-room');
  state.notify(12n);
  state.notify(10n);
  await expect(page.getByText('同步中', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '送出' })).toBeEnabled();
  expect(state.reads).toBe(2);
  release();

  await expect(page.getByText('第二批文字')).toBeVisible();
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  expect(state.reads).toBe(3);
});

test('a blocked Hub still allows initial REST reads, refresh, polling and writes', async ({ page }) => {
  await page.clock.install();
  const state = await mockApi(page, false);
  await page.goto('/r/ABC123');
  await expect(page.locator('.message-text')).toHaveText(original);
  await expect(page.getByText('連線中斷')).toBeVisible();
  expect(state.subscriptions).toBe(0);

  let releaseNegotiate!: () => void;
  const heldNegotiate = new Promise<void>(resolve => { releaseNegotiate = resolve; });
  state.beforeNegotiate = () => heldNegotiate;
  state.items.unshift(makeItem('remote-2', '手動重新整理取得的文字', false, '2'));
  state.version = 2n;
  await page.getByRole('button', { name: '重新整理' }).click();
  await expect(page.getByText('手動重新整理取得的文字')).toBeVisible();

  const checksBeforePoll = state.checks;
  state.items.unshift(makeItem('remote-3', '輪詢取得的文字', false, '3'));
  state.version = 3n;
  await page.clock.fastForward(20_100);
  await expect.poll(() => state.checks).toBeGreaterThan(checksBeforePoll);
  await expect(page.getByText('輪詢取得的文字')).toBeVisible();
  releaseNegotiate();

  await page.getByRole('textbox', { name: '要送出的文字' }).fill('Hub 中斷期間新增的文字');
  await page.getByRole('button', { name: '送出' }).click();
  await expect(page.getByText('Hub 中斷期間新增的文字')).toBeVisible();
  await expect(page.getByText('連線中斷')).toBeVisible();
  expect(state.adds).toBe(1);
  expect(state.subscriptions).toBe(0);
});

test('a foreground snapshot during initial negotiation keeps the connecting status', async ({ page }) => {
  await page.clock.install();
  const state = await mockApi(page);
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  state.beforeNegotiate = () => held;
  state.allowSnapshotBeforeSubscribe = true;
  await page.goto('/r/ABC123');
  await expect(page.getByText('連線中', { exact: true })).toBeVisible();
  await page.clock.fastForward(20_100);
  await expect(page.locator('.message-text')).toHaveText(original);
  expect(state.subscriptions).toBe(0);
  await expect(page.getByText('連線中', { exact: true })).toBeVisible();
  await expect(page.getByText('連線中斷', { exact: true })).toHaveCount(0);
  release();
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
});

test('negotiate 429 backs off even on manual refresh and REST reads and writes continue', async ({ page }) => {
  await page.clock.install();
  await page.addInitScript(() => { Math.random = () => 0.99; });
  const state = await mockApi(page, false);
  state.negotiateStatus = 429;
  await page.goto('/r/ABC123');
  await expect(page.getByText('連線中斷')).toBeVisible();
  await expect(page.locator('.message-text')).toHaveText(original);
  expect(state.negotiations).toBe(1);

  const readsBeforeRefresh = state.reads;
  await page.getByRole('button', { name: '重新整理' }).click();
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await expect.poll(() => state.reads).toBeGreaterThan(readsBeforeRefresh);
  expect(state.negotiations).toBe(1);
  await page.clock.runFor(1_050);
  await expect.poll(() => state.negotiations).toBe(2);
  await page.getByRole('textbox', { name: '要送出的文字' }).fill('透過 REST 寫入');
  await page.getByRole('button', { name: '送出' }).click();
  await expect(page.getByText('透過 REST 寫入')).toBeVisible();
  expect(state.negotiations).toBe(2);
  await page.clock.runFor(2_200);
  await expect.poll(() => state.negotiations).toBe(3);
  await expect(page.getByText('連線中斷')).toBeVisible();
});

for (const reconnect of [false, true]) {
  test(`negotiate 401 clears the identity and stops ${reconnect ? 'automatic reconnect' : 'initial retries'}`, async ({ page }) => {
    await page.clock.install();
    const state = await mockApi(page, reconnect);
    state.negotiateStatus = reconnect ? 200 : 401;
    await page.goto('/r/ABC123');
    if (reconnect) {
      await expect(page.getByText('已同步', { exact: true })).toBeVisible();
      state.negotiateStatus = 401;
      state.disconnect();
      await page.clock.runFor(1_100);
    }
    await expect(page.getByText('這個分頁的身分已失效')).toBeVisible();
    const negotiations = reconnect ? 2 : 1;
    expect(state.negotiations).toBe(negotiations);
    await page.clock.fastForward(60_000);
    expect(state.negotiations).toBe(negotiations);
    expect(state.joins).toBe(1);
  });
}

test('a write succeeds while a snapshot request is in flight', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  state.beforeRead = () => held;
  state.version++;
  state.notify(state.version);
  await expect.poll(() => state.reads).toBe(2);
  await expect(page.getByText('同步中', { exact: true })).toBeVisible();

  await page.getByRole('textbox', { name: '要送出的文字' }).fill('同步中送出的文字');
  const send = page.getByRole('button', { name: '送出' });
  await expect(send).toBeEnabled();
  await send.click();
  await expect.poll(() => state.adds).toBe(1);
  expect(state.reads).toBe(2);
  release();
  await expect(page.getByText('同步中送出的文字')).toBeVisible();
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
});

test('after snapshot retries are exhausted, the room can still add and delete items', async ({ page }) => {
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();

  async function enterStale() {
    const previousReads = state.reads;
    const previousFailures = state.lateResponses;
    state.beforeRead = async () => 503;
    state.version++;
    state.notify(state.version);
    await expect.poll(() => state.lateResponses).toBe(previousFailures + 3);
    expect(state.reads).toBe(previousReads + 3);
    await expect(page.getByText('同步未完成')).toBeVisible();
    state.beforeRead = undefined;
  }

  await enterStale();
  await page.getByRole('textbox', { name: '要送出的文字' }).fill('同步失敗期間新增的文字');
  await page.getByRole('button', { name: '送出' }).click();
  await expect(page.getByText('同步失敗期間新增的文字')).toBeVisible();
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();

  await enterStale();
  await page.getByRole('button', { name: '刪除', exact: true }).click();
  await page.getByRole('dialog').getByRole('button', { name: '刪除文字' }).click();
  await expect(page.getByText('同步失敗期間新增的文字')).toHaveCount(0);
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
});

test('the POST response scrolls to an already displayed write and later remote hints preserve position', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 500 });
  const state = await mockApi(page);
  state.items = Array.from({ length: 20 }, (_, index) => makeItem(`existing-${index}`, `先前的文字 ${index + 1}`, false, '1'));
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  const chat = page.locator('.chat-scroll');
  await chat.evaluate(element => { element.scrollTop = 0; });
  expect(await chat.evaluate(element => element.scrollHeight - element.clientHeight)).toBeGreaterThan(100);

  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  state.beforeAddResponse = version => {
    state.notify(version);
    return held;
  };
  await page.getByRole('textbox', { name: '要送出的文字' }).fill('快照先顯示的文字');
  await page.getByRole('button', { name: '送出' }).click();
  await expect(page.getByText('快照先顯示的文字')).toHaveCount(1);
  expect(await chat.evaluate(element => element.scrollTop)).toBe(0);
  await expect(page.getByRole('button', { name: '送出' })).toBeDisabled();
  release();
  await expect.poll(() => chat.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop))
    .toBeLessThan(2);

  await chat.evaluate(element => { element.scrollTop = 0; });
  const remoteVersion = ++state.version;
  state.items.unshift(makeItem('remote', '其他人後續新增的文字', false, String(remoteVersion)));
  state.notify(remoteVersion);
  await expect(page.getByText('其他人後續新增的文字')).toHaveCount(1);
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  expect(await chat.evaluate(element => element.scrollTop)).toBe(0);
});

test('new messages follow only at the bottom and Jump to Latest clears the count', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 500 });
  const state = await mockApi(page);
  state.version = 20n;
  state.items = Array.from({ length: 20 }, (_, index) => makeItem(String(20 - index), `先前的文字 ${20 - index}`, false));
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  const chat = page.locator('.chat-scroll');
  const jump = page.getByRole('button', { name: /跳到最新/ });
  const bottomDistance = () => chat.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop);
  const scrollTop = () => chat.evaluate(element => element.scrollTop);
  function addRemote(content: string) {
    const version = String(++state.version);
    state.items.unshift(makeItem(version, content, false));
  }

  expect(await bottomDistance()).toBeLessThanOrEqual(2);
  addRemote('在底部時新增');
  state.notify(state.version);
  await expect(page.getByText('在底部時新增')).toBeVisible();
  expect(await bottomDistance()).toBeLessThanOrEqual(2);

  await chat.evaluate(element => { element.scrollTop = element.scrollHeight - element.clientHeight - 40; });
  await expect(jump).toHaveCount(0);
  const readingTop = await scrollTop();
  addRemote('往上閱讀時新增');
  state.notify(state.version);
  await expect(jump).toHaveAccessibleName('跳到最新，1 則新訊息');
  expect(await scrollTop()).toBe(readingTop);

  state.disconnect();
  await expect(page.getByText('連線中斷')).toBeVisible();
  addRemote('斷線期間新增');
  await expect.poll(() => state.subscriptions).toBe(2);
  await expect(jump).toHaveAccessibleName('跳到最新，2 則新訊息');
  expect(await scrollTop()).toBe(readingTop);

  await jump.click();
  await expect.poll(bottomDistance).toBeLessThanOrEqual(2);
  await expect(jump).toHaveCount(0);
  await expect(chat).toBeFocused();
  await page.setViewportSize({ width: 360, height: 470 });
  await expect.poll(bottomDistance).toBeLessThanOrEqual(2);

  await chat.evaluate(element => { element.scrollTop = 0; });
  await expect(jump).toHaveAccessibleName('跳到最新');
});

test('a snapshot arriving during a smooth jump retargets the jump to the new bottom', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 500 });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  const state = await mockApi(page);
  state.version = 40n;
  state.items = Array.from({ length: 40 }, (_, index) => makeItem(String(40 - index), `舊訊息 ${40 - index}`, false));
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  const chat = page.locator('.chat-scroll');
  const jump = page.getByRole('button', { name: /跳到最新/ });
  await chat.evaluate(element => { element.scrollTop = 0; });
  await expect(jump).toBeVisible();

  let release!: () => void;
  const held = new Promise<void>(resolve => { release = resolve; });
  state.beforeRead = () => held;
  state.items.unshift(makeItem('41', '平滑捲動中抵達的文字', false));
  state.version = 41n;
  state.notify(state.version);
  await expect.poll(() => state.reads).toBe(2);
  await page.exposeFunction('releaseJumpSnapshot', release);
  await chat.evaluate(element => {
    element.addEventListener('scroll', () => {
      const scrollWindow: Window & { releaseJumpSnapshot?: () => Promise<void> } = window;
      if (element.scrollTop > 0 && element.scrollTop < element.scrollHeight - element.clientHeight) {
        void scrollWindow.releaseJumpSnapshot?.();
      }
    }, { once: true });
  });
  await jump.click();
  await expect(page.getByText('平滑捲動中抵達的文字')).toBeVisible();
  await expect.poll(() => chat.evaluate(element => element.scrollHeight - element.clientHeight - element.scrollTop))
    .toBeLessThanOrEqual(2);
  await expect(jump).toHaveCount(0);
});

test('keyboard scrolling interrupts a smooth jump and preserves the reading position on the next snapshot', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 500 });
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  const state = await mockApi(page);
  state.version = 40n;
  state.items = Array.from({ length: 40 }, (_, index) => makeItem(String(40 - index), `舊訊息 ${40 - index}`, false));
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  const chat = page.locator('.chat-scroll');
  const jump = page.getByRole('button', { name: /跳到最新/ });
  await chat.evaluate(element => { element.scrollTop = 0; });
  await expect(jump).toBeVisible();

  let interrupted = false;
  await page.exposeFunction('interruptSmoothJump', async () => {
    await page.keyboard.press('Home');
    interrupted = true;
  });
  await chat.evaluate(element => {
    const scrollWindow: Window & { interruptSmoothJump?: () => Promise<void> } = window;
    function interruptDuringMotion() {
      const remaining = element.scrollHeight - element.clientHeight - element.scrollTop;
      if (element.scrollTop <= 0 || remaining <= 80) return;
      element.removeEventListener('scroll', interruptDuringMotion);
      void scrollWindow.interruptSmoothJump?.();
    }
    element.addEventListener('scroll', interruptDuringMotion);
  });
  await jump.click();
  await expect.poll(() => interrupted).toBe(true);
  await expect.poll(() => chat.evaluate(element => element.scrollTop)).toBe(0);

  state.items.unshift(makeItem('41', '中斷捲動後收到的文字', false));
  state.version = 41n;
  state.notify(state.version);
  await expect(page.getByText('中斷捲動後收到的文字')).toBeVisible();
  await expect(jump).toHaveAccessibleName('跳到最新，1 則新訊息');
  expect(await chat.evaluate(element => element.scrollTop)).toBe(0);
});

for (const status of [401, 404]) {
  test(`a late ${status} from an old snapshot request does not invalidate a rejoined session`, async ({ page }) => {
    const state = await mockApi(page);
    await page.goto('/r/ABC123');
    await expect(page.getByText('已同步', { exact: true })).toBeVisible();
    let release!: (status: number) => void;
    const held = new Promise<number>(resolve => { release = resolve; });
    state.beforeRead = () => held;
    state.version = 2n;
    state.notify(2n);
    await expect.poll(() => state.reads).toBe(2);

    await page.getByRole('button', { name: '更多選項' }).click();
    await page.getByRole('button', { name: '離開房間' }).click();
    await page.getByRole('dialog').getByRole('button', { name: '離開房間' }).click();
    await expect(page).toHaveURL('/');
    state.beforeRead = undefined;
    state.items = [makeItem('2', '重新加入後的文字', false)];
    await page.getByRole('textbox', { name: '六位房碼' }).fill(room.code);
    await page.getByRole('button', { name: '加入', exact: true }).click();
    await expect(page.getByText('重新加入後的文字')).toBeVisible();
    release(status);
    await expect.poll(() => state.lateResponses).toBe(1);
    await expect(page.getByText('已同步', { exact: true })).toBeVisible();
    expect(state.joins).toBe(2);
  });
}

test('foreground polling repairs a dropped hint and reconnect subscribes again', async ({ page }) => {
  await page.clock.install();
  const state = await mockApi(page);
  await page.goto('/r/ABC123');
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  await page.clock.fastForward(20_100);
  await expect.poll(() => state.checks).toBe(1);
  expect(state.reads).toBe(1);

  state.items.unshift(makeItem('2', '遺漏提示的文字', false));
  state.version = 2n;
  const changedAt = await page.evaluate(() => Date.now());
  await page.clock.fastForward(20_100);
  await expect(page.getByText('遺漏提示的文字')).toBeVisible();
  expect(await page.evaluate(() => Date.now()) - changedAt).toBeLessThan(22_000);
  state.disconnect();
  await expect(page.getByText('連線中斷')).toBeVisible();
  state.items = [];
  state.version = 3n;
  await expect.poll(() => state.subscriptions).toBe(2);
  await expect(page.getByText('房間還沒有文字')).toBeVisible();

  state.closed = true;
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await expect(page.getByText('這個房間已關閉或到期')).toBeVisible();
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
    await page.getByRole('button', { name: '複製', exact: true }).focus();
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
  await page.getByRole('button', { name: '更多選項' }).click();
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
