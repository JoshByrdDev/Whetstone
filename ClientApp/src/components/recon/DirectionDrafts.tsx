import { useEffect, useState } from 'react';
import { chamfer } from '../orbit';
import type { DraftCandidate, ReconDraftView } from './types';

/**
 * Drafted directions in the Scout form (docs/recon-tab-plan.md, "Drafted directions"). Under a
 * Recon gap's or changed area's empty direction: Draft directions runs one agent over what the
 * board knows about the area (ReconDrafter) and lists two or three candidates, each a person and
 * the job they came to do. Use this puts one in the field for the owner to edit; nothing is filed
 * from here. The last draft for the suggestion is read back when the form opens, so reopening the
 * form does not pay for another.
 *
 * The parent is told which candidate went in, so the ticket can record the draft it came from -
 * which is what lets Recon's yield say whether a drafted direction pays.
 */
export function DirectionDrafts({ project, draftKey, onUse, used }: {
  project: string; draftKey: string;
  onUse: (c: DraftCandidate, draftId: number) => void;
  /** The candidate's direction last put in the field, so it is marked as the one in use. */
  used: string | null;
}) {
  const [draft, setDraft] = useState<ReconDraftView | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const url = `/api/recon/${encodeURIComponent(project)}/drafts`;

  useEffect(() => {
    let live = true;
    setDraft(null); setError(null);
    fetch(`${url}?key=${encodeURIComponent(draftKey)}`)
      .then(r => (r.ok ? r.json() : null))
      .then((j: { draft: ReconDraftView | null } | null) => { if (live && j?.draft) setDraft(j.draft); })
      .catch(() => { /* no past draft to show; the button still works */ });
    return () => { live = false; };
  }, [url, draftKey]);

  const ask = async () => {
    setBusy(true); setError(null);
    try {
      const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ key: draftKey }) });
      const j = await r.json().catch(() => null) as { draft?: ReconDraftView; error?: string } | null;
      if (!r.ok || !j?.draft) throw new Error(j?.error ?? `${r.status} ${r.statusText}`);
      setDraft(j.draft);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="mt-2 min-w-0 space-y-2">
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <button type="button" onClick={() => void ask()} disabled={busy}
          className="orbit-tap border border-hair bg-sunk px-2.5 py-1 text-xs text-gray-200 disabled:opacity-50"
          style={{ clipPath: chamfer(6) }}>
          {busy ? 'Drafting…' : draft ? 'Draft again' : 'Draft directions'}
        </button>
        <span className="min-w-0 flex-1 text-[11px] text-gray-500">
          {busy
            ? 'An agent is reading what Recon knows about this area. About a minute.'
            : 'From the area\'s description, its empty cells, and what past directions found.'}
        </span>
      </div>
      {error && <div className="text-[11px] text-red-300" style={{ overflowWrap: 'anywhere' }}>{error}</div>}
      {draft && (
        <div key={draft.id} className="orbit-swap min-w-0 space-y-1.5">
          {draft.candidates.map((c, i) => {
            const current = used === c.direction;
            return (
              <div key={i} className="orbit-pick min-w-0 border border-hair bg-panel px-3 py-2" style={{ clipPath: chamfer(8) }}
                aria-current={current ? 'true' : undefined}>
                <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 text-[11px] text-gray-500">
                  <span className="text-gray-300">{c.signedInAs}</span>
                  <span>{c.width === 'both' ? 'phone and desk' : c.width}</span>
                </div>
                <p className="mt-0.5 min-w-0 text-sm leading-snug text-gray-100" style={{ overflowWrap: 'anywhere' }}>{c.direction}</p>
                <p className="mt-0.5 min-w-0 text-[11px] text-gray-500" style={{ overflowWrap: 'anywhere' }}>{c.why}</p>
                <button type="button" onClick={() => onUse(c, draft.id)}
                  className="orbit-tap mt-1 border border-hair bg-sunk px-2 py-0.5 text-[11px] text-gray-300"
                  style={{ clipPath: chamfer(5) }}>
                  {current ? 'In use' : 'Use this'}
                </button>
              </div>
            );
          })}
          {draft.question && (
            <div className="text-[11px] text-gray-400" style={{ overflowWrap: 'anywhere' }}>
              <span className="text-gray-300">It would draft better knowing:</span> {draft.question}{' '}
              <span className="text-gray-500">Say it in this area&apos;s <span style={{ fontFamily: 'var(--w-font-mono)' }}>about</span> in the project&apos;s discovery block.</span>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
