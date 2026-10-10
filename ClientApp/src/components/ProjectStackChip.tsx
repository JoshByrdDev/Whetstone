import { useState, useRef, useEffect } from 'react';
import { usePop } from '../hooks/usePop';

export interface StackLink {
  label: string;
  url: string;
}

export interface StackHolder {
  kind: 'you' | 'agent';
  ticketId: number;
  ticketKey: string | null;
  skill: string | null;
  since: string;
}

export interface StackSnapshot {
  key: string;
  displayName: string;
  configured: boolean;
  state: 'NoStack' | 'Stopped' | 'Starting' | 'Running' | 'Stopping' | 'Error';
  probedAt: string | null;
  holder: StackHolder | null;
  canStart: boolean;
  canStop: boolean;
  blocked: string | null;
  open: StackLink[];
  lastError: string | null;
  output: string[];
  actionSeconds: number;
  waitingQa: number;
}

const DOT: Record<StackSnapshot['state'], string> = {
  NoStack: 'bg-gray-700',
  Stopped: 'bg-gray-600',
  Starting: 'bg-amber-400 animate-pulse',
  Running: 'bg-emerald-400',
  Stopping: 'bg-amber-400 animate-pulse',
  Error: 'bg-red-500',
};

const LABEL: Record<StackSnapshot['state'], string> = {
  NoStack: '',
  Stopped: 'Stopped',
  Starting: 'Starting',
  Running: 'Running',
  Stopping: 'Stopping',
  Error: 'Failed',
};

/**
 * Start / stop / open one project's local stack, inline on the board's project row.
 *
 * Start and Stop go through the same resource lease the QA phase takes, so this deliberately
 * refuses to act while an agent holds the stack — it says who has it instead. That is the point of
 * the control: one place to see whether the app is up and to take it, without racing the fleet.
 */
export function ProjectStackChip({
  stack,
  onChanged,
}: {
  stack: StackSnapshot;
  onChanged: (next: StackSnapshot) => void;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showLog, setShowLog] = useState(false);
  const [openMenu, setOpenMenu] = useState(false);
  const menuPop = usePop(openMenu);
  const logPop = usePop(showLog);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!openMenu) return;
    const close = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setOpenMenu(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [openMenu]);

  if (!stack.configured) return null;

  const act = async (verb: 'start' | 'stop') => {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(`/api/projects/${stack.key}/stack/${verb}`, { method: 'POST' });
      const body = await res.json();
      if (res.ok) {
        onChanged(body as StackSnapshot);
      } else {
        setError(body?.error ?? `Could not ${verb} the stack.`);
        if (body?.stack) onChanged(body.stack as StackSnapshot);
      }
    } catch {
      setError(`Could not ${verb} the stack.`);
    } finally {
      setBusy(false);
    }
  };

  const agentHolds = stack.holder?.kind === 'agent';
  const inFlight = stack.state === 'Starting' || stack.state === 'Stopping';
  const label = inFlight && stack.actionSeconds > 0
    ? `${LABEL[stack.state]}… ${stack.actionSeconds}s`
    : LABEL[stack.state];

  return (
    <div className="relative flex items-center gap-1.5 text-[11px]">
      <span className={`h-2 w-2 rounded-full ${DOT[stack.state]}`} />
      <span className="text-gray-400">{label}</span>

      {/* Who has it. An agent's hold is why the buttons are dead; the user's own is a reminder. */}
      {agentHolds && (
        <span className="px-1.5 py-0.5 rounded bg-violet-500/15 text-violet-300 text-[10px] font-medium"
              title={stack.blocked ?? ''}>
          {stack.holder!.ticketKey} · {stack.holder!.skill ?? 'agent'}
        </span>
      )}
      {stack.holder?.kind === 'you' && !inFlight && (
        <span className="px-1.5 py-0.5 rounded bg-sky-500/15 text-sky-300 text-[10px] font-medium"
              title="You hold the QA stack lease. Agents cannot start QA on this project until you stop it.">
          yours{stack.waitingQa > 0 ? ` · ${stack.waitingQa} QA waiting` : ''}
        </span>
      )}

      {stack.state === 'Running' && stack.open.length > 0 && (
        stack.open.length === 1 ? (
          <a href={stack.open[0].url} target="_blank" rel="noreferrer"
             className="px-1.5 py-0.5 rounded border border-hair text-gray-300 hover:border-gray-500 hover:text-gray-100 transition-colors">
            Open
          </a>
        ) : (
          <div className="relative" ref={menuRef}>
            <button onClick={() => setOpenMenu(v => !v)}
                    className="px-1.5 py-0.5 rounded border border-hair text-gray-300 hover:border-gray-500 hover:text-gray-100 transition-colors">
              Open ▾
            </button>
            {menuPop.show && (
              <div className="orbit-pop absolute z-20 mt-1 min-w-[160px] rounded-md border border-hair bg-panel py-1 shadow-lg" data-fx={menuPop.fx}>
                {stack.open.map(l => (
                  <a key={l.url} href={l.url} target="_blank" rel="noreferrer"
                     onClick={() => setOpenMenu(false)}
                     className="block px-3 py-1.5 text-[11px] text-gray-300 hover:bg-gray-800 hover:text-gray-100">
                    {l.label}
                  </a>
                ))}
              </div>
            )}
          </div>
        )
      )}

      {stack.canStart && (
        <button onClick={() => act('start')} disabled={busy}
                className="px-1.5 py-0.5 rounded bg-emerald-600/80 hover:bg-emerald-500 text-gray-100 font-medium disabled:opacity-40 transition-colors">
          Start
        </button>
      )}
      {stack.canStop && (
        <button onClick={() => act('stop')} disabled={busy}
                className="px-1.5 py-0.5 rounded border border-hair text-gray-300 hover:border-red-500/60 hover:text-red-300 disabled:opacity-40 transition-colors">
          Stop
        </button>
      )}
      {agentHolds && (
        <span className="text-[10px] text-gray-600">waiting on the agent</span>
      )}

      {(stack.lastError || error) && (
        <button onClick={() => setShowLog(v => !v)} className="text-[10px] text-red-400 hover:text-red-300 underline decoration-dotted">
          {error ?? 'why?'}
        </button>
      )}
      {!stack.lastError && !error && stack.output.length > 0 && inFlight && (
        <button onClick={() => setShowLog(v => !v)} className="text-[10px] text-gray-600 hover:text-gray-400">
          log
        </button>
      )}

      {logPop.show && (
        <div className="orbit-pop absolute left-0 top-full z-30 mt-1 max-h-64 w-[560px] max-w-[70vw] overflow-auto rounded-md border border-hair bg-sunk p-3 font-mono text-[10px] leading-relaxed text-gray-400 shadow-xl"
             data-fx={logPop.fx}>
          {stack.lastError && <div className="mb-2 text-red-400">{stack.lastError}</div>}
          {stack.output.length === 0
            ? <div className="text-gray-600">No output yet.</div>
            : stack.output.map((line, i) => <div key={i} className="whitespace-pre-wrap">{line}</div>)}
        </div>
      )}
    </div>
  );
}
