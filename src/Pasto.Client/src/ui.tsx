import { useEffect, useRef } from 'react';
import type { ButtonHTMLAttributes, ReactNode } from 'react';
import logo from './assets/logo/layers-mark.svg';
import { ApiError } from './api';

type IconName = 'search' | 'menu' | 'qr' | 'copy' | 'plus' | 'enter' | 'send' | 'refresh' | 'close' | 'down';

const iconPaths: Record<IconName, ReactNode> = {
  search: <><circle cx="10.5" cy="10.5" r="5.5" /><path d="m15 15 4 4" /></>,
  menu: <><circle cx="5" cy="12" r="1" /><circle cx="12" cy="12" r="1" /><circle cx="19" cy="12" r="1" /></>,
  qr: <><path d="M3 3h6v6H3zM15 3h6v6h-6zM3 15h6v6H3zM15 15h3v3h3v3h-6zM21 13v2M12 10v3M10 12h4" /></>,
  copy: <><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" /></>,
  plus: <path d="M12 5v14M5 12h14" />,
  enter: <path d="M13 4h5a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2h-5M3 12h12m-4-4 4 4-4 4" />,
  send: <path d="m3 11 17-7-7 17-2-8-8-2ZM11 13l9-9" />,
  refresh: <path d="M20 7v5h-5M4 17v-5h5m-4 0a7 7 0 0 1 12-5l3 5m-16 0 3 5a7 7 0 0 0 12-5" />,
  close: <path d="m6 6 12 12M18 6 6 18" />,
  down: <path d="M12 5v14m-5-5 5 5 5-5" />,
};

export function Icon({ name }: { name: IconName }) {
  return <svg className="icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
    strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{iconPaths[name]}</svg>;
}

export function Logo() {
  return <div className="logo"><span className="logo-tile"><span className="logo-mark" aria-hidden="true"
    style={{ maskImage: `url(${JSON.stringify(logo)})` }} /></span><span className="wordmark"><strong>Pasto.</strong><small>Share instantly.</small></span></div>;
}

export function Button({ className = '', variant = 'ghost', type = 'button', ...props }: ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'ghost' | 'soft' | 'danger';
}) {
  return <button type={type} className={`button ${variant} ${className}`} {...props} />;
}

export function StatusNotice({ title, children, action, danger = false, alert = false }: {
  title: string; children: ReactNode; action?: ReactNode; danger?: boolean; alert?: boolean;
}) {
  return <aside className={`notice${danger ? ' notice-danger' : ''}`} role={alert ? 'alert' : 'status'}>
    <div><strong>{title}</strong><p>{children}</p></div>{action}
  </aside>;
}

const errors: Record<string, string> = {
  InvalidRoomCode: '請確認房碼為六位英文字母或數字。',
  RoomUnavailable: '請確認房碼，或向分享者索取新的邀請。',
  RoomItemLimitReached: '房間已達文字筆數上限。',
  ParticipantLimitReached: '房間已達加入人數上限。',
  ItemDeleteForbidden: '你只能刪除自己送出的文字。',
  RoomDeleteForbidden: '只有房主可以關閉房間。',
  EmptyContent: '請輸入要送出的文字。',
  TextTooLarge: '文字超過 8,192 bytes 上限。',
  InvalidRequest: '請確認輸入內容後再試一次。',
};

export function ErrorNotice({ error, uncertain = false }: { error: unknown; uncertain?: boolean }) {
  if (error instanceof ApiError && error.status === 503) return <StatusNotice title="伺服器忙碌" danger alert>
    {error.code === 'Overloaded' ? '操作尚未執行，請稍候再試。' : uncertain
      ? '目前無法確認操作結果。請先重新整理或確認房間狀態，再決定是否重試。'
      : '請稍候再試。'}
  </StatusNotice>;
  if (error instanceof ApiError && error.status === 429) return <StatusNotice title="操作太頻繁" danger alert>
    請在 {error.retryAfter} 秒後再試一次。
  </StatusNotice>;
  const known = error instanceof ApiError ? errors[error.code] : undefined;
  return <StatusNotice title="操作未完成" danger alert>{known ?? (uncertain
    ? '目前無法確認操作結果。請先重新整理或確認房間狀態，再決定是否重試。'
    : '目前無法連線，請稍後再試一次。')}</StatusNotice>;
}

export type Theme = 'system' | 'light' | 'dark';
export function readTheme(): Theme {
  try {
    const value = localStorage.getItem('pasto-theme');
    return value === 'light' || value === 'dark' ? value : 'system';
  } catch { return 'system'; }
}

export function applyTheme(theme: Theme) {
  if (theme === 'system') delete document.documentElement.dataset.theme;
  else document.documentElement.dataset.theme = theme;
  try {
    if (theme === 'system') localStorage.removeItem('pasto-theme');
    else localStorage.setItem('pasto-theme', theme);
  } catch { /* Apply to the current page even if preferences cannot be saved. */ }
}

export function ThemeOptions({ theme, onChange }: { theme: Theme; onChange: (theme: Theme) => void }) {
  return <fieldset className="theme-options"><legend>外觀</legend>{([
    ['system', '系統'], ['light', '淺色'], ['dark', '深色'],
  ] as const).map(([value, label]) => <Button key={value} aria-pressed={theme === value}
    variant={theme === value ? 'soft' : 'ghost'} onClick={() => onChange(value)}>{label}</Button>)}</fieldset>;
}

export function ConfirmDialog({ title, description, action, onCancel, onConfirm }: {
  title: string; description: string; action: string; onCancel: () => void; onConfirm: () => void;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => { dialog.current?.showModal(); }, []);
  return <dialog ref={dialog} aria-labelledby="confirm-title" aria-describedby="confirm-description"
    onCancel={event => { event.preventDefault(); onCancel(); }}>
    <h2 id="confirm-title">{title}</h2><p id="confirm-description">{description}</p>
    <div className="dialog-actions"><Button autoFocus onClick={onCancel}>取消</Button>
      <Button variant="danger" onClick={onConfirm}>{action}</Button></div>
  </dialog>;
}

export function CopyFallback({ value, close }: { value: string; close: () => void }) {
  const field = useRef<HTMLTextAreaElement>(null);

  useEffect(() => {
    field.current?.focus();
    field.current?.select();
  }, [value]);

  return <StatusNotice title="無法自動複製" alert action={<Button onClick={close}>關閉</Button>}>
    文字已幫你選取，請按 Ctrl＋C 或長按複製。<textarea ref={field} aria-label="手動複製文字" readOnly value={value} />
  </StatusNotice>;
}
