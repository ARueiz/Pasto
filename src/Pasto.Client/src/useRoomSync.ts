import { DefaultHttpClient, HttpError, HubConnectionBuilder, HubConnectionState, LogLevel, NullLogger } from '@microsoft/signalr';
import type { HttpRequest } from '@microsoft/signalr';
import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, api, invalidRoom } from './api';
import type { RoomSession, Snapshot } from './api';

type SyncStatus = 'connecting' | 'syncing' | 'current' | 'disconnected' | 'stale';
type RoomChanged = { roomId: string; version: string };

const coalesceMs = 150;
const pollDelay = () => 15_000 + Math.random() * 5_000;
const reconnectDelay = (attempt: number) => Math.min(20_000, 1_000 * 2 ** attempt) * (0.5 + Math.random() / 2);

class RoomHubHttpClient extends DefaultHttpClient {
  constructor(private readonly onUnauthorized: () => void) {
    super(NullLogger.instance);
  }

  override async send(request: HttpRequest) {
    try {
      return await super.send(request);
    } catch (error) {
      // SignalR wraps negotiate errors before exposing them to the connection callbacks.
      if (error instanceof HttpError && error.statusCode === 401 && request.url &&
        new URL(request.url).pathname === '/hubs/room/negotiate') this.onUnauthorized();
      throw error;
    }
  }
}

