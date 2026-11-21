import { expect, test } from '@playwright/test';
import type { Page, WebSocketRoute } from '@playwright/test';

const room = {
  roomId: '88888888-8888-4888-8888-888888888888', code: 'ABC123', credential: 'test-credential',
  participantId: '99999999-9999-4999-8999-999999999999', isCreator: false, authorCode: '368F1',
};
const roomPath = `/api/rooms/${room.roomId}`;
const afterCapacityClose = 'Connection closed with an error. HubException: HubConnectionLimitReached';
type Mode = 'ready' | 'capacity-http' | 'capacity-close' | 'websocket-blocked' | 'subscription-throttled' | 'subscription-error';

async function mockRoom(page: Page, mode: Mode) {
  let socket: WebSocketRoute | undefined;
  const state = {
    mode, negotiations: 0, sockets: 0, checks: 0, subscriptions: 0, latest: 1,
    disconnect: () => socket?.close({ code: 1011 }),
  };
  await page.route('**/hubs/room/negotiate*', async route => {
    state.negotiations++;
    if (state.mode === 'capacity-http') {
      await route.fulfill({
        status: 429, headers: { 'Retry-After': '30' },
        json: { code: 'HubConnectionLimitReached', retryAfterSeconds: 30 },
      });
      return;
    }
    await route.fulfill({ json: {
      connectionId: 'connection', connectionToken: 'token', negotiateVersion: 1,
      availableTransports: [{ transport: 'WebSockets', transferFormats: ['Text'] }],
    } });
  });
  await page.route('**/hubs/room?*', route => {
    throw Error(`Unexpected HTTP fallback transport: ${route.request().method()}`);
  });
  await page.routeWebSocket(/\/hubs\/room\?/, ws => {
    socket = ws;
    state.sockets++;
    if (state.mode === 'websocket-blocked') {
      ws.close({ code: 1011 });
      return;
    }
    ws.onMessage(payload => {
      for (const frame of payload.toString().split('\x1e').filter(Boolean)) {
        const request = JSON.parse(frame);
        if (request.protocol === 'json') {
          const close = state.mode === 'capacity-close'
            ? JSON.stringify({ type: 7, error: afterCapacityClose }) + '\x1e'
            : '';
          ws.send('{}\x1e' + close);
        }
        if (request.target === 'SubscribeRoom' && state.mode !== 'capacity-close') {
          state.subscriptions++;
          const code = state.mode === 'subscription-throttled' ? 'SubscribeRateLimited' :
            state.mode === 'subscription-error' ? 'RoomUnavailable' : undefined;
          const error = code ? `Failed to invoke 'SubscribeRoom' due to an error on the server. HubException: ${code}` : undefined;
          ws.send(JSON.stringify({ type: 3, invocationId: request.invocationId, error }) + '\x1e');
        }
      }
    });
  });
  await page.route(url => url.pathname.startsWith('/api/rooms'), async route => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/api/rooms/join' || path === `${roomPath}/me`) return route.fulfill({ json: room });
    if (path === roomPath) {
      state.checks++;
      return route.fulfill({ json: { version: String(state.latest) } });
    }
    if (path === `${roomPath}/items`) return route.fulfill({ json: {
      items: [{
        id: String(state.latest), content: `rest-${state.latest}`, isOwn: false, canDelete: false,
        createdAt: '2026-11-21T08:00:00Z', authorCode: room.authorCode, authorIsCreator: false,
        createdVersion: String(state.latest),
      }],
      itemCount: 1, snapshotVersion: String(state.latest), expiresAt: '2099-01-01T00:00:00Z',
    } });
    throw Error(`Unexpected API request: ${path}`);
  });
  return state;
}

