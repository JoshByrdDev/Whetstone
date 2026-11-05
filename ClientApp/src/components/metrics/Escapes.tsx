import { useState } from 'react';
import { Chip, GhostButton } from '../ui';
import { MONO } from '../orbit';
import { classifyEscape, escapesUrl, type EscapeList, type EscapeView } from '../../api/escapes';
import type { EscapeRow } from './types';
import { Empty, Meter, Section, Sheet, Stat, ago, pct, phaseName, useJson } from './parts';

/**
 * What got past the whole pipeline: finished work a person sent back after it reached review
 * (mart_escapes), by the kind of miss they said it was. The rate is per hand-off to review. A
 * requirement change is counted apart - the owner changing their mind is not a miss - and the
 * escapes nobody has classified yet are listed underneath, to be classified in place.
 */
const KINDS: { key: string; label: string; field: keyof EscapeRow; swatch: string; note: string }[] = [
  { key: 'OutsideContract', label: 'Outside contract', field: 'outsideContract', swatch: 'bg-sky-500', note: 'a stand-in or an assumption about a service the fleet cannot reach' },
  { key: 'InheritedBehaviour', label: 'Inherited behaviour', field: 'inheritedBehaviour', swatch: 'bg-violet-500', note: 'old behaviour or data a new rule depends on' },
  { key: 'UiPolish', label: 'UI polish', field: 'uiPolish', swatch: 'bg-amber-500', note: 'how a screen looks or moves' },
  { key: 'Logic', label: 'Logic', field: 'logic', swatch: 'bg-red-500', note: 'a plain defect' },
  { key: 'Other', label: 'Other', field: 'other', swatch: 'bg-gray-500', note: 'anything else the pipeline missed' },
  { key: 'RequirementChange', label: 'Requirement change', field: 'requirementChanges', swatch: 'bg-emerald-500', note: 'the owner asked for something new - not a miss' },
];
const SOURCES: { key: string; label: string }[] = [
  { key: 'Owner', label: 'Owner' }, { key: 'PrReview', label: 'PR review' }, { key: 'Other', label: 'Other' },
];
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
  const open = useJson<EscapeList>(escapesUrl(project, true), bump + saved);
  const unclassified = open.data?.escapes ?? [];

  return (
    <Section title="Escapes after review"
      meta={all ? `${all.misses} missed · ${all.requirementChanges} requirement changes · ${all.handoffs} hand-offs` : undefined}>
      {!all ? (
        <Empty>Nothing reached review in this window. An escape is logged when someone sends finished work back.</Empty>
      ) : (
        <>
          <div className="mb-3 flex flex-wrap gap-3">
            <Stat label="Misses per hand-off" value={pct(all.missesPerHandoff)} muted={!all.handoffs}
              context={`${all.misses} sent back of ${all.handoffs} hand-offs to review`} />
            <Stat label="Misses per ticket" value={all.missesPerTicket == null ? '—' : all.missesPerTicket.toFixed(2)} muted={!all.ticketsReviewed}
              context={`${all.ticketsReviewed} tickets reached review`} />
            <Stat label="Unclassified" value={String(all.unclassified)} muted={!all.unclassified}
              context={all.unclassified ? 'counted as misses until someone says otherwise' : 'every escape has a kind'} />
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

      {unclassified.length > 0 && (
        <div className="min-w-0">
          <div className="mb-2 text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
            To classify ({unclassified.length})
          </div>
          <ul className="space-y-2">
            {unclassified.map(e => <Unclassified key={e.id} e={e} onSaved={() => setSaved(s => s + 1)} />)}
          </ul>
        </div>
      )}
      {open.error && <Empty>Could not load the escapes to classify: {open.error}</Empty>}
    </Section>
  );
}

/** One escape nobody has classified yet: what came back and why, and the two calls to make about it. */
function Unclassified({ e, onSaved }: { e: EscapeView; onSaved: () => void }) {
  const [kind, setKind] = useState<string | null>(null);
  const [source, setSource] = useState<string | null>('Owner');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const why = e.note ?? e.reason ?? '';

  const save = async () => {
    if (!kind) return;
    setBusy(true); setError(null);
    try { await classifyEscape(e.id, kind, source); onSaved(); }
    catch (err) { setError(String(err instanceof Error ? err.message : err)); setBusy(false); }
  };

  return (
    <li className="min-w-0">
      <Sheet>
        <div className="flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5 text-xs">
          <span className="text-gray-500" style={{ fontFamily: MONO }}>#{e.ticketId}</span>
          <span className="min-w-0 flex-1 truncate text-gray-200">{e.ticketTitle ?? 'Untitled ticket'}</span>
          <span className="text-[11px] text-gray-500">{phaseName(e.fromPhase)} → {phaseName(e.toPhase)} · {ago(e.at)}</span>
        </div>
        {why && <p className="mt-1.5 line-clamp-3 min-w-0 break-words text-xs leading-relaxed text-gray-400">{why}</p>}
        <div className="mt-2 flex flex-wrap gap-1.5" role="group" aria-label="Kind of miss">
          {KINDS.map(k => <Chip key={k.key} on={kind === k.key} title={k.note} onClick={() => setKind(k.key)}>{k.label}</Chip>)}
        </div>
        <div className="mt-1.5 flex flex-wrap items-center gap-1.5" role="group" aria-label="Found by">
          <span className="text-[11px] text-gray-500">Found by</span>
          {SOURCES.map(s => <Chip key={s.key} on={source === s.key} onClick={() => setSource(s.key)}>{s.label}</Chip>)}
          <span className="ml-auto">
            <GhostButton onClick={save} disabled={!kind || busy} title={kind ? 'Save this classification' : 'Pick a kind first'}>
              {busy ? 'Saving…' : 'Save'}
            </GhostButton>
          </span>
        </div>
        {error && <p className="mt-1.5 text-xs text-red-300">{error}</p>}
      </Sheet>
    </li>
  );
}
