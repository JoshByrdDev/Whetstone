import { useCallback, useEffect, useState } from 'react';
import {
  getBug, getTracedHere, setBug, traceBug, confirmLink, rejectLink, reopenLink, addLink,
  FOUND_BY, MISSED_BY,
} from '../api/bugs';
import type { BugView, BugLinkView, TracedHere } from '../api/bugs';
import { Chip, GhostButton } from './ui';

/**
 * Bug tracing on a ticket (docs/bugs-and-experiments-plan.md, 1.9): whether this ticket fixes a bug,
 * where that bug came from - the board's suggestions and the owner's verdicts - and, on any ticket,
 * the bugs that have been traced back to it.
 *
 * The board suggests and only you decide. Nothing a worker or a steward can reach writes here.
 */

const caps = 'text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500';
const pct = (n: number) => `${Math.round(n * 100)}%`;
const short = (sha: string) => sha.slice(0, 7);

/** Ask the board to open another ticket in the drawer. */
const openTicket = (id: number) => window.dispatchEvent(new CustomEvent('open-ticket', { detail: { ticketId: id } }));

function TicketRef({ id, title }: { id: number; title: string | null }) {
  return (
    <button onClick={() => openTicket(id)} className="min-w-0 text-left text-[13px] text-gray-200 hover:text-white transition-colors">
      <span className="font-mono text-gray-500">#{id}</span> <span className="break-words">{title ?? ''}</span>
    </button>
  );
}

/** Who found it and which stage should have, then confirm. Shared by a suggestion and a named source. */
function Verdict({ busy, onConfirm, onCancel, label }: {
  busy: boolean; label: string;
  onConfirm: (foundBy: string, missedBy: string, note: string) => void;
  onCancel: () => void;
}) {
  const [foundBy, setFoundBy] = useState<string>('Owner');
  const [missedBy, setMissedBy] = useState<string>('');
  const [note, setNote] = useState('');
  return (
    <div className="orbit-swap flex flex-col gap-2 mt-2">
      <div>
        <div className="text-[10px] uppercase tracking-wider text-gray-500 mb-1">Found by</div>
        <div className="flex flex-wrap gap-1.5">
          {FOUND_BY.map(f => <Chip key={f.key} on={foundBy === f.key} onClick={() => setFoundBy(f.key)}>{f.label}</Chip>)}
        </div>
      </div>
      <div>
        <div className="text-[10px] uppercase tracking-wider text-gray-500 mb-1">Should have been caught at</div>
        <div className="flex flex-wrap gap-1.5">
          {MISSED_BY.map(m => <Chip key={m} on={missedBy === m} onClick={() => setMissedBy(m)}>{m}</Chip>)}
        </div>
      </div>
      <input
        value={note}
        onChange={e => setNote(e.target.value)}
        placeholder="A note, if it needs one"
        className="w-full min-w-0 rounded border border-hair bg-transparent px-2 py-1 text-[12px] text-gray-200 placeholder:text-gray-600"
      />
      <div className="flex flex-wrap gap-2">
        <GhostButton tone="go" disabled={busy || !missedBy} onClick={() => onConfirm(foundBy, missedBy, note)}>{label}</GhostButton>
        <GhostButton disabled={busy} onClick={onCancel}>Cancel</GhostButton>
      </div>
    </div>
  );
}

