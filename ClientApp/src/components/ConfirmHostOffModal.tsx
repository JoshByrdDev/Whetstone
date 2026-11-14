import { useEffect } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';
import { GhostButton } from './ui';
import type { WorkerHostRun } from '../hooks/useWorkerHosts';

/**
 * The are-you-sure in front of switching a worker host Off while it has runs out. Shown only then:
 * Off with nothing out, and On, stay one click.
 *
 * Off kills nothing. It takes the host out of dispatch - the orchestrator stops claiming for it and
 * claim-next refuses it - while every run already there carries on, finishes and hands its branch
 * back (only the hard Stop orders a worker host's runs killed). So the dialog says that, and offers
 * "stop after current runs" as the gentler choice for what it actually is: the same no-new-work, but
 * the host stays On and shows what it is still finishing, and the board switches it Off by itself
 * once the last run is back - nothing left to remember to come back for.
 *
 * Rendered only while open, as `useVeil` needs.
 */
export function ConfirmHostOffModal({ name, runs, onOff, onStopAfter, onClose }: {
  name: string;
  runs: WorkerHostRun[];
  onOff: () => void;
  onStopAfter: () => void;
  onClose: () => void;
}) {
  const { fx, leave, dismiss } = useVeil(onClose);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss]);

  const then = (act: () => void) => leave(() => { onClose(); act(); });
  const n = runs.length;
  const them = n === 1 ? 'it' : 'them';

  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-host-off-title"
        className="orbit-modal relative w-full max-w-md flex flex-col overflow-hidden"
      >
        <div className="px-5 py-3 border-b border-hair bg-panel">
          <h2 id="confirm-host-off-title" className="text-sm font-semibold text-gray-200 uppercase tracking-wider break-words">
            Switch {name} off?
          </h2>
        </div>
        <div className="px-5 py-4 space-y-3 text-[13px] text-gray-300">
          <p>
            {name} has {n} run{n === 1 ? '' : 's'} out. Switching it off stops the board giving it new work. It does
            not stop or kill what it is running: {n === 1 ? 'that run carries' : 'those runs carry'} on, finish, and
            hand {n === 1 ? 'its branch' : 'their branches'} back as usual.
          </p>
          <ul className="max-h-40 overflow-auto rounded border border-hairsoft">
            {runs.map(r => (
              <li key={r.runId} className="flex items-baseline gap-2 px-2 py-1 text-[11px] border-b border-hairsoft last:border-0">
                <span className="font-mono text-gray-600 flex-none">#{r.slot}</span>
                <span className="font-mono text-amber-400 flex-none">{r.ticketId}</span>
                <span className="text-gray-400 min-w-0 truncate">{r.project ?? ''}</span>
              </li>
            ))}
          </ul>
          <p className="text-[12px] text-gray-500">
            <span className="text-gray-300">Stop after current runs</span> takes no new work either, but keeps {name} on
            and shows what it is still finishing, then switches it off by itself once {them === 'it' ? 'that run is' : 'they are'} back.
          </p>
        </div>
        <div className="flex flex-wrap items-center justify-end gap-2 px-5 py-3 border-t border-hair bg-panel">
          <button onClick={dismiss} autoFocus className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200 transition-colors">
            Cancel
          </button>
          <GhostButton tone="hold" onClick={() => then(onStopAfter)}>Stop after current runs</GhostButton>
          <GhostButton onClick={() => then(onOff)}>Switch off</GhostButton>
        </div>
      </div>
    </div>,
    document.body,
  );
}
