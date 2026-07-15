import { useCallback, useEffect, useRef, useState } from 'react';
import type { AgentStreamChunk, ChatMessage } from './types';
import { storageKey } from '@/lib/brand';
import { renderMarkdown } from './markdown';

const API = window.location.origin;

interface Props {
  widgetId: string;
  agentId: string;
  agentName: string;
  token: string;
  welcomeMessage?: string;
  placeholderText?: string;
  showBranding: boolean;
}

export function WidgetChat({
  widgetId,
  agentId,
  agentName,
  token,
  welcomeMessage,
  placeholderText,
  showBranding,
}: Props) {
  const [messages, setMessages] = useState<ChatMessage[]>(() => {
    const initial: ChatMessage[] = [];
    if (welcomeMessage) {
      initial.push({ id: 'welcome', role: 'agent', content: welcomeMessage });
    }
    return initial;
  });
  const [input, setInput] = useState('');
  const [streaming, setStreaming] = useState(false);
  const [typing, setTyping] = useState(false);
  const sessionIdRef = useRef<string>(sessionStorage.getItem(storageKey(`session_${widgetId}`)) ?? '');
  const abortRef = useRef<AbortController | null>(null);
  const listEndRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    listEndRef.current?.scrollIntoView({ behavior: 'smooth' });
  }, [messages, typing]);

  const handleClose = useCallback(() => {
    window.parent.postMessage({ type: 'DIVA_CLOSE' }, '*');
  }, []);

  const sendMessage = useCallback(async () => {
    const text = input.trim();
    if (!text || streaming) return;
    setInput('');

    const userMsg: ChatMessage = { id: crypto.randomUUID(), role: 'user', content: text };
    setMessages(prev => [...prev, userMsg]);
    setTyping(true);
    setStreaming(true);

    abortRef.current?.abort();
    abortRef.current = new AbortController();

    const agentMsgId = crypto.randomUUID();
    let agentContent = '';

    try {
      const res = await fetch(`${API}/api/agents/${encodeURIComponent(agentId)}/invoke/stream`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          Authorization: `Bearer ${token}`,
        },
        body: JSON.stringify({
          message: text,
          sessionId: sessionIdRef.current || undefined,
        }),
        signal: abortRef.current.signal,
      });

      if (!res.ok || !res.body) {
        throw new Error(`API error ${res.status}`);
      }

      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';

        for (const line of lines) {
          if (!line.startsWith('data:')) continue;
          const json = line.slice(5).trim();
          if (!json || json === '[DONE]') continue;

          let chunk: AgentStreamChunk;
          try { chunk = JSON.parse(json); } catch { continue; }

          if (chunk.sessionId) {
            sessionIdRef.current = chunk.sessionId;
            sessionStorage.setItem(storageKey(`session_${widgetId}`), chunk.sessionId);
          }

          if (chunk.type === 'iteration_start') {
            setTyping(true);
          } else if (chunk.type === 'text_delta' && chunk.delta) {
            agentContent += chunk.delta;
            setTyping(false);
            setMessages(prev => {
              const existing = prev.find(m => m.id === agentMsgId);
              if (existing) {
                return prev.map(m =>
                  m.id === agentMsgId ? { ...m, content: agentContent, streaming: true } : m
                );
              }
              return [
                ...prev,
                { id: agentMsgId, role: 'agent', content: agentContent, streaming: true },
              ];
            });
          } else if (chunk.type === 'final_response' && chunk.content) {
            agentContent = chunk.content;
            setTyping(false);
            setMessages(prev => {
              const existing = prev.find(m => m.id === agentMsgId);
              if (existing) {
                return prev.map(m =>
                  m.id === agentMsgId ? { ...m, content: agentContent, streaming: false } : m
                );
              }
              return [
                ...prev,
                { id: agentMsgId, role: 'agent', content: agentContent, streaming: false },
              ];
            });
          } else if (chunk.type === 'error') {
            setTyping(false);
            setMessages(prev => [
              ...prev,
              {
                id: crypto.randomUUID(),
                role: 'agent',
                content: chunk.content ?? 'An error occurred.',
              },
            ]);
          } else if (chunk.type === 'done') {
            setTyping(false);
            setMessages(prev =>
              prev.map(m =>
                m.id === agentMsgId ? { ...m, streaming: false } : m
              )
            );
          }
        }
      }
    } catch (err) {
      if ((err as Error).name === 'AbortError') return;
      setTyping(false);
      setMessages(prev => [
        ...prev,
        { id: crypto.randomUUID(), role: 'agent', content: 'Failed to connect. Please try again.' },
      ]);
    } finally {
      setStreaming(false);
      setTyping(false);
    }
  }, [agentId, input, streaming, token, widgetId]);

  const onKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault();
      sendMessage();
    }
  };

  return (
    <div style={styles.root}>
      <style>{`
        .diva-md > *:first-child { margin-top: 0; }
        .diva-md > *:last-child { margin-bottom: 0; }
        .diva-md-p { margin: 0 0 8px; }
        .diva-md-h { font-weight: 600; margin: 10px 0 6px; }
        .diva-md-h1 { font-size: 1.15em; }
        .diva-md-h2 { font-size: 1.08em; }
        .diva-md-h3, .diva-md-h4, .diva-md-h5, .diva-md-h6 { font-size: 1em; }
        .diva-md-ul, .diva-md-ol { margin: 0 0 8px; padding-left: 20px; }
        .diva-md-ul li, .diva-md-ol li { margin: 2px 0; }
        .diva-md-code { background: rgba(127,127,127,0.18); border-radius: 4px; padding: 1px 4px; font-family: ui-monospace, monospace; font-size: 0.85em; }
        .diva-md-pre { background: rgba(127,127,127,0.14); border-radius: 6px; padding: 8px 10px; overflow-x: auto; margin: 0 0 8px; }
        .diva-md-pre code { font-family: ui-monospace, monospace; font-size: 0.82em; white-space: pre; }
        .diva-md-link { color: var(--diva-primary, #6366f1); text-decoration: underline; }
        .diva-md-table-wrap { overflow-x: auto; margin: 0 0 8px; }
        .diva-md-table { border-collapse: collapse; width: 100%; font-size: 0.9em; }
        .diva-md-th, .diva-md-td { border: 1px solid rgba(127,127,127,0.3); padding: 4px 8px; text-align: left; }
        .diva-md-th { font-weight: 600; background: rgba(127,127,127,0.1); }
      `}</style>
      {/* Header */}
      <div style={styles.header}>
        <div style={styles.headerTitle}>
          <div style={styles.dot} />
          <span>{agentName}</span>
        </div>
        <button
          style={styles.closeBtn}
          onClick={handleClose}
          aria-label="Close chat"
          title="Close"
        >
          <svg width="16" height="16" viewBox="0 0 16 16" fill="currentColor">
            <path d="M3.72 3.72a.75.75 0 0 1 1.06 0L8 6.94l3.22-3.22a.75.75 0 1 1 1.06 1.06L9.06 8l3.22 3.22a.75.75 0 1 1-1.06 1.06L8 9.06l-3.22 3.22a.75.75 0 0 1-1.06-1.06L6.94 8 3.72 4.78a.75.75 0 0 1 0-1.06Z" />
          </svg>
        </button>
      </div>

      {/* Message list */}
      <div style={styles.messageList}>
        {messages.map(msg => (
          <MessageBubble key={msg.id} msg={msg} />
        ))}
        {typing && <TypingIndicator />}
        <div ref={listEndRef} />
      </div>

      {/* Input bar */}
      <div style={styles.inputBar}>
        <textarea
          value={input}
          onChange={e => setInput(e.target.value)}
          onKeyDown={onKeyDown}
          placeholder={placeholderText ?? 'Type a message…'}
          disabled={streaming}
          rows={1}
          style={styles.textarea}
        />
        <button
          onClick={sendMessage}
          disabled={!input.trim() || streaming}
          style={{
            ...styles.sendBtn,
            opacity: !input.trim() || streaming ? 0.45 : 1,
            cursor: !input.trim() || streaming ? 'not-allowed' : 'pointer',
          }}
          aria-label="Send message"
        >
          <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor">
            <path d="M3.478 2.405a.75.75 0 0 0-.926.94l2.432 7.905H13.5a.75.75 0 0 1 0 1.5H4.984l-2.432 7.905a.75.75 0 0 0 .926.94 60.519 60.519 0 0 0 18.445-8.986.75.75 0 0 0 0-1.218A60.517 60.517 0 0 0 3.478 2.405Z" />
          </svg>
        </button>
      </div>

      {showBranding && (
        <div style={styles.branding}>
          Powered by <strong>Diva AI</strong>
        </div>
      )}
    </div>
  );
}

