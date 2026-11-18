import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { QRCodeSVG } from 'qrcode.react';
import { ApiError, api } from './api';
import type { Item, RoomSession, Snapshot } from './api';
import { Button, ConfirmDialog, CopyFallback, ErrorNotice, Icon, Logo, StatusNotice, ThemeOptions } from './ui';
import type { Theme } from './ui';

const maxBytes = 8192;
const byteCount = new TextEncoder();
const countFormat = new Intl.NumberFormat('zh-TW');
const timeFormat = new Intl.DateTimeFormat('zh-TW', { hour: '2-digit', minute: '2-digit', hour12: false });
const dateFormat = new Intl.DateTimeFormat('zh-TW', { year: 'numeric', month: 'long', day: 'numeric' });
const noItems: Item[] = [];

type Confirmation = { kind: 'leave' | 'close' } | { kind: 'delete'; id: string };
const confirmationText = {
  leave: ['離開房間？', '這個分頁會清除身分與已載入的文字。房間和其他裝置不受影響。', '離開房間'],
  close: ['關閉房間？', '關閉後，所有人都無法再讀取此房間。', '關閉房間'],
  delete: ['刪除這則文字？', '刪除後，房間裡將無法再讀取這則文字。', '刪除文字'],
};

function queryPattern(text: string): RegExp | null {
  const term = text.trim();
  return term ? new RegExp(term.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), 'giu') : null;
}

function MessageText({ content, pattern }: { content: string; pattern: RegExp | null }) {
  if (!pattern) return <p className="message-text">{content}</p>;
  const matches = [...content.matchAll(pattern)];
  let start = 0;
  return <p className="message-text">{matches.map(match => {
    const before = content.slice(start, match.index);
    start = match.index + match[0].length;
    return <span key={match.index}>{before}<mark>{match[0]}</mark></span>;
  })}{content.slice(start)}</p>;
}

