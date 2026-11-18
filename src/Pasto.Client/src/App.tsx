import { useCallback, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { ApiError, api } from './api';
import type { RoomSession } from './api';
import { forgetSession, loadSession, readJoinInput, routeCode, saveSession } from './session';
import { Room } from './Room';
import { applyTheme, Button, ErrorNotice, Icon, Logo, readTheme, StatusNotice, ThemeOptions } from './ui';
import type { Theme } from './ui';

type Ready = { kind: 'ready'; session: RoomSession; saved: boolean };
type Gate = Ready | { kind: 'loading' | 'identity' | 'room' } | { kind: 'error'; error: unknown };
type Appearance = { theme: Theme; onTheme: (theme: Theme) => void };

export function App() {
  const [path, setPath] = useState(() => window.location.pathname);
  const [theme, setTheme] = useState(readTheme);
  const handoff = useRef<Ready | null>(null);

  useEffect(() => {
    const sync = () => setPath(window.location.pathname);
    window.addEventListener('popstate', sync);
    return () => window.removeEventListener('popstate', sync);
  }, []);
  useEffect(() => { applyTheme(theme); }, [theme]);

  function navigate(next: string, replace = false) {
    if (window.location.pathname !== next) {
      if (replace) window.history.replaceState(null, '', next);
      else window.history.pushState(null, '', next);
    }
    setPath(next);
  }

  function created(session: RoomSession) {
    handoff.current = { kind: 'ready', session, saved: saveSession(session) };
    navigate(`/r/${session.code}`);
  }

  const code = routeCode(path);
  return code ? <RoomGate key={code} code={code} handoff={handoff} theme={theme} onTheme={setTheme}
    home={() => navigate('/', true)} /> : <Landing theme={theme} onTheme={setTheme} onCreate={created}
      onJoin={code => navigate(`/r/${code}`)} invalidPath={path !== '/'} />;
}

function Landing({ theme, onTheme, onCreate, onJoin, invalidPath }: Appearance & {
  onCreate: (session: RoomSession) => void; onJoin: (code: string) => void; invalidPath: boolean;
}) {
  const [code, setCode] = useState('');
  const [creating, setCreating] = useState(false);
  const [menu, setMenu] = useState(false);
  const [error, setError] = useState<unknown>();
  const active = useRef<AbortController | null>(null);
  useEffect(() => () => active.current?.abort(), []);

  async function create() {
    if (active.current) return;
    const operation = new AbortController();
    active.current = operation;
    setCreating(true);
    setError(undefined);
    try {
      const session = await api.create(operation.signal);
      if (!operation.signal.aborted) onCreate(session);
    } catch (error) {
      if (!operation.signal.aborted) setError(error);
    } finally {
      active.current = null;
      if (!operation.signal.aborted) setCreating(false);
    }
  }

  function join(event: FormEvent) {
    event.preventDefault();
    const parsed = readJoinInput(code);
    if (parsed && !creating) onJoin(parsed);
  }

  return <div className="landing">
    <header className="landing-header"><Logo /><div className="menu-host">
      <Button aria-label="更多選項" aria-expanded={menu} onClick={() => setMenu(!menu)}><Icon name="menu" /></Button>
      {menu && <div className="room-menu" onKeyDown={event => { if (event.key === 'Escape') setMenu(false); }}>
        <ThemeOptions theme={theme} onChange={onTheme} />
      </div>}
    </div></header>
    <main className="landing-main">
      <div className="hero"><h1>在裝置之間傳文字</h1><p>用房碼或 QR Code，把文字傳到另一台裝置，不用註冊。</p></div>
      {invalidPath && <StatusNotice title="無法辨識這個邀請網址" danger>請使用六位房碼加入房間。</StatusNotice>}
      {error !== undefined && <ErrorNotice error={error} uncertain />}
      <div className="entry-grid">
        <section className="entry-card"><span className="entry-icon"><Icon name="plus" /></span><h2>建立房間</h2>
          <p>你會成為房主，可以分享房碼，也可以提前關閉房間。</p>
          <Button variant="primary" disabled={creating} onClick={() => void create()}>{creating ? '建立中…' : '建立新房間'}</Button>
        </section>
        <section className="entry-card"><span className="entry-icon"><Icon name="enter" /></span><h2>加入房間</h2>
          <p>輸入六位房碼，或貼上分享連結。</p>
          <form className="join-form" onSubmit={join}>
            <input aria-label="六位房碼" autoComplete="off" autoCapitalize="characters" spellCheck={false} placeholder="輸入房碼"
              maxLength={6} value={code} onChange={event => setCode(event.target.value.toUpperCase())}
              onPaste={event => {
                const parsed = readJoinInput(event.clipboardData.getData('text'));
                if (parsed) { event.preventDefault(); setCode(parsed); }
              }} />
            <Button variant="primary" type="submit" disabled={creating || !readJoinInput(code)}>加入</Button>
          </form>
        </section>
      </div>
      <p className="privacy">房間裡的文字任何知道房碼的人都看得到，請不要放密碼等機密資料。</p>
    </main>
  </div>;
}

function RoomGate({ code, handoff, home, theme, onTheme }: Appearance & {
  code: string; handoff: { current: Ready | null }; home: () => void;
}) {
  const [arrival] = useState(() => handoff.current?.session.code === code ? handoff.current : null);
  const [gate, setGate] = useState<Gate>(arrival ?? { kind: 'loading' });
  const [attempt, setAttempt] = useState({ number: 0, fresh: false });
  const invalidate = useCallback((kind: 'identity' | 'room') => {
    if (gate.kind !== 'ready') return;
    forgetSession(code, gate.session.roomId, kind);
    setGate({ kind });
  }, [code, gate]);

  useEffect(() => {
    if (arrival && attempt.number === 0) {
      if (handoff.current === arrival) handoff.current = null;
      return;
    }
    const controller = new AbortController();
    const { signal } = controller;
    const remembered = attempt.fresh ? {} : loadSession(code);
    let known = remembered.session;
    const finish = (next: Gate) => { if (!signal.aborted) setGate(next); };
    async function enter() {
      if (remembered.invalid) { finish({ kind: remembered.invalid }); return; }
      try {
        if (known) {
          const identity = await api.me(known, signal);
          if (signal.aborted) return;
          known = { ...known, ...identity };
        } else {
          known = await api.join(code, signal);
        }
        if (!signal.aborted) finish({ kind: 'ready', session: known, saved: saveSession(known) });
      } catch (error) {
        if (signal.aborted) return;
        if (error instanceof ApiError && (error.status === 401 && known || error.status === 404)) {
          const invalid = error.status === 401 ? 'identity' : 'room';
          forgetSession(code, known?.roomId, invalid);
          finish({ kind: invalid });
        } else finish({ kind: 'error', error });
      }
    }
    void enter();
    return () => controller.abort();
  }, [arrival, attempt, code, handoff]);

  function retry(fresh: boolean) {
    setGate({ kind: 'loading' });
    setAttempt({ number: attempt.number + 1, fresh });
  }

  if (gate.kind === 'ready') return <Room session={gate.session} saved={gate.saved} theme={theme} onTheme={onTheme}
    onLeave={() => { forgetSession(code, gate.session.roomId); home(); }}
    onInvalid={invalidate} />;

  return <div className="landing"><header className="landing-header"><Logo /></header><main className="gate">
    {gate.kind === 'loading' && <p role="status">正在開啟房間…</p>}
    {gate.kind === 'identity' && <StatusNotice title="這個分頁的身分已失效" danger alert
      action={<Button variant="primary" onClick={() => retry(true)}>以新身分加入</Button>}>
      原本的刪除權和房主權無法恢復。要用新身分重新加入 {code} 嗎？</StatusNotice>}
    {gate.kind === 'room' && <StatusNotice title="這個房間已關閉或到期" danger alert
      action={<Button onClick={home}>回首頁</Button>}>
      房間裡的文字已無法讀取。可以回到首頁建立新房間。</StatusNotice>}
    {gate.kind === 'error' && <><ErrorNotice error={gate.error} />
      <Button onClick={() => retry(attempt.fresh)}>再試一次</Button></>}
    {gate.kind !== 'room' && <Button onClick={home}>回首頁</Button>}
  </main></div>;
}