function Suggestion({ link, busy, act }: {
  link: BugLinkView; busy: boolean;
  act: (f: () => Promise<unknown>) => void;
}) {
  const [confirming, setConfirming] = useState(false);
  const decided = link.status !== 'Suggested';
  return (
    <div key={link.status} className="orbit-swap rounded-lg border border-hair px-3 py-2.5"
         style={{ opacity: link.status === 'Rejected' ? 0.55 : 1 }}>
      <div className="flex items-start gap-2 min-w-0">
        <div className="min-w-0 flex-1">
          {link.sourceTicketId != null
            ? <TicketRef id={link.sourceTicketId} title={link.sourceTitle} />
            : <span className="text-[13px] text-gray-400">Commits the board did not deliver</span>}
          <div className="mt-1 text-[11px] text-gray-500">
            {pct(link.share)} of the blamed lines
            {link.neighbours && ' · from neighbouring lines'}
            {link.origin === 'Manual' && ' · named by you'}
            {link.escaped && <span className="text-[color:var(--w-blocked)]"> · escaped</span>}
          </div>
        </div>
        <span className={`${caps} flex-none`} style={{
          color: link.status === 'Confirmed' ? 'var(--w-done)' : link.status === 'Rejected' ? 'var(--w-t600)' : 'var(--w-queue)',
        }}>{link.status}</span>
      </div>

      {link.commits.length > 0 && (
        <ul className="mt-2 flex flex-col gap-0.5">
          {link.commits.slice(0, 4).map(c => (
            <li key={c.repo + c.sha} className="text-[11px] text-gray-400 min-w-0 break-words">
              <span className="font-mono text-gray-500">{c.repo ? `${c.repo}@` : ''}{short(c.sha)}</span> {c.subject}
              <span className="text-gray-600"> · {c.lines} line{c.lines === 1 ? '' : 's'}</span>
            </li>
          ))}
          {link.commits.length > 4 && <li className="text-[11px] text-gray-600">and {link.commits.length - 4} more</li>}
        </ul>
      )}

      {link.runs.length > 0 && (
        <div className="mt-1.5 text-[11px] text-gray-500 min-w-0 break-words">
          {link.runs[0].tier === 'exact' ? 'Written by ' : link.runs[0].tier === 'likely' ? 'Most likely written by ' : 'Runs that committed: '}
          {link.runs.slice(0, 3).map((r, i) => (
            <span key={r.runId}>
              {i > 0 && ', '}
              <span className="text-gray-300">{r.skill} pass {r.pass}</span>
              <span className="text-gray-600"> ({new Date(r.startedAt).toLocaleDateString()}{r.model ? `, ${r.model}` : ''})</span>
            </span>
          ))}
        </div>
      )}

      {decided && (link.foundBy || link.missedBy) && (
        <div className="mt-1.5 text-[11px] text-gray-400">
          {link.foundBy && <>Found by {FOUND_BY.find(f => f.key === link.foundBy)?.label ?? link.foundBy}</>}
          {link.missedBy && <> · missed at {link.missedBy}</>}
          {link.note && <span className="text-gray-500"> · {link.note}</span>}
        </div>
      )}

      {confirming
        ? <Verdict busy={busy} label="Confirm" onCancel={() => setConfirming(false)}
                   onConfirm={(f, m, n) => { setConfirming(false); act(() => confirmLink(link.id, f, m, n)); }} />
        : (
          <div className="mt-2 flex flex-wrap gap-2">
            {link.status !== 'Confirmed' && link.sourceTicketId != null &&
              <GhostButton tone="go" disabled={busy} onClick={() => setConfirming(true)}>Confirm</GhostButton>}
            {link.status !== 'Rejected' && <GhostButton disabled={busy} onClick={() => act(() => rejectLink(link.id))}>Reject</GhostButton>}
            {decided && <GhostButton disabled={busy} onClick={() => act(() => reopenLink(link.id))}>Undo</GhostButton>}
          </div>
        )}
    </div>
  );
}

function NameSource({ ticketId, busy, act }: { ticketId: number; busy: boolean; act: (f: () => Promise<unknown>) => void }) {
  const [open, setOpen] = useState(false);
  const [source, setSource] = useState('');
  const id = parseInt(source.replace('#', ''), 10);
  if (!open) return <GhostButton disabled={busy} onClick={() => setOpen(true)}>Name another source</GhostButton>;
  return (
    <div className="orbit-swap rounded-lg border border-hair px-3 py-2.5">
      <input
        value={source}
        onChange={e => setSource(e.target.value)}
        placeholder="The ticket that caused it, e.g. 42"
        inputMode="numeric"
        className="w-full min-w-0 rounded border border-hair bg-transparent px-2 py-1 text-[12px] text-gray-200 placeholder:text-gray-600"
      />
      <Verdict busy={busy || !Number.isFinite(id)} label="Confirm as the cause" onCancel={() => setOpen(false)}
               onConfirm={(f, m, n) => { setOpen(false); setSource(''); act(() => addLink(ticketId, id, f, m, n)); }} />
    </div>
  );
}

