import type { RoomSession } from './api';

type Invalid = 'identity' | 'room';
type StoredRoomSession = Omit<RoomSession, 'authorCode'> & { authorCode?: string };
const prefix = 'pasto:room:';
const codeKey = (code: string) => `${prefix}code:${code}`;
const credentialKey = (id: string) => `${prefix}credential:${id}`;
const isUuid = (value: string) => /^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(value);

export function loadSession(code: string): { session?: StoredRoomSession; invalid?: Invalid } {
  try {
    const id = sessionStorage.getItem(codeKey(code));
    if (!id) return {};
    if (id === 'identity' || id === 'room') return { invalid: id };
    if (!isUuid(id)) return { invalid: 'identity' };
    const stored = sessionStorage.getItem(credentialKey(id));
    if (!stored) return { invalid: 'identity' };
    let session: StoredRoomSession;
    try { session = JSON.parse(stored) as StoredRoomSession; }
    catch { return { invalid: 'identity' }; }
    return session && session.roomId === id && session.code === code && typeof session.credential === 'string' && session.credential
      ? { session } : { invalid: 'identity' };
  } catch {
    return {};
  }
}

export function saveSession(session: RoomSession): boolean {
  try {
    sessionStorage.setItem(credentialKey(session.roomId), JSON.stringify(session));
    sessionStorage.setItem(codeKey(session.code), session.roomId);
    return true;
  } catch {
    forgetSession(session.code, session.roomId);
    return false;
  }
}

export function forgetSession(code: string, id?: string, invalid?: Invalid) {
  try {
    if (id) sessionStorage.removeItem(credentialKey(id));
    if (invalid) sessionStorage.setItem(codeKey(code), invalid);
    else sessionStorage.removeItem(codeKey(code));
  } catch {
    // Storage may be disabled. The caller also drops the in-memory session.
  }
}

export const isRoomCode = (value: string) => /^[A-Z0-9]{6}$/.test(value);

export function routeCode(pathname: string): string | null {
  const match = /^\/r\/([A-Za-z0-9]{6})\/?$/.exec(pathname);
  return match?.[1].toUpperCase() ?? null;
}

export function readJoinInput(input: string): string | null {
  const code = input.trim().toUpperCase();
  if (isRoomCode(code)) return code;
  try {
    const url = new URL(input.trim(), window.location.origin);
    return (url.protocol === 'http:' || url.protocol === 'https:') ? routeCode(url.pathname) : null;
  } catch {
    return null;
  }
}