for (const mode of ['capacity-http', 'capacity-close'] as const) {
  test(`${mode} defers Hub retries while REST polling continues`, async ({ page }) => {
    await page.clock.install();
    await page.addInitScript(() => { Math.random = () => 0.5; });
    const state = await mockRoom(page, mode);
    await page.goto('/r/ABC123');
    await expect(page.getByText('連線中斷')).toBeVisible();
    await expect(page.getByText('rest-1', { exact: true })).toBeVisible();
    expect(state.negotiations).toBe(1);
    expect(state.sockets).toBe(mode === 'capacity-close' ? 1 : 0);

    state.latest = 2;
    const beforePoll = state.checks;
    await page.clock.runFor(20_000);
    await expect(page.getByText('rest-2', { exact: true })).toBeVisible();
    expect(state.checks).toBeGreaterThan(beforePoll);
    await page.getByRole('button', { name: '重新整理' }).click();
    await page.evaluate(() => window.dispatchEvent(new Event('online')));
    await page.clock.runFor(10_500);
    expect(state.negotiations).toBe(1);

    state.mode = 'ready';
    await page.clock.runFor(2_500);
    await expect(page.getByText('已同步', { exact: true })).toBeVisible();
    expect(state.negotiations).toBe(2);
    expect(state.subscriptions).toBe(1);
  });
}

test('SignalR Close after initial transport startup does not enter built-in reconnect', async ({ page }) => {
  await mockRoom(page, 'capacity-close');
  await page.goto('/');
  await page.addScriptTag({ path: 'node_modules/@microsoft/signalr/dist/browser/signalr.js' });
  const result = await page.evaluate(async () => {
    const signalR = (window as Window & {
      signalR?: typeof import('@microsoft/signalr');
    }).signalR;
    if (!signalR) throw Error('SignalR browser script did not load.');
    const { HubConnectionBuilder, HttpTransportType } = signalR;
    let polls = 0;
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/room', { transport: HttpTransportType.WebSockets })
      .configureLogging(signalR.LogLevel.None)
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: () => { polls++; return 0; } })
      .build();
    const failure = await new Promise<unknown>(resolve => {
      connection.onclose(resolve);
      void connection.start().catch(resolve);
    });
    await connection.stop();
    return { message: failure instanceof Error ? failure.message : '', polls };
  });
  expect(result.message).toContain(afterCapacityClose);
  expect(result.polls).toBe(0);
});

test('WebSocket failure after negotiate leaves REST polling active', async ({ page }) => {
  await page.clock.install();
  const state = await mockRoom(page, 'websocket-blocked');
  await page.goto('/r/ABC123');
  await expect(page.getByText('連線中斷')).toBeVisible();
  await expect(page.getByText('rest-1', { exact: true })).toBeVisible();
  expect(state.sockets).toBeGreaterThan(0);
  state.latest = 2;
  await page.clock.fastForward(20_100);
  await expect(page.getByText('rest-2', { exact: true })).toBeVisible();
  expect(state.subscriptions).toBe(0);
});

test('SubscribeRateLimited retries on the same socket after one token refills', async ({ page }) => {
  await page.clock.install();
  const state = await mockRoom(page, 'subscription-throttled');
  await page.goto('/r/ABC123');
  await expect(page.getByText('rest-1', { exact: true })).toBeVisible();
  expect(state.subscriptions).toBe(1);
  await page.clock.runFor(5_000);
  expect(state.subscriptions).toBe(1);
  state.mode = 'ready';
  await page.clock.runFor(1_250);
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  expect(state.subscriptions).toBe(2);
  expect(state.negotiations).toBe(1);
  expect(state.sockets).toBe(1);
});

test('a different subscription error restarts the connection', async ({ page }) => {
  await page.clock.install();
  const state = await mockRoom(page, 'subscription-error');
  await page.goto('/r/ABC123');
  await expect(page.getByText('rest-1', { exact: true })).toBeVisible();
  state.mode = 'ready';
  await page.clock.runFor(1_100);
  await expect(page.getByText('已同步', { exact: true })).toBeVisible();
  expect(state.negotiations).toBe(2);
  expect(state.sockets).toBe(2);
});
