import { useState } from 'react';
import { GhostButton, PrimaryButton } from '../ui';
import { Section } from '../metrics/parts';
import { chamfer } from '../orbit';
import { Fact, Notice, StateMark, cellPhrase, shortDay } from './parts';
import type { InFlight, ReconCell, ScoutAsk, SetAside, Suggestion, SuggestionsRead } from './types';

/**
 * Scout next (docs/recon-tab-plan.md, "The page" 1): the suggestions in the marts' rule order, each
 * with the short facts that put it where it is. Nothing here ranks or counts - the order is
 * `mart_recon_suggestions.rank`, and the two live filters (an open discovery carrying a row's key,
 * the owner's Not now) were applied by the route.
 *
 * Scout this opens the new-ticket form with the direction in it; Not now sets a row aside until its
 * area's code moves, or for a week or a month. The first row's Scout this is the page's one primary
 * action - a page whose every row wore the accent would have none.
 */

const KIND_WORD: Record<string, string> = { rerun: 'rerun', gap: 'gap', changed: 'changed' };

/** What kind of row it is, said as the cell states say theirs: a rerun and a changed area are changes, a gap is a gap. */
const KIND_STATE: Record<string, string> = { rerun: 'changed', gap: 'gap', changed: 'changed' };

const RULE_WORD: Record<number, string> = {
  1: 'changed since, and a past direction paid off here',
  2: 'a gap whose code changed in the last 30 days',
  3: 'a gap',
  4: 'changed since, but nothing past directions found here was approved',
};

/** The form's title and note for a suggestion, or one of its cells. */
export function askFor(s: Suggestion, cell?: ReconCell): ScoutAsk {
  if (cell) {
    return {
      key: `gap:${s.area}:${cell.role}:${cell.width}`,
      title: `Scout ${s.area} ${cellPhrase(cell)}`,
      direction: `Use ${s.area} ${cellPhrase(cell)}: who are you, and what did you come to do?`,
      note: `From Recon: a gap in ${s.area}, ${cellPhrase(cell)}.`,
    };
  }
  const note = s.kind === 'rerun'
    ? `From Recon: rerunning the direction of #${s.sourceDiscoveryId}, because ${s.area} has changed since.`
    : s.kind === 'changed'
      ? `From Recon: ${s.area} has changed since it was scouted.`
      : `From Recon: a gap in ${s.area}${s.cells.length ? ` - ${s.cells.map(cellPhrase).join('; ')}` : ''}.`;
  return {
    key: s.suggestionKey,
    title: s.kind === 'rerun' ? `Scout ${s.area} again` : s.kind === 'changed' ? `Scout what changed in ${s.area}` : `Scout ${s.area}`,
    direction: s.direction,
    note,
  };
}

export function ScoutNext({ read, error, onScout, onDismiss, onUndismiss, onOpenArea, onOpenTicket, busyKey, cannotFile }: {
  read: SuggestionsRead | null; error: string | null;
  /** Why a discovery cannot be filed here; Scout this is off, and the reason is said once above the list. */
  cannotFile: string | null;
  onScout: (ask: ScoutAsk) => void;
  onDismiss: (key: string, until: Date | null) => void;
  onUndismiss: (key: string) => void;
  onOpenArea: (area: string) => void;
  onOpenTicket: (id: number) => void;
  busyKey: string | null;
}) {
  const [open, setOpen] = useState<string | null>(null);
  const list = read?.suggestions ?? [];

  return (
    <Section title="Scout next" meta={read ? `${list.length} suggestion${list.length === 1 ? '' : 's'}` : undefined}>
      {error && <Notice warn>Could not read the suggestions: {error}</Notice>}
      {!error && !read && <Notice>Loading…</Notice>}
      {read && list.length === 0 && (
        <Notice>
          Nothing to suggest: every cell has been scouted and no area&apos;s code has changed since
          {read.setAside.length ? ', or what is left has been set aside below' : ''}
          {read.inFlight.length ? '. What is being scouted now is listed below' : ''}.
        </Notice>
      )}
      {list.length > 0 && cannotFile && <div className="mb-2"><Notice>Scout this is off: {cannotFile}</Notice></div>}
      {list.length > 0 && (
        <ol className="space-y-2">
          {list.map((s, i) => (
            <SuggestionRow key={s.suggestionKey} s={s} first={i === 0} place={i + 1} open={open === s.suggestionKey}
              onToggle={() => setOpen(o => (o === s.suggestionKey ? null : s.suggestionKey))}
              onScout={onScout} onDismiss={onDismiss} onOpenArea={onOpenArea} onOpenTicket={onOpenTicket}
              busy={busyKey === s.suggestionKey} canFile={!cannotFile} />
          ))}
        </ol>
      )}
      {read && read.inFlight.length > 0 && <InFlightList rows={read.inFlight} onOpenTicket={onOpenTicket} />}
      {read && read.setAside.length > 0 && <SetAsideList rows={read.setAside} onUndismiss={onUndismiss} busyKey={busyKey} />}
    </Section>
  );
}