export function Room({ session, saved, theme, onTheme, onLeave, onInvalid }: {
  session: RoomSession; saved: boolean; theme: Theme; onTheme: (theme: Theme) => void;
  onLeave: () => void; onInvalid: (kind: 'identity' | 'room') => void;
}) {
  const [snapshot, setSnapshot] = useState<Snapshot>();
  const [pending, setPending] = useState(false);
  const [needsRefresh, setNeedsRefresh] = useState(true);
  const [error, setError] = useState<{ value: unknown; uncertain: boolean }>();
  const [qr, setQr] = useState(false);
  const [search, setSearch] = useState(false);
  const [query, setQuery] = useState('');
  const [menu, setMenu] = useState(false);
  const [draft, setDraft] = useState('');
  const [confirmation, setConfirmation] = useState<Confirmation>();
  const [fallback, setFallback] = useState<string>();
  const [copied, setCopied] = useState('');
  const [scrollRequest, setScrollRequest] = useState(0);
  const active = useRef<AbortController | null>(null);
  const alive = useRef(true);
  const list = useRef<HTMLElement>(null);
  const input = useRef<HTMLTextAreaElement>(null);
  const menuHost = useRef<HTMLDivElement>(null);
  const searchButton = useRef<HTMLButtonElement>(null);
  const inviteUrl = new URL(`/r/${session.code}`, window.location.origin).href;

  const run = useCallback(async (work: (signal: AbortSignal) => Promise<void>, uncertain = false) => {
    if (active.current) return;
    const operation = new AbortController();
    active.current = operation;
    setPending(true);
    setError(undefined);
    try {
      await work(operation.signal);
    } catch (error) {
      if (operation.signal.aborted) return;
      if (error instanceof ApiError && error.status === 401) onInvalid('identity');
      else if (error instanceof ApiError && error.status === 404) onInvalid('room');
      else {
        if (!(error instanceof ApiError) || error.status >= 500) setNeedsRefresh(true);
        setError({ value: error, uncertain });
      }
    } finally {
      if (active.current === operation) active.current = null;
      if (!operation.signal.aborted) setPending(false);
    }
  }, [onInvalid]);

  const refresh = useCallback(async (signal: AbortSignal) => {
    try {
      const next = await api.snapshot(session, signal);
      if (!signal.aborted) { setSnapshot(next); setNeedsRefresh(false); }
    } catch (error) {
      if (!signal.aborted) setNeedsRefresh(true);
      throw error;
    }
  }, [session]);

  useEffect(() => {
    alive.current = true;
    void run(async signal => {
      await refresh(signal);
      if (!signal.aborted) setScrollRequest(value => value + 1);
    });
    return () => { alive.current = false; active.current?.abort(); active.current = null; };
  }, [refresh, run]);

  useLayoutEffect(() => {
    if (scrollRequest && list.current) list.current.scrollTop = list.current.scrollHeight;
  }, [scrollRequest]);

  useLayoutEffect(() => {
    const textarea = input.current;
    if (!textarea) return;
    textarea.style.height = 'auto';
    textarea.style.height = `${textarea.scrollHeight}px`;
  }, [draft]);

  useEffect(() => {
    if (!menu) return;
    const closeAway = (event: PointerEvent) => {
      if (event.target instanceof Node && !menuHost.current?.contains(event.target)) setMenu(false);
    };
    document.addEventListener('pointerdown', closeAway);
    return () => document.removeEventListener('pointerdown', closeAway);
  }, [menu]);

  async function copy(text: string, label: string) {
    setFallback(undefined);
    setCopied('');
    try {
      await navigator.clipboard.writeText(text);
      if (alive.current) setCopied(label);
    } catch {
      if (alive.current) setFallback(text);
    }
  }

  function closeSearch() {
    setSearch(false);
    setQuery('');
    setScrollRequest(value => value + 1);
    searchButton.current?.focus();
  }

  function reload() {
    setMenu(false);
    void run(refresh);
  }

  const bytes = byteCount.encode(draft).length;
  const over = bytes > maxBytes;
  const canSend = !pending && !needsRefresh && !!draft.trim() && !over;

  function send() {
    if (!canSend) return;
    void run(async signal => {
      await api.add(session, draft, signal);
      if (signal.aborted) return;
      setDraft('');
      await refresh(signal);
      if (!signal.aborted && !search) setScrollRequest(value => value + 1);
    }, true);
  }

  function confirm() {
    setConfirmation(undefined);
    if (!confirmation) return;
    if (confirmation.kind === 'leave') { onLeave(); return; }
    void run(async signal => {
      if (confirmation.kind === 'close') {
        await api.close(session, signal);
        if (!signal.aborted) onInvalid('room');
        return;
      }
      if (confirmation.kind === 'delete') {
        await api.delete(session, confirmation.id, signal);
        if (!signal.aborted) await refresh(signal);
      }
    }, true);
  }

  const items = snapshot?.items ?? noItems;
  const pattern = search ? queryPattern(query) : null;
  const shown = (pattern ? items.filter(item => item.content.search(pattern) !== -1) : [...items]).reverse();
  const dialog = confirmation && confirmationText[confirmation.kind];

  return <div className="room-page">
    <header className="room-header"><div className="room-row"><Logo />
      <div className="header-actions"><span className="online"><i aria-hidden="true" />3 人在線</span>
        <button ref={searchButton} type="button" className="button ghost icon-button" aria-label="搜尋"
          aria-expanded={search} onClick={() => { setMenu(false); if (search) closeSearch(); else setSearch(true); }}><Icon name="search" /></button>
        <div className="menu-host" ref={menuHost} onKeyDown={event => { if (event.key === 'Escape') setMenu(false); }}>
          <Button className="icon-button" aria-label="更多選項" aria-expanded={menu} onClick={() => setMenu(!menu)}><Icon name="menu" /></Button>
          {menu && <div className="room-menu"><ThemeOptions theme={theme} onChange={onTheme} />
            <button className="menu-action" type="button" disabled={pending} onClick={reload}>重新整理</button>
            <div className="menu-divider" />
            <button className="menu-action destructive" type="button" onClick={() => { setMenu(false); setConfirmation({ kind: 'leave' }); }}>離開房間</button>
            {session.isCreator && <button className="menu-action destructive" type="button" disabled={pending || needsRefresh}
              onClick={() => { setMenu(false); setConfirmation({ kind: 'close' }); }}>關閉房間 <small>房主</small></button>}
          </div>}
        </div>
      </div></div>
    </header>
    <div className="room-toolbar"><div className="room-row">
      {search ? <div className="search-bar" onKeyDown={event => { if (event.key === 'Escape') closeSearch(); }}>
        <div className="search-field"><Icon name="search" /><input autoFocus aria-label="搜尋這個房間" type="search" value={query}
          placeholder="搜尋這個房間" onChange={event => setQuery(event.target.value)} /><span aria-live="polite">{shown.length} 筆</span></div>
        <Button onClick={closeSearch}>取消</Button>
      </div> : <><div className="room-code"><span>目前房間</span><strong>{session.code}</strong></div>
        <div className="invite-actions">
          <Button className="icon-button" aria-label="加入 QR Code" aria-expanded={qr} aria-controls="invite-qr"
            variant={qr ? 'soft' : 'ghost'} onClick={() => setQr(!qr)}><Icon name="qr" /></Button>
          <Button variant="soft" onClick={() => void copy(inviteUrl, '已複製邀請')}><Icon name="copy" />複製邀請</Button>
        </div></>}
    </div></div>
    {!search && qr && <section className="qr-wrap" id="invite-qr" aria-label="房間邀請">
      <div className="qr-panel"><QRCodeSVG value={inviteUrl} size={120} marginSize={4} title="掃描加入這個房間"
        bgColor="var(--qr-bg)" fgColor="var(--qr-fg)" /><div><strong>掃描加入房間</strong><code>{inviteUrl}</code>
        <p>QR 只包含加入連結，掃描的裝置會取得自己的新身分。</p></div></div>
    </section>}
    <div className="room-notices">
        {!saved && <StatusNotice title="身分只保存在這次開啟的頁面">瀏覽器不允許保存身分，重新整理後會失去目前身分。</StatusNotice>}
        {error && <ErrorNotice error={error.value} uncertain={error.uncertain} />}
        {fallback !== undefined && <CopyFallback value={fallback} close={() => setFallback(undefined)} />}
        {copied && <p className="copy-feedback" role="status">{copied}</p>}
    </div>
    <main className="chat-scroll" ref={list} aria-label="房間文字" aria-busy={pending}>
      <div className="chat-body">
        {!snapshot && pending && <p className="empty-chat" role="status">正在讀取文字…</p>}
        {snapshot && shown.length === 0 && (pattern ? <div className="empty-chat"><strong>找不到符合的文字</strong>
          <p>只會搜尋這個房間目前載入的內容。</p></div> : <div className="empty-chat"><strong>房間還沒有文字</strong>
          <p>送出一段文字，或分享邀請給另一台裝置。</p></div>)}
        {shown.map((item, index) => {
          const date = new Date(item.createdAt);
          const day = dateFormat.format(date);
          const time = timeFormat.format(date);
          const insertDate = !search && (index === 0 || day !== dateFormat.format(new Date(shown[index - 1].createdAt)));
          return <div key={item.id} className="message-group">
            {insertDate && <div className="date-divider"><time dateTime={item.createdAt}>
              {day === dateFormat.format(new Date()) ? '今天' : day} {time}</time></div>}
            <article className={`message${item.isOwn ? ' mine' : ''}`}><p className="speaker">{item.isOwn ? '你' : '訪客'}</p>
              <div className="bubble"><MessageText content={item.content} pattern={pattern} /></div>
              <div className="message-actions"><time dateTime={item.createdAt}>{time}</time>
                <button type="button" onClick={() => void copy(item.content, '已複製文字')}>複製</button>
                {item.isOwn && item.canDelete && <button type="button" disabled={pending || needsRefresh}
                  onClick={() => setConfirmation({ kind: 'delete', id: item.id })}>刪除</button>}
              </div>
            </article>
          </div>;
        })}
        {!search && <div className="manual-update"><StatusNotice title="其他裝置的新內容不會自動出現"
          action={<Button disabled={pending} onClick={reload}><Icon name="refresh" />重新整理</Button>}>
          按「重新整理」取得最新內容。</StatusNotice></div>}
      </div>
    </main>
    <footer className="composer-footer"><form className="composer" onSubmit={event => { event.preventDefault(); send(); }}>
      <div className="composer-input"><textarea ref={input} rows={1} aria-label="要送出的文字" placeholder="輸入或貼上文字…"
        aria-describedby={draft ? 'byte-counter' : undefined} aria-invalid={over} value={draft} readOnly={pending}
        onChange={event => setDraft(event.target.value)} />
        {draft && <p className={`byte-counter${over ? ' over' : ''}`} id="byte-counter">
          {countFormat.format(bytes)} / 8,192 bytes {over && '・超過上限'}</p>}</div>
      <Button className="send" type="submit" variant="primary" disabled={!canSend} aria-label="送出"><Icon name="send" /></Button>
    </form></footer>
    {dialog && <ConfirmDialog title={dialog[0]} description={dialog[1]} action={dialog[2]}
      onCancel={() => setConfirmation(undefined)} onConfirm={confirm} />}
  </div>;
}
