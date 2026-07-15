import { useEffect, useRef, useState } from 'react';
import { WidgetChat } from './WidgetChat';
import type { WidgetInitResponse, WidgetTheme } from './types';
import { DARK_PRESET } from './types';
import { storageKey } from '@/lib/brand';

const API = window.location.origin;
const SSO_TIMEOUT_MS = 3000;

function applyTheme(theme: WidgetTheme, respectSystem: boolean) {
  const prefersDark =
    respectSystem && window.matchMedia('(prefers-color-scheme: dark)').matches;
  const effective =
    prefersDark && theme.preset === 'light' ? DARK_PRESET : theme;

  const root = document.documentElement;
  root.style.setProperty('--diva-bg', effective.background);
  root.style.setProperty('--diva-surface', effective.surface);
  root.style.setProperty('--diva-border', effective.border);
  root.style.setProperty('--diva-primary', effective.primary);
  root.style.setProperty('--diva-primary-text', effective.primaryText);
  root.style.setProperty('--diva-text', effective.text);
  root.style.setProperty('--diva-text-muted', effective.textMuted);
  root.style.setProperty('--diva-font', effective.fontFamily);
  root.style.setProperty('--diva-font-size', effective.fontSize);
  root.style.setProperty('--diva-agent-bg', effective.agentBubbleBg);
  root.style.setProperty('--diva-agent-text', effective.agentBubbleText);
  root.style.setProperty('--diva-header-bg', effective.headerBg);
  root.style.setProperty('--diva-header-text', effective.headerText);
  root.style.setProperty('--diva-input-bg', effective.inputBg);
  root.style.setProperty('--diva-input-border', effective.inputBorder);
  root.style.setProperty('--diva-input-text', effective.inputText);
}

function getStoredAuth(widgetId: string) {
  try {
    const raw = sessionStorage.getItem(storageKey(`widget_${widgetId}`));
    if (!raw) return null;
    const parsed = JSON.parse(raw) as { token: string; expiresAt: string };
    const exp = new Date(parsed.expiresAt).getTime();
    if (exp - Date.now() < 5 * 60 * 1000) return null; // within 5 min → re-auth
    return parsed.token;
  } catch {
    return null;
  }
}

function saveAuth(widgetId: string, token: string, expiresAt: string) {
  sessionStorage.setItem(
    storageKey(`widget_${widgetId}`),
    JSON.stringify({ token, expiresAt })
  );
}

type AppState = 'loading' | 'authing' | 'ready' | 'denied';