/** On a bug ticket: the toggle, and where it came from. On every ticket: bugs traced back to it. */
export function BugTrace({ ticketId }: { ticketId: number }) {
  const [view, setView] = useState<BugView | null>(null);
  const [here, setHere] = useState<TracedHere[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    const [v, h] = await Promise.all([getBug(ticketId).catch(() => null), getTracedHere(ticketId).catch(() => [])]);
    setView(v); setHere(h);
  }, [ticketId]);

  useEffect(() => { setView(null); setHere([]); setError(null); void load(); }, [load]);

  const act = (f: () => Promise<unknown>) => {
    setBusy(true); setError(null);
    f().then(load).catch(e => setError(String(e?.message ?? e))).finally(() => setBusy(false));
  };

  if (!view) return null;
  const confirmed = here.filter(h => h.status === 'Confirmed');

  return (
    <div className="flex flex-col gap-4">
      <div>
        <div className={`${caps} mb-2.5`}>
          Bug
          <span className="normal-case tracking-normal text-gray-600"> · traced to its cause when the fix lands</span>
        </div>
        <label className="flex items-start gap-2.5 cursor-pointer group">
          <input
            type="checkbox"
            checked={view.isBug}
            disabled={busy}
            onChange={e => act(() => setBug(ticketId, e.target.checked))}
            className="mt-[3px] w-3.5 h-3.5 flex-none rounded accent-amber-500 cursor-pointer disabled:cursor-default"
          />
          <span className="min-w-0">
            <span className={`text-[13px] ${view.isBug ? 'text-gray-200' : 'text-gray-400'} group-hover:text-gray-200 transition-colors`}>
              This ticket fixes a bug
            </span>
            <span className="block text-[11px] leading-snug text-gray-600">
              When it is merged or Done, the lines its fix changed are blamed back to the tickets that wrote them, for you to confirm.
            </span>
          </span>
        </label>
      </div>

      {view.isBug && (
        <div>
          <div className={`${caps} mb-2.5`}>
            Where this bug came from
            <span className="normal-case tracking-normal text-gray-600">
              {view.tracedAt ? ` · traced ${new Date(view.tracedAt).toLocaleString()}` : ' · not traced yet'}
            </span>
          </div>
          {view.note && <p className="text-[12px] text-gray-500 mb-2 leading-snug">{view.note}</p>}
          <div className="flex flex-col gap-2">
            {view.links.map(l => <Suggestion key={l.id} link={l} busy={busy} act={act} />)}
          </div>
          <div className="mt-2 flex flex-wrap gap-2 items-start">
            <GhostButton disabled={busy} onClick={() => act(() => traceBug(ticketId))}>
              {view.tracedAt ? 'Trace again' : 'Trace now'}
            </GhostButton>
            <NameSource ticketId={ticketId} busy={busy} act={act} />
          </div>
        </div>
      )}

      {here.length > 0 && (
        <div>
          <div className={`${caps} mb-2.5`}>
            Bugs traced here
            <span className="normal-case tracking-normal text-gray-600">
              {` · ${confirmed.length} confirmed${confirmed.some(h => h.escaped) ? `, ${confirmed.filter(h => h.escaped).length} escaped` : ''}`}
            </span>
          </div>
          <div className="flex flex-col gap-1.5">
            {here.map(h => (
              <div key={h.linkId} className="rounded-lg border border-hair px-3 py-2" style={{ opacity: h.status === 'Confirmed' ? 1 : 0.6 }}>
                <TicketRef id={h.bugTicketId} title={h.bugTitle} />
                <div className="mt-0.5 text-[11px] text-gray-500">
                  {h.status === 'Confirmed' ? 'Confirmed' : 'Suggested'} · {pct(h.share)} of its blamed lines
                  {h.missedBy && <> · missed at {h.missedBy}</>}
                  {h.foundBy && <> · found by {FOUND_BY.find(f => f.key === h.foundBy)?.label ?? h.foundBy}</>}
                  {h.escaped && <span className="text-[color:var(--w-blocked)]"> · escaped</span>}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {error && <p className="text-[12px] text-[color:var(--w-blocked)]">{error}</p>}
    </div>
  );
}
