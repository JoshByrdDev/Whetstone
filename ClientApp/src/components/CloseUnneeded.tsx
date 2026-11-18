import { useState } from 'react';
import { usePop } from '../hooks/usePop';
import type { Resolution } from '../types/tickets';

/**
 * Close a ticket that needs no work - not a bug, already fixed, a duplicate, not needed - with why
 * (Ticket.Resolution, POST /api/tickets/{id}/close). What Delete was used for, without taking the
 * ticket's history out of the metrics: a bug filed and then thrown away still counts as filed.
 */
export const RESOLUTIONS: { key: Resolution; label: string; hint: string }[] = [
  { key: 'NotABug', label: 'Not a bug', hint: 'the behaviour is intended, or the report misread it' },
  { key: 'AlreadyFixed', label: 'Already fixed', hint: 'the base already does what it asks' },
  { key: 'Duplicate', label: 'Duplicate', hint: 'another ticket covers it' },
  { key: 'NotNeeded', label: 'Not needed', hint: 'a decision or a change since made it moot' },
];

export const resolutionLabel = (r: Resolution | null | undefined) => RESOLUTIONS.find(x => x.key === r)?.label ?? null;

export function CloseUnneeded({ busy, onClose }: {
  busy: boolean;
  onClose: (resolution: Resolution, reason: string, duplicateOf?: number) => Promise<unknown>;
}) {
  const [open, setOpen] = useState(false);
  const pop = usePop(open);
  const [resolution, setResolution] = useState<Resolution>('NotABug');
  const [reason, setReason] = useState('');
  const [dup, setDup] = useState('');
  const dupId = Number.parseInt(dup.replace('#', ''), 10);
  const ready = reason.trim().length > 0 && (resolution !== 'Duplicate' || Number.isFinite(dupId));

  return (
    <div className="relative">
      <button onClick={() => setOpen(!open)} disabled={busy}
        className="orbit-tap text-[11px] text-gray-400 hover:text-gray-200 disabled:opacity-50"
        title="Close it as needing no work. It stays on the record, and in the metrics.">
        Close, no work ▾
      </button>
      {pop.show && (
        <div className="orbit-pop absolute right-0 bottom-full mb-1 z-20 w-72 max-w-[calc(100vw-2rem)] bg-gray-900 border border-hair rounded-lg shadow-xl p-3 flex flex-col gap-2"
             data-fx={pop.fx} data-side="above">
          {RESOLUTIONS.map(r => (
            <label key={r.key} className="flex items-start gap-2 text-xs text-gray-300 cursor-pointer">
              <input type="radio" name="resolution" className="mt-0.5" checked={resolution === r.key} onChange={() => setResolution(r.key)} />
              <span className="min-w-0"><span className="text-gray-100">{r.label}</span> <span className="text-gray-500">- {r.hint}</span></span>
            </label>
          ))}
          {resolution === 'Duplicate' && (
            <input value={dup} onChange={e => setDup(e.target.value)} placeholder="Duplicate of ticket #"
              className="min-w-0 bg-gray-950 border border-hair rounded px-2 py-1 text-xs text-gray-200" />
          )}
          <textarea value={reason} onChange={e => setReason(e.target.value)} rows={2}
            placeholder="Why it needs no work"
            className="min-w-0 bg-gray-950 border border-hair rounded px-2 py-1 text-xs text-gray-200" />
          <button disabled={busy || !ready}
            onClick={async () => { await onClose(resolution, reason.trim(), resolution === 'Duplicate' ? dupId : undefined); setOpen(false); }}
            className="orbit-primary self-end btn-act bg-amber-500 text-void px-3 py-1 text-xs disabled:bg-gray-700 disabled:text-gray-500">
            Close ticket
          </button>
        </div>
      )}
    </div>
  );
}