export function WidgetApp({ widgetId }: { widgetId: string }) {
  const [config, setConfig] = useState<WidgetInitResponse | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [state, setState] = useState<AppState>('loading');
  const [error, setError] = useState<string | null>(null);
  const ssoTimerRef = useRef<ReturnType<typeof setTimeout> | null>(null);

  /* ── 1. Load widget config ───────────────────────────────────────────── */
  useEffect(() => {
    if (!widgetId) {
      setError('No widget ID provided.');
      setState('denied');
      return;
    }
    fetch(`${API}/api/widget/${encodeURIComponent(widgetId)}/init`)
      .then(r => {
        if (!r.ok) throw new Error('Widget not found or inactive.');
        return r.json() as Promise<WidgetInitResponse>;
      })
      .then(cfg => {
        setConfig(cfg);
        applyTheme(cfg.theme, cfg.respectSystemTheme);

        // Watch system theme changes
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        const onChange = () => applyTheme(cfg.theme, cfg.respectSystemTheme);
        mq.addEventListener('change', onChange);
        return () => mq.removeEventListener('change', onChange);
      })
      .catch(e => {
        setError(e instanceof Error ? e.message : 'Failed to load widget.');
        setState('denied');
      });
  }, [widgetId]);

  /* ── 2. Auth flow once config is loaded ─────────────────────────────── */
  useEffect(() => {
    if (!config) return;

    // Check stored session first
    const stored = getStoredAuth(widgetId);
    if (stored) {
      setToken(stored);
      setState('ready');
      return;
    }

    setState('authing');

    if (config.hasSso) {
      // Request SSO token from host page
      window.parent.postMessage({ type: 'DIVA_SSO_REQUEST' }, '*');

      const onMessage = (e: MessageEvent) => {
        if (e.data?.type !== 'DIVA_SSO_TOKEN') return;
        if (ssoTimerRef.current) clearTimeout(ssoTimerRef.current);
        window.removeEventListener('message', onMessage);

        if (e.data.token) {
          exchangeSsoToken(e.data.token);
        } else {
          fallbackToAnonymous();
        }
      };

      window.addEventListener('message', onMessage);
      ssoTimerRef.current = setTimeout(() => {
        window.removeEventListener('message', onMessage);
        fallbackToAnonymous();
      }, SSO_TIMEOUT_MS);
    } else {
      fallbackToAnonymous();
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [config]);

  function exchangeSsoToken(ssoToken: string) {
    fetch(`${API}/api/widget/${encodeURIComponent(widgetId)}/auth`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ssoToken }),
    })
      .then(r => {
        if (!r.ok) throw new Error('SSO auth failed.');
        return r.json() as Promise<{ token: string; expiresAt: string }>;
      })
      .then(data => {
        saveAuth(widgetId, data.token, data.expiresAt);
        setToken(data.token);
        setState('ready');
      })
      .catch(() => fallbackToAnonymous());
  }

  function fallbackToAnonymous() {
    if (!config?.allowAnonymous) {
      setState('denied');
      return;
    }
    fetch(`${API}/api/widget/${encodeURIComponent(widgetId)}/session`, {
      method: 'POST',
    })
      .then(r => {
        if (!r.ok) throw new Error('Session creation failed.');
        return r.json() as Promise<{ token: string; expiresAt: string }>;
      })
      .then(data => {
        saveAuth(widgetId, data.token, data.expiresAt);
        setToken(data.token);
        setState('ready');
      })
      .catch(() => {
        setState('denied');
      });
  }

  /* ── Render ─────────────────────────────────────────────────────────── */
  const containerStyle: React.CSSProperties = {
    height: '100dvh',
    display: 'flex',
    flexDirection: 'column',
    background: 'var(--diva-bg, #ffffff)',
    color: 'var(--diva-text, #111827)',
    fontFamily: 'var(--diva-font, system-ui, sans-serif)',
    fontSize: 'var(--diva-font-size, 14px)',
  };

  if (state === 'loading' || state === 'authing') {
    return (
      <div style={{ ...containerStyle, alignItems: 'center', justifyContent: 'center', gap: 8 }}>
        <SpinnerDots />
      </div>
    );
  }

  if (state === 'denied') {
    return (
      <div style={{ ...containerStyle, alignItems: 'center', justifyContent: 'center', padding: 24, textAlign: 'center' }}>
        <p style={{ color: 'var(--diva-text-muted, #6b7280)' }}>
          {error ?? 'Authentication required to use this widget.'}
        </p>
      </div>
    );
  }

  return (
    <WidgetChat
      widgetId={widgetId}
      agentId={config!.agentId}
      agentName={config!.agentName}
      token={token!}
      welcomeMessage={config!.welcomeMessage}
      placeholderText={config!.placeholderText}
      showBranding={config!.showBranding}
    />
  );
}

function SpinnerDots() {
  return (
    <div style={{ display: 'flex', gap: 6 }}>
      {[0, 1, 2].map(i => (
        <div
          key={i}
          style={{
            width: 8,
            height: 8,
            borderRadius: '50%',
            background: 'var(--diva-primary, #6366f1)',
            opacity: 0.7,
            animation: `diva-bounce 1.2s ease-in-out ${i * 0.2}s infinite`,
          }}
        />
      ))}
      <style>{`
        @keyframes diva-bounce {
          0%, 80%, 100% { transform: scale(0.6); opacity: 0.4; }
          40% { transform: scale(1); opacity: 1; }
        }
      `}</style>
    </div>
  );
}