function MessageBubble({ msg }: { msg: ChatMessage }) {
  const isUser = msg.role === 'user';
  return (
    <div style={{ display: 'flex', justifyContent: isUser ? 'flex-end' : 'flex-start', marginBottom: 8 }}>
      <div
        style={{
          maxWidth: '80%',
          padding: '8px 12px',
          borderRadius: isUser ? '16px 16px 4px 16px' : '16px 16px 16px 4px',
          background: isUser
            ? 'var(--diva-primary, #6366f1)'
            : 'var(--diva-agent-bg, #f3f4f6)',
          color: isUser
            ? 'var(--diva-primary-text, #ffffff)'
            : 'var(--diva-agent-text, #111827)',
          lineHeight: 1.5,
          wordBreak: 'break-word',
          whiteSpace: isUser ? 'pre-wrap' : 'normal',
        }}
      >
        {isUser ? (
          msg.content
        ) : (
          <span
            className="diva-md"
            dangerouslySetInnerHTML={{ __html: renderMarkdown(msg.content) }}
          />
        )}
        {msg.streaming && <span style={{ opacity: 0.5 }}>▍</span>}
      </div>
    </div>
  );
}

function TypingIndicator() {
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 4, marginBottom: 8, paddingLeft: 4 }}>
      {[0, 1, 2].map(i => (
        <div
          key={i}
          style={{
            width: 7,
            height: 7,
            borderRadius: '50%',
            background: 'var(--diva-text-muted, #6b7280)',
            animation: `diva-typing 1.2s ease-in-out ${i * 0.15}s infinite`,
          }}
        />
      ))}
      <style>{`
        @keyframes diva-typing {
          0%, 60%, 100% { transform: translateY(0); opacity: 0.4; }
          30% { transform: translateY(-4px); opacity: 1; }
        }
      `}</style>
    </div>
  );
}

