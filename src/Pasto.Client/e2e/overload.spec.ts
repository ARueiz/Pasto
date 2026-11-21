import { expect, test } from '@playwright/test';
import type { Page, Route } from '@playwright/test';

const room = {
  roomId: '88888888-8888-4888-8888-888888888888', code: 'ABC123', credential: 'test-credential',
  participantId: '99999999-9999-4999-8999-999999999999', isCreator: true, authorCode: '368F1',
};
const roomPath = `/api/rooms/${room.roomId}`;
const overload = (route: Route) => route.fulfill({
  status: 503, headers: { 'Retry-After': '2' }, json: { code: 'Overloaded' },
});

async function clock(page: Page) {
  await page.clock.install();
  await page.addInitScript(() => { Math.random = () => 0.5; });
}

async function roomResponses(page: Page) {
  await page.route('**/hubs/room/negotiate*', route => route.fulfill({ status: 403 }));
  await page.route(`**${roomPath}`, route => route.fulfill({ json: { version: '0' } }));
  await page.route(`**${roomPath}/items`, route => route.fulfill({ json: {
    items: [], itemCount: 0, snapshotVersion: '0', expiresAt: '2099-01-01T00:00:00Z',
  } }));
}

test('creation retries an Overloaded response up to three times with Retry-After and jitter', async ({ page }) => {
  await clock(page);
  await roomResponses(page);
  let requests = 0;
  await page.route('**/api/rooms', route => ++requests <= 3
    ? overload(route)
    : route.fulfill({ status: 201, json: room }));
  await page.goto('/');
  await page.getByRole('button', { name: '建立新房間' }).click();
  await expect.poll(() => requests).toBe(1);
  await page.clock.runFor(2_050);
  expect(requests).toBe(1);
  await expect(page).toHaveURL(/\/$/);
  await expect(page.getByText('伺服器忙碌')).toHaveCount(0);
  await page.clock.runFor(250);
  await expect.poll(() => requests).toBe(2);
  await page.clock.runFor(2_300);
  await expect.poll(() => requests).toBe(3);
  await page.clock.runFor(2_300);
  await expect(page).toHaveURL(/\/r\/ABC123$/);
  expect(requests).toBe(4);
});

test('join shows busy only after three retries and gates a manual retry', async ({ page }) => {
  await clock(page);
  await roomResponses(page);
  let requests = 0;
  await page.route('**/api/rooms/join', route => ++requests <= 4
    ? overload(route)
    : route.fulfill({ status: 201, json: room }));
  await page.goto('/r/ABC123');
  for (let expected = 1; expected < 4; expected++) {
    await expect.poll(() => requests).toBe(expected);
    await expect(page.getByText('伺服器忙碌')).toHaveCount(0);
    await page.clock.runFor(2_300);
  }
  await expect(page.getByText('伺服器忙碌')).toBeVisible();
  const retry = page.getByRole('button', { name: '再試一次' });
  await expect(retry).toBeDisabled();
  await page.clock.runFor(2_100);
  await expect(retry).toBeEnabled();
  expect(requests).toBe(4);
  await retry.click();
  await expect(page.getByRole('textbox', { name: '要送出的文字' })).toBeVisible();
  expect(requests).toBe(5);
});

for (const code of ['Overloaded', 'ServiceUnavailable']) {
  test(`add handles ${code} and keeps the draft until a write succeeds`, async ({ page }) => {
    await clock(page);
    await roomResponses(page);
    await page.route('**/api/rooms/join', route => route.fulfill({ status: 201, json: room }));
    let writes = 0;
    const draft = '尚未送出的文字';
    await page.route(`**${roomPath}/items`, route => {
      if (route.request().method() !== 'POST') return route.fallback();
      expect(route.request().postDataJSON()).toEqual({ content: draft });
      writes++;
      if (code === 'ServiceUnavailable')
        return route.fulfill({ status: 503, headers: { 'Retry-After': '2' }, json: { code } });
      return writes === 1 ? overload(route) : route.fulfill({ status: 201, json: { committedVersion: '1' } });
    });
    await page.goto('/r/ABC123');
    const input = page.getByRole('textbox', { name: '要送出的文字' });
    await input.fill(draft);
    await page.getByRole('button', { name: '送出' }).click();
    await expect.poll(() => writes).toBe(1);
    await expect(input).toHaveValue(draft);
    if (code === 'ServiceUnavailable') {
      await expect(page.getByText('伺服器忙碌')).toBeVisible();
      await expect(page.getByText(/無法確認操作結果/)).toBeVisible();
      await page.clock.runFor(7_000);
      expect(writes).toBe(1);
      await expect(input).toHaveValue(draft);
      return;
    }
    await expect(page.getByText('伺服器忙碌')).toHaveCount(0);
    await page.clock.runFor(2_300);
    await expect(input).toHaveValue('');
    expect(writes).toBe(2);
  });
}

test('leaving the Join screen cancels an overload retry', async ({ page }) => {
  await clock(page);
  let requests = 0;
  await page.route('**/api/rooms/join', route => {
    requests++;
    return overload(route);
  });
  await page.goto('/r/ABC123');
  await expect.poll(() => requests).toBe(1);
  await page.getByRole('button', { name: '回首頁' }).click();
  await page.clock.runFor(6_000);
  expect(requests).toBe(1);
});

test('the browser API retries a burst of thirty joins through an eight-slot gate', async ({ page }) => {
  await clock(page);
  let requests = 0;
  let accepted = 0;
  let available = 8;
  await page.route('**/api/rooms/join', route => {
    requests++;
    if (available-- <= 0) return overload(route);
    return route.fulfill({ status: 201, json: { ...room, participantId: String(++accepted) } });
  });
  await page.goto('/');
  const joining = page.evaluate(async () => {
    const source = '/src/api.ts';
    const { api } = await import(source) as typeof import('../src/api');
    const results = await Promise.all(Array.from({ length: 30 }, () => api.join('ABC123', new AbortController().signal)));
    return results.map(result => result.participantId);
  });

  for (const [count, successes] of [[30, 8], [52, 16], [66, 24], [72, 30]]) {
    await expect.poll(() => requests).toBe(count);
    expect(accepted).toBe(successes);
    if (successes < 30) {
      available = 8;
      await page.clock.runFor(2_300);
    }
  }
  expect(new Set(await joining).size).toBe(30);
});
