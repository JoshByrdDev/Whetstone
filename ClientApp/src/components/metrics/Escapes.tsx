import { useState } from 'react';
import { Chip, GhostButton } from '../ui';
import { MONO } from '../orbit';
import { classifyEscape, escapesUrl, type EscapeList, type EscapeView } from '../../api/escapes';
import type { EscapeRow } from './types';
import { Empty, Meter, Section, Sheet, Stat, ago, pct, phaseName, useJson } from './parts';

/**
 * What got past the whole pipeline: finished work a person sent back after it reached review
 * (mart_escapes), by the kind of miss it was. The implementation run that picks up a send-back
 * classifies it before it can hand off, so this panel only reports: the counts, the trend, and each
 * escape with the run's reason. A person can correct one from its row, and that is recorded as
 * theirs. A requirement change is counted apart - the owner changing their mind is not a miss - and
 * an escape from before runs classified their own stays unclassified rather than counted as one.
 */
const KINDS: { key: string; label: string; field: keyof EscapeRow; swatch: string; note: string }[] = [
  { key: 'OutsideContract', label: 'Outside contract', field: 'outsideContract', swatch: 'bg-sky-500', note: 'a stand-in or an assumption about a service the fleet cannot reach' },
  { key: 'InheritedBehaviour', label: 'Inherited behaviour', field: 'inheritedBehaviour', swatch: 'bg-violet-500', note: 'old behaviour or data a new rule depends on' },
  { key: 'UiPolish', label: 'UI polish', field: 'uiPolish', swatch: 'bg-amber-500', note: 'how a screen looks or moves' },
  { key: 'Logic', label: 'Logic', field: 'logic', swatch: 'bg-red-500', note: 'a plain defect' },
  { key: 'Other', label: 'Other', field: 'other', swatch: 'bg-gray-500', note: 'anything else the pipeline missed' },
  { key: 'RequirementChange', label: 'Requirement change', field: 'requirementChanges', swatch: 'bg-emerald-500', note: 'the owner asked for something new - not a miss' },
];
const kindLabel = (k: string | null) => (k ? KINDS.find(x => x.key === k)?.label ?? k : 'Unclassified');
const SOURCES: { key: string; label: string }[] = [
  { key: 'Owner', label: 'Owner' }, { key: 'PrReview', label: 'PR review' }, { key: 'Other', label: 'Other' },
];
const sourceLabel = (s: string | null) => (s ? SOURCES.find(x => x.key === s)?.label ?? s : null);
const BY: { key: string; label: string }[] = [
  { key: 'week', label: 'Week' },
  { key: 'model', label: 'Built by model' },
  { key: 'effort', label: 'Built at effort' },
  { key: 'skill_version', label: 'Implement skill' },
  { key: 'provider', label: 'Provider' },
];

export function Escapes({ rows, project, bump }: { rows: EscapeRow[]; project: string | null; bump: number }) {
  const [by, setBy] = useState('week');
  const [saved, setSaved] = useState(0);
  const all = rows.find(r => r.dimension === 'all');
  const shown = rows.filter(r => r.dimension === by)
    .sort((a, b) => by === 'week' ? a.dimValue.localeCompare(b.dimValue) : b.handoffs - a.handoffs);
  const most = Math.max(1, ...shown.map(r => r.misses));
  const list = useJson<EscapeList>(escapesUrl(project, false), bump + saved);
  const escapes = list.data?.escapes ?? [];

  return (
    <Section title="Escapes after review"
      meta={all ? `${all.misses} missed · ${all.requirementChanges} requirement changes · ${all.handoffs} hand-offs` : undefined}>
      {!all ? (
        <Empty>Nothing reached review in this window. An escape is logged when someone sends finished work back.</Empty>
      ) : (
        <>
          <div className="mb-3 flex flex-wrap gap-3">
            <Stat label="Misses per hand-off" value={pct(all.missesPerHandoff)} muted={!all.handoffs}
              context={`${all.misses} missed of ${all.handoffs} hand-offs to review`} />
            <Stat label="Misses per ticket" value={all.missesPerTicket == null ? '—' : all.missesPerTicket.toFixed(2)} muted={!all.ticketsReviewed}
              context={`${all.ticketsReviewed} tickets reached review`} />
            <Stat label="Corrected by a person" value={String(all.overridden ?? 0)} muted={!all.overridden}
              context={all.unclassified ? `${all.unclassified} older escapes unclassified, counted as neither` : 'the run\'s own call, overridden'} />
          </div>

          <Sheet className="mb-3">
            <ul className="space-y-1.5 text-xs">
              {KINDS.map(k => {
                const n = Number(all[k.field] ?? 0);
                return (
                  <li key={k.key} className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)_2.5rem] items-center gap-x-3" title={k.note}>
                    <span className="min-w-0 truncate text-gray-300">{k.label}</span>
                    <Meter value={n} max={Math.max(1, all.escapes)} swatch={k.swatch} title={`${n} of ${all.escapes} escapes: ${k.note}`} />
                    <span className={`text-right ${n ? 'text-gray-100' : 'text-gray-600'}`} style={{ fontFamily: MONO }}>{n}</span>
                  </li>
                );
              })}
              {all.unclassified > 0 && (
                <li className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)_2.5rem] items-center gap-x-3"
                  title="From before runs classified their own - counted as no kind">
                  <span className="min-w-0 truncate text-gray-500">Unclassified</span>
                  <span className="text-[10.5px] text-gray-600">older, counted as no kind</span>
                  <span className="text-right text-gray-500" style={{ fontFamily: MONO }}>{all.unclassified}</span>
                </li>
              )}
            </ul>
          </Sheet>

          <div className="mb-2 flex flex-wrap gap-1.5">
            {BY.map(b => <Chip key={b.key} on={by === b.key} onClick={() => setBy(b.key)}>{b.label}</Chip>)}
          </div>
          {shown.length === 0 ? <Empty>Nothing to split.</Empty> : (
            <Sheet className="orbit-swap mb-3" key={by}>
              <ul className="space-y-1.5 text-xs">
                {shown.map(r => (
                  <li key={r.dimValue} className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)_3rem_3rem] items-center gap-x-3">
                    <span className="min-w-0 truncate text-gray-300" style={by === 'week' || by.endsWith('version') ? { fontFamily: MONO } : undefined}>{r.dimValue}</span>
                    <Meter value={r.misses} max={most} swatch="bg-red-500" title={`${r.misses} missed of ${r.handoffs} hand-offs`} />
                    <span className="text-right text-gray-100" style={{ fontFamily: MONO }} title="misses per hand-off">{pct(r.missesPerHandoff)}</span>
                    <span className="text-right text-gray-500" style={{ fontFamily: MONO }} title="hand-offs to review">{r.handoffs}</span>
                  </li>
                ))}
                <li className="pt-1 text-[10.5px] text-gray-600">Columns: misses · per hand-off · hand-offs to review.</li>
              </ul>
            </Sheet>
          )}
        </>
      )}

      {escapes.length > 0 && (
        <div className="min-w-0">
          <div className="mb-2 text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
            Escapes ({escapes.length})
          </div>
          <ul className="space-y-2">
            {escapes.map(e => <EscapeItem key={e.id} e={e} onSaved={() => setSaved(s => s + 1)} />)}
          </ul>
        </div>
      )}
      {list.error && <Empty>Could not load the escapes: {list.error}</Empty>}
    </Section>
  );
}