export function useRoomSync(session: RoomSession, onInvalid: (kind: 'identity' | 'room') => void) {
  const [snapshot, setSnapshot] = useState<Snapshot>();
  const [status, setStatus] = useState<SyncStatus>('connecting');
  const [error, setError] = useState<unknown>();
  const controls = useRef<{ refresh: (version?: string) => void; stop: () => void }>(null);

  useEffect(() => {
    // 每次入房使用獨立生命週期，只有仍有效的工作才能更新畫面。
    const lifetime = new AbortController();
    const { signal } = lifetime;
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/room', {
        accessTokenFactory: () => session.credential,
        withCredentials: false,
        httpClient: new RoomHubHttpClient(() => invalidate('identity')),
      })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: ({ previousRetryCount }) =>
        previousRetryCount < 5 ? reconnectDelay(previousRetryCount) : null })
      .configureLogging(LogLevel.None)
      .build();
    let displayed = -1n;
    let observed = -1n;
    let subscribed = false;
    let snapshotQueued = false;
    let forceRefresh = false;
    let checkQueued = false;
    let round: AbortController | undefined;
    let roundTimer: number | undefined;
    let roundDue = Infinity;
    let retryAt = 0;
    let retries = 0;
    let pollTimer: number | undefined;
    let startTimer: number | undefined;
    let startAttempts = 0;
    let starting = false;

    function schedule(delay = coalesceMs) {
      if (signal.aborted || round || (!checkQueued && !snapshotQueued)) return;
      const now = performance.now();
      const due = Math.max(now + delay, retryAt);
      if (due >= roundDue) return;
      window.clearTimeout(roundTimer);
      roundDue = due;
      roundTimer = window.setTimeout(() => {
        roundDue = Infinity;
        void sync();
      }, due - now);
    }

    function requestSnapshot(version?: bigint, delay = coalesceMs) {
      if (signal.aborted) return;
      if (version !== undefined) {
        if (version <= displayed) return;
        if (version > observed) observed = version;
      } else forceRefresh = true;
      snapshotQueued = true;
      if (subscribed) setStatus('syncing');
      schedule(delay);
    }

    function check() {
      if (signal.aborted) return;
      checkQueued = true;
      schedule(0);
    }

    function finishIfCurrent() {
      if (displayed >= observed && !snapshotQueued && !checkQueued) {
        if (subscribed) setStatus('current');
        setError(undefined);
      }
    }

    async function sync() {
      if (signal.aborted || round || (!checkQueued && !snapshotQueued)) return;
      const operation = new AbortController();
      round = operation;
      const checking = checkQueued;
      if (checking) checkQueued = false;
      else {
        snapshotQueued = false;
        forceRefresh = false;
      }
      const expected = observed;
      try {
        if (checking) {
          const metadata = await api.metadata(session, operation.signal);
          if (signal.aborted || operation.signal.aborted) return;
          const version = BigInt(metadata.version);
          if (version > observed) observed = version;
          if (observed > displayed) requestSnapshot(observed);
        } else {
          const next = await api.snapshot(session, operation.signal);
          if (signal.aborted || operation.signal.aborted) return;
          const version = BigInt(next.snapshotVersion);
          if (version >= displayed) {
            displayed = version;
            setSnapshot(next);
          }
          if (version < displayed || version < expected) throw new Error('The snapshot is older than the expected room version.');
          snapshotQueued = forceRefresh || observed > displayed;
        }
        retries = 0;
        retryAt = 0;
        finishIfCurrent();
      } catch (error) {
        if (signal.aborted || operation.signal.aborted) return;
        const invalid = invalidRoom(error);
        if (invalid) {
          invalidate(invalid);
          return;
        }
        setError(error);
        if (subscribed) setStatus('stale');
        const throttled = error instanceof ApiError && error.status === 429;
        retryAt = performance.now() + (throttled ? error.retryAfter * 1_000 : 500 * 2 ** Math.min(retries, 2));
        if (retries++ < 2) {
          if (checking) checkQueued = true;
          else snapshotQueued = true;
        }
      } finally {
        round = undefined;
        schedule();
      }
    }

    async function subscribe() {
      await connection.invoke('SubscribeRoom', session.roomId);
      if (signal.aborted || connection.state !== HubConnectionState.Connected) return;
      subscribed = true;
      startAttempts = 0;
      requestSnapshot(undefined, 0);
    }

    function restartLater() {
      if (signal.aborted || !navigator.onLine || startTimer !== undefined) return;
      startTimer = window.setTimeout(() => {
        startTimer = undefined;
        void start();
      }, reconnectDelay(Math.min(startAttempts++, 5)));
    }

    function disconnected() {
      subscribed = false;
      if (!signal.aborted) setStatus('disconnected');
    }

    function invalidate(kind: 'identity' | 'room') {
      if (signal.aborted) return;
      stop();
      onInvalid(kind);
    }

    async function recoverSubscription() {
      if (signal.aborted) return;
      disconnected();
      if (displayed < 0n) requestSnapshot(undefined, 0);
      else check();
      await connection.stop();
      restartLater();
    }

    async function start() {
      if (signal.aborted || starting || startTimer !== undefined || !navigator.onLine ||
        connection.state !== HubConnectionState.Disconnected) return;
      starting = true;
      try {
        await connection.start();
        if (!signal.aborted) await subscribe();
      } catch {
        await recoverSubscription();
      } finally {
        starting = false;
      }
    }

    function schedulePoll() {
      window.clearTimeout(pollTimer);
      if (signal.aborted || document.hidden) return;
      pollTimer = window.setTimeout(() => {
        if (navigator.onLine) check();
        schedulePoll();
      }, pollDelay());
    }

    function foreground() {
      if (document.hidden) {
        window.clearTimeout(pollTimer);
        return;
      }
      if (navigator.onLine) {
        check();
        void start();
      }
      schedulePoll();
    }

    function offline() {
      disconnected();
      void connection.stop();
    }

    function stop() {
      lifetime.abort();
      round?.abort();
      window.clearTimeout(roundTimer);
      window.clearTimeout(startTimer);
      window.clearTimeout(pollTimer);
      document.removeEventListener('visibilitychange', foreground);
      window.removeEventListener('online', foreground);
      window.removeEventListener('offline', offline);
      void connection.stop();
    }

    connection.on('RoomChanged', (change: RoomChanged | undefined) => {
      if (change?.roomId !== session.roomId || !/^\d{1,19}$/.test(change.version)) return;
      const version = BigInt(change.version);
      if (version > observed) requestSnapshot(version);
    });
    connection.onreconnecting(disconnected);
    connection.onreconnected(() => { void subscribe().catch(recoverSubscription); });
    connection.onclose(() => {
      if (signal.aborted) return;
      disconnected();
      if (navigator.onLine) check();
      restartLater();
    });

    controls.current = {
      refresh: version => {
        requestSnapshot(version === undefined ? undefined : BigInt(version), 0);
        if (!subscribed) void start();
      },
      stop,
    };
    document.addEventListener('visibilitychange', foreground);
    window.addEventListener('online', foreground);
    window.addEventListener('offline', offline);
    void start();
    schedulePoll();
    return () => { controls.current = null; stop(); };
  }, [session, onInvalid]);

  const refresh = useCallback((version?: string) => controls.current?.refresh(version), []);
  const stop = useCallback(() => controls.current?.stop(), []);
  return { snapshot, status, error, refresh, stop };
}
