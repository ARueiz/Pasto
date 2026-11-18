export interface Identity {
  roomId: string;
  participantId: string;
  isCreator: boolean;
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
}

export interface Snapshot {
  items: Item[];
  itemCount: number;
  snapshotVersion: string;
  expiresAt: string;
}

export class ApiError extends Error {
  constructor(readonly status: number, readonly code: string, readonly retryAfter: number) {
    super(code);
  }
}

async function request<T>(path: string, signal: AbortSignal, method = 'GET', credential?: string, body?: object): Promise<T> {
  const headers = new Headers({ Accept: 'application/json' });
  if (credential) headers.set('Authorization', `Bearer ${credential}`);
  if (body) headers.set('Content-Type', 'application/json');
  const response = await fetch(path, { method, headers, signal, body: body && JSON.stringify(body) });
  if (!response.ok) {
    const problem: { code?: string } | null = await response.json().catch(() => null);
    const retry = response.headers.get('Retry-After');
    const seconds = retry && /^\d+$/.test(retry) ? Number(retry) : 60;
    throw new ApiError(response.status, problem?.code ?? 'RequestFailed', Math.max(1, seconds));
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>;
}

const roomPath = (session: RoomSession) => `/api/rooms/${encodeURIComponent(session.roomId)}`;

export const api = {
  create: (signal: AbortSignal) => request<RoomSession>('/api/rooms', signal, 'POST'),
  join: (code: string, signal: AbortSignal) => request<RoomSession>('/api/rooms/join', signal, 'POST', undefined, { code }),
  me: (session: RoomSession, signal: AbortSignal) => request<Identity>(`${roomPath(session)}/me`, signal, 'GET', session.credential),
  snapshot: (session: RoomSession, signal: AbortSignal) => request<Snapshot>(`${roomPath(session)}/items`, signal, 'GET', session.credential),
  add: (session: RoomSession, content: string, signal: AbortSignal) =>
    request<unknown>(`${roomPath(session)}/items`, signal, 'POST', session.credential, { content }),
  delete: (session: RoomSession, id: string, signal: AbortSignal) =>
    request<unknown>(`${roomPath(session)}/items/${encodeURIComponent(id)}`, signal, 'DELETE', session.credential),
  close: (session: RoomSession, signal: AbortSignal) => request<void>(roomPath(session), signal, 'DELETE', session.credential),
};