const styles = {
  root: {
    display: 'flex',
    flexDirection: 'column' as const,
    height: '100dvh',
    background: 'var(--diva-bg, #ffffff)',
    color: 'var(--diva-text, #111827)',
    fontFamily: 'var(--diva-font, system-ui, sans-serif)',
    fontSize: 'var(--diva-font-size, 14px)',
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'space-between',
    padding: '12px 16px',
    background: 'var(--diva-header-bg, #6366f1)',
    color: 'var(--diva-header-text, #ffffff)',
    flexShrink: 0,
  },
  headerTitle: {
    display: 'flex',
    alignItems: 'center',
    gap: 8,
    fontWeight: 600,
    fontSize: 15,
  },
  dot: {
    width: 8,
    height: 8,
    borderRadius: '50%',
    background: '#4ade80',
    boxShadow: '0 0 0 2px rgba(74,222,128,.3)',
  },
  closeBtn: {
    background: 'none',
    border: 'none',
    cursor: 'pointer',
    color: 'var(--diva-header-text, #ffffff)',
    display: 'flex',
    alignItems: 'center',
    padding: 4,
    borderRadius: 4,
    opacity: 0.8,
  },
  messageList: {
    flex: 1,
    overflowY: 'auto' as const,
    padding: '16px 12px',
    display: 'flex',
    flexDirection: 'column' as const,
  },
  inputBar: {
    display: 'flex',
    alignItems: 'flex-end',
    gap: 8,
    padding: '10px 12px',
    borderTop: '1px solid var(--diva-border, #e5e7eb)',
    background: 'var(--diva-input-bg, #ffffff)',
    flexShrink: 0,
  },
  textarea: {
    flex: 1,
    resize: 'none' as const,
    border: '1px solid var(--diva-input-border, #d1d5db)',
    borderRadius: 10,
    padding: '8px 12px',
    background: 'var(--diva-input-bg, #ffffff)',
    color: 'var(--diva-input-text, #111827)',
    fontFamily: 'inherit',
    fontSize: 'inherit',
    lineHeight: 1.5,
    outline: 'none',
    maxHeight: 100,
    overflowY: 'auto' as const,
  },
  sendBtn: {
    width: 36,
    height: 36,
    flexShrink: 0,
    border: 'none',
    borderRadius: '50%',
    background: 'var(--diva-primary, #6366f1)',
    color: 'var(--diva-primary-text, #ffffff)',
    display: 'flex',
    alignItems: 'center',
    justifyContent: 'center',
    transition: 'opacity .15s',
  },
  branding: {
    textAlign: 'center' as const,
    fontSize: 11,
    color: 'var(--diva-text-muted, #6b7280)',
    padding: '4px 0 6px',
    flexShrink: 0,
  },
};
