export interface Identity {
  roomId: string;
  participantId: string;
  isCreator: boolean;
  authorCode: string;
}

export interface RoomSession extends Identity {
  code: string;
  credential: string;
}

export interface Item {
  id: string;
  content: string;
  createdAt: string;
  createdVersion: string;
  isOwn: boolean;
  canDelete: boolean;
  authorCode: string;
  authorIsCreator: boolean;
}

export interface Snapshot {
  items: Item[];
  itemCount: number;
  snapshotVersion: string;
  expiresAt: string;
}

interface RoomMetadata {
  version: string;
}

interface CommittedChange {
  committedVersion: string;
}

export class ApiError extends Error {
  readonly retryAt: number;

  constructor(readonly status: number, readonly code: string, readonly retryAfter: number) {
    super(code);
    this.retryAt = performance.now() + retryAfter * 1_000;
  }
}

export function isOverloaded(error: unknown): error is ApiError {
  return error instanceof ApiError && error.status === 503 && error.code === 'Overloaded';
}

function waitForRetry(delay: number, signal: AbortSignal) {
  signal.throwIfAborted();
  return new Promise<void>((resolve, reject) => {
    const timer = window.setTimeout(() => {
      signal.removeEventListener('abort', cancel);
      resolve();
    }, delay);
    function cancel() {
      window.clearTimeout(timer);
      reject(signal.reason);
    }
    signal.addEventListener('abort', cancel, { once: true });
  });
}

async function request<T>(path: string, signal: AbortSignal, method = 'GET', credential?: string, body?: object): Promise<T> {
  const headers = new Headers({ Accept: 'application/json' });
  if (credential) headers.set('Authorization', `Bearer ${credential}`);
  if (body) headers.set('Content-Type', 'application/json');
  const deadline = AbortSignal.any([signal, AbortSignal.timeout(10_000)]);
  const response = await fetch(path, { method, headers, signal: deadline, body: body && JSON.stringify(body) });
  if (!response.ok) {
    const problem: { code?: string } | null = await response.json().catch(() => null);
    const retry = response.headers.get('Retry-After');
    const delta = retry && /^\d+$/.test(retry) ? Number(retry) : NaN;
    const seconds = Number.isSafeInteger(delta) && delta <= 2_147_483 ? delta : 60;
    throw new ApiError(response.status, problem?.code ?? 'RequestFailed', Math.max(1, seconds));
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

async function postWithAdmissionRetry<T>(path: string, signal: AbortSignal, credential?: string, body?: object) {
  for (let retries = 0; ; retries++) {
    try {
      return await request<T>(path, signal, 'POST', credential, body);
    } catch (error) {
      if (!isOverloaded(error) || retries >= 3) throw error;
      const delay = error.retryAt - performance.now() + Math.random() * 500;
      await waitForRetry(Math.max(0, delay), signal);
    }
  }
}

const roomPath = (session: Pick<RoomSession, 'roomId'>) => `/api/rooms/${encodeURIComponent(session.roomId)}`;

export const api = {
  create: (signal: AbortSignal) => postWithAdmissionRetry<RoomSession>('/api/rooms', signal),
  join: (code: string, signal: AbortSignal) => postWithAdmissionRetry<RoomSession>('/api/rooms/join', signal, undefined, { code }),
  me: (session: Pick<RoomSession, 'roomId' | 'credential'>, signal: AbortSignal) =>
    request<Identity>(`${roomPath(session)}/me`, signal, 'GET', session.credential),
  metadata: (session: RoomSession, signal: AbortSignal) => request<RoomMetadata>(roomPath(session), signal, 'GET', session.credential),
  snapshot: (session: RoomSession, signal: AbortSignal) => request<Snapshot>(`${roomPath(session)}/items`, signal, 'GET', session.credential),
  add: (session: RoomSession, content: string, signal: AbortSignal) =>
    postWithAdmissionRetry<CommittedChange>(`${roomPath(session)}/items`, signal, session.credential, { content }),
  delete: (session: RoomSession, id: string, signal: AbortSignal) =>
    request<CommittedChange>(`${roomPath(session)}/items/${encodeURIComponent(id)}`, signal, 'DELETE', session.credential),
  close: (session: RoomSession, signal: AbortSignal) => request<void>(roomPath(session), signal, 'DELETE', session.credential),
};

export function invalidRoom(error: unknown) {
  if (error instanceof ApiError && error.status === 401) return 'identity';
  if (error instanceof ApiError && error.status === 404) return 'room';
}