function SuggestionRow({ s, first, place, open, onToggle, onScout, onDismiss, onOpenArea, onOpenTicket, busy, canFile }: {
  s: Suggestion; first: boolean; place: number; open: boolean; onToggle: () => void; canFile: boolean;
  onScout: (ask: ScoutAsk) => void; onDismiss: (key: string, until: Date | null) => void;
  onOpenArea: (area: string) => void; onOpenTicket: (id: number) => void; busy: boolean;
}) {
  const [asking, setAsking] = useState(false);
  const days = (n: number) => new Date(Date.now() + n * 86400000);
  const expandable = s.kind === 'gap' ? s.cells.length > 0 : s.tickets.length > 0;

  return (
    <li className="orbit-pick min-w-0 border border-hair bg-panel" style={{ clipPath: chamfer(10) }}
      aria-current={open ? 'true' : undefined}>
      <div className="flex min-w-0 flex-col gap-3 px-4 py-3 sm:flex-row sm:items-start">
        <button className="min-w-0 flex-1 text-left" onClick={onToggle} aria-expanded={open}
          title={expandable ? (open ? 'Fold it' : 'Show what it covers') : undefined}>
          <div className="flex min-w-0 flex-wrap items-center gap-x-3 gap-y-1 text-[11px] uppercase tracking-wide text-gray-500">
            <span className="text-gray-300" style={{ fontFamily: 'var(--w-font-mono)' }}>#{place}</span>
            <StateMark state={KIND_STATE[s.kind] ?? 'unknown'} word={KIND_WORD[s.kind] ?? s.kind} />
            <span className="min-w-0 normal-case text-gray-200">{s.area}</span>
            {s.cooling && <StateMark state="cooling" />}
            <span className="min-w-0 normal-case tracking-normal text-gray-600">rule {s.rule}: {RULE_WORD[s.rule] ?? ''}</span>
          </div>
          <p className="mt-1.5 min-w-0 text-sm leading-snug text-gray-100" style={{ overflowWrap: 'anywhere' }}>{s.direction}</p>
          <div className="mt-2 flex min-w-0 flex-wrap gap-1.5">
            {s.reasons.map(r => <Fact key={r}>{r}</Fact>)}
          </div>
        </button>
        <div key={asking ? 'ask' : 'act'} className="orbit-swap flex flex-none flex-wrap items-center gap-2">
          {asking ? (
            <>
              <span className="text-[11px] text-gray-500">Set aside</span>
              <GhostButton disabled={busy} onClick={() => { setAsking(false); onDismiss(s.suggestionKey, null); }}
                title="Back as soon as a commit touches this area's code">
                {s.areaHeadCommit ? 'Until its code changes' : 'Until I undo it'}
              </GhostButton>
              <GhostButton disabled={busy} onClick={() => { setAsking(false); onDismiss(s.suggestionKey, days(7)); }}>A week</GhostButton>
              <GhostButton disabled={busy} onClick={() => { setAsking(false); onDismiss(s.suggestionKey, days(30)); }}>A month</GhostButton>
              <button className="orbit-tap text-[11px] text-gray-500 underline" onClick={() => setAsking(false)}>Keep it</button>
            </>
          ) : (
            <>
              {first
                ? <PrimaryButton onClick={() => onScout(askFor(s))} disabled={busy || !canFile}>Scout this</PrimaryButton>
                : <GhostButton tone="go" onClick={() => onScout(askFor(s))} disabled={busy || !canFile}>Scout this</GhostButton>}
              <GhostButton onClick={() => setAsking(true)} disabled={busy}>Not now</GhostButton>
            </>
          )}
        </div>
      </div>
      {open && (
        <div key="open" className="orbit-swap space-y-2 border-t border-hair px-4 py-3 text-xs text-gray-400">
          {s.kind === 'gap' && s.cells.length > 0 && (
            <>
              <div className="text-[11px] uppercase tracking-wide text-gray-500">The {s.cells.length} cell{s.cells.length === 1 ? '' : 's'} nobody has scouted</div>
              <ul className="space-y-1.5">
                {s.cells.map(c => (
                  <li key={`${c.role}:${c.width}`} className="flex min-w-0 flex-wrap items-center gap-2">
                    <StateMark state="gap" />
                    <span className="min-w-0 flex-1 text-gray-300">{s.area} {cellPhrase(c)}</span>
                    <button className="orbit-tap border border-hair bg-sunk px-2 py-0.5 text-[11px] text-gray-300"
                      style={{ clipPath: chamfer(5) }} disabled={!canFile} onClick={() => onScout(askFor(s, c))}>
                      Scout just this
                    </button>
                  </li>
                ))}
              </ul>
            </>
          )}
          {s.tickets.length > 0 && (
            <div className="flex min-w-0 flex-wrap items-center gap-1.5">
              <span>Changed by</span>
              {s.tickets.map(t => (
                <button key={t} className="orbit-tap text-amber-300 underline" onClick={() => onOpenTicket(t)}>#{t}</button>
              ))}
            </div>
          )}
          {s.kind === 'rerun' && s.sourceDiscoveryId != null && (
            <div>
              Reruns the direction of{' '}
              <button className="orbit-tap text-amber-300 underline" onClick={() => onOpenTicket(s.sourceDiscoveryId!)}>#{s.sourceDiscoveryId}</button>
              , word for word. Chart writes a fresh route against the product as it is now.
            </div>
          )}
          <button className="orbit-tap text-[11px] text-gray-300 underline" onClick={() => onOpenArea(s.area)}>
            Open {s.area} in the coverage
          </button>
        </div>
      )}
    </li>
  );
}