/** One escape: what came back, the kind the run called it, its reason - and a person's correction, behind Edit. */
function EscapeItem({ e, onSaved }: { e: EscapeView; onSaved: () => void }) {
  const [editing, setEditing] = useState(false);
  const who = e.kind == null ? null
    : e.classifiedBy === 'Worker' ? (e.classifiedByRunId != null ? `run ${e.classifiedByRunId}` : 'a run')
    : e.overridden ? 'corrected by a person' : 'a person';

  return (
    <li className="min-w-0">
      <Sheet>
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5 text-xs">
          <span className="text-gray-500" style={{ fontFamily: MONO }}>#{e.ticketId}</span>
          <span className="min-w-0 flex-1 truncate text-gray-200">{e.ticketTitle ?? 'Untitled ticket'}</span>
          <span className="text-[11px] text-gray-500">{phaseName(e.fromPhase)} → {phaseName(e.toPhase)} · {ago(e.at)}</span>
        </div>
        <div className="mt-1.5 flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-xs">
          <span className={e.kind ? 'text-gray-100' : 'text-gray-500'}>{kindLabel(e.kind)}</span>
          {sourceLabel(e.source) && <span className="text-gray-500">· found by {sourceLabel(e.source)}</span>}
          {who && <span className={e.overridden ? 'text-amber-300' : 'text-gray-600'}>· {who}</span>}
          {e.backfilled && <span className="text-gray-600">· from history</span>}
          <span className="ml-auto">
            <GhostButton onClick={() => setEditing(v => !v)} title="Correct this escape's kind or source">
              {editing ? 'Close' : 'Edit'}
            </GhostButton>
          </span>
        </div>
        {e.why
          ? <p className={`mt-1.5 min-w-0 break-words text-xs leading-relaxed ${e.kind === 'RequirementChange' ? 'text-emerald-200' : 'text-gray-400'}`}>{e.why}</p>
          : (e.note ?? e.reason) && <p className="mt-1.5 line-clamp-2 min-w-0 break-words text-xs leading-relaxed text-gray-500">{e.note ?? e.reason}</p>}
        {editing && <Correct e={e} onSaved={() => { setEditing(false); onSaved(); }} />}
      </Sheet>
    </li>
  );
}

/** A person's correction: the kind and who found it. Saved as a person's, overriding the run's call. */
function Correct({ e, onSaved }: { e: EscapeView; onSaved: () => void }) {
  const [kind, setKind] = useState<string | null>(e.kind);
  const [source, setSource] = useState<string | null>(e.source ?? 'Owner');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const save = async () => {
    if (!kind) return;
    setBusy(true); setError(null);
    try { await classifyEscape(e.id, kind, source, e.why ?? undefined); onSaved(); }
    catch (err) { setError(String(err instanceof Error ? err.message : err)); setBusy(false); }
  };

  return (
    <div className="orbit-swap mt-2 border-t border-hair pt-2">
      <div className="flex flex-wrap gap-1.5" role="group" aria-label="Kind of miss">
        {KINDS.map(k => <Chip key={k.key} on={kind === k.key} title={k.note} onClick={() => setKind(k.key)}>{k.label}</Chip>)}
      </div>
      <div className="mt-1.5 flex flex-wrap items-center gap-1.5" role="group" aria-label="Found by">
        <span className="text-[11px] text-gray-500">Found by</span>
        {SOURCES.map(s => <Chip key={s.key} on={source === s.key} onClick={() => setSource(s.key)}>{s.label}</Chip>)}
        <span className="ml-auto">
          <GhostButton onClick={save} disabled={!kind || busy} title="Save this correction as yours">
            {busy ? 'Saving…' : 'Save'}
          </GhostButton>
        </span>
      </div>
      {error && <p className="mt-1.5 text-xs text-red-300">{error}</p>}
    </div>
  );
}
