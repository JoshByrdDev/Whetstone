import { useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';
import { GhostButton } from './ui';
import type { AgentState, DashboardState } from '../types/events';

/**
 * Whether the hard stop would kill anything: an agent out or a steward run going. A heartbeat
 * between passes with every berth empty loses nothing to a stop, so the are-you-sure is noise
 * there. Unknown (the state could not be read) counts as yes - the dialog is the safe side.
 */
export async function fleetHasWorkOut(): Promise<boolean> {
  try {
    const r = await fetch('/api/state');
    if (!r.ok) return true;
    const s: DashboardState = await r.json();
    return (s.agents?.length ?? 0) > 0 || !!s.steward;
  } catch {
    return true;
  }
}

/**
 * The are-you-sure in front of the hard stop (POST /api/heartbeat/stop). That stop kills every
 * worker mid-phase, on this machine and on every worker host, so whatever they had not committed
 * is lost. The dialog names what is out right now and points at the gentler stop beside it.
 *
 * Rendered only while open, as `useVeil` needs.
 */
export function ConfirmStopModal({ onConfirm, onClose }: { onConfirm: () => void; onClose: () => void }) {
  const { fx, leave, dismiss } = useVeil(onClose);
  const [agents, setAgents] = useState<AgentState[] | null>(null);
  const [steward, setSteward] = useState(false);

  useEffect(() => {
    let live = true;
    fetch('/api/state')
      .then(r => (r.ok ? r.json() : null))
      .then((s: DashboardState | null) => {
        if (!live || !s) return;
        setAgents(s.agents ?? []);
        setSteward(!!s.steward);
      })
      .catch(() => {});
    return () => { live = false; };
  }, []);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss]);

  const confirm = () => leave(() => { onClose(); onConfirm(); });

  const n = agents?.length ?? 0;
  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-stop-title"
        className="orbit-modal relative w-full max-w-md flex flex-col overflow-hidden"
      >
        <div className="px-5 py-3 border-b border-hair bg-panel">
          <h2 id="confirm-stop-title" className="text-sm font-semibold text-gray-200 uppercase tracking-wider">
            Stop every worker now?
          </h2>
        </div>
        <div className="px-5 py-4 space-y-3 text-[13px] text-gray-300">
          <p>
            This kills the heartbeat and every agent it has out, here and on every worker host, in the middle of
            whatever they are doing. Work they have not committed is lost.
          </p>
          {agents === null ? (
            <p className="text-[12px] text-gray-500">Reading what is running…</p>
          ) : n === 0 && !steward ? (
            <p className="text-[12px] text-gray-500">No agents are out right now.</p>
          ) : (
            <div>
              <div className="text-[11px] font-semibold text-gray-400 pb-1">
                {n} agent{n === 1 ? '' : 's'} running{steward ? ', plus the steward' : ''}
              </div>
              <ul className="max-h-40 overflow-auto rounded border border-hairsoft">
                {agents.map(a => (
                  <li key={a.slot} className="flex items-baseline gap-2 px-2 py-1 text-[11px] border-b border-hairsoft last:border-0">
                    <span className="font-mono text-gray-600 flex-none">#{a.slot}</span>
                    <span className="font-mono text-amber-400 flex-none">{a.ticketId ?? '—'}</span>
                    <span className="text-gray-400 min-w-0 truncate">{a.phase ?? a.status}</span>
                  </li>
                ))}
              </ul>
            </div>
          )}
          <p className="text-[12px] text-gray-500">
            To let them finish first, use <span className="text-gray-300">Stop after current cycle</span> instead.
          </p>
        </div>
        <div className="flex items-center justify-end gap-2 px-5 py-3 border-t border-hair bg-panel">
          <button onClick={dismiss} autoFocus className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200 transition-colors">
            Cancel
          </button>
          <GhostButton tone="danger" onClick={confirm}>Stop everything now</GhostButton>
        </div>
      </div>
    </div>,
    document.body,
  );
}