function InFlightList({ rows, onOpenTicket }: { rows: InFlight[]; onOpenTicket: (id: number) => void }) {
  return (
    <div className="mt-3 min-w-0 text-xs text-gray-500">
      <div className="mb-1 text-[11px] uppercase tracking-wide">Being scouted now, so not suggested</div>
      <ul className="space-y-1">
        {rows.map(r => (
          <li key={`${r.key}-${r.ticketId}`} className="flex min-w-0 flex-wrap items-baseline gap-x-2">
            <button className="orbit-tap text-amber-300 underline" onClick={() => onOpenTicket(r.ticketId)}>#{r.ticketId}</button>
            <span className="min-w-0 flex-1 truncate text-gray-400">{r.title ?? r.key}</span>
            <span>{r.phase}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

function SetAsideList({ rows, onUndismiss, busyKey }: { rows: SetAside[]; onUndismiss: (key: string) => void; busyKey: string | null }) {
  return (
    <div className="mt-3 min-w-0 text-xs text-gray-500">
      <div className="mb-1 text-[11px] uppercase tracking-wide">Set aside</div>
      <ul className="space-y-1.5">
        {rows.map(r => (
          <li key={r.key} className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5">
            <span className="text-gray-300">{r.area ?? r.key}</span>
            <span className="min-w-0 flex-1 text-gray-500" style={{ overflowWrap: 'anywhere' }}>
              {r.kind ?? ''} · {r.until ? `until ${shortDay(r.until)}` : 'until its code changes or you undo it'} · set aside {shortDay(r.at)}
            </span>
            <button className="orbit-tap text-amber-300 underline disabled:opacity-40" disabled={busyKey === r.key}
              onClick={() => onUndismiss(r.key)}>Undo</button>
          </li>
        ))}
      </ul>
    </div>
  );
}
