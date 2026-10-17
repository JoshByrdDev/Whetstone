import { useEffect, useMemo, useState } from 'react';
import { GhostButton } from './ui';
import { BODY, MONO, TIER } from './orbit';
import { inline } from './Markdown';
import { OptionRow, label } from './stewardParts';
import { decideStewardUnit, getUnitDecisions } from '../api/steward';
import type { UnitDecision, UnitDecisionChoice } from '../api/steward';

/**
 * A unit's open decisions, answered where they are read.
 *
 * A spec leaves its choices open under "To decide", each with its options and a recommended pick.
 * They used to be answered as one question that pointed at the spec - read it over there, come back
 * and say "all of them" or type exceptions. Here each one is shown as the spec writes it, with the
 * pick already chosen, so the commonest answer is one press and an exception is a click on the row
 * it belongs to. What is sent is written into the spec under "Decided", where every run files from.
 *
 * Three places ask for it, and say which by `mode`: agreeing a draft (`agree`, which may leave some
 * open for later), settling an agreed unit's leftovers (`settle`), and a steward's question that
 * named decisions (`answer`, which settles every one it asked and answers the question with them).
 */

type Take = 'pick' | 'letters' | 'own' | 'later';
interface Row { take: Take; letters: string[]; note: string; noting: boolean }

export function DecisionSheet({ project, unit, only, mode, answering, onDone, onCancel }: {
  project: string; unit: number;
  /** Just these decisions, in this order; every open one when left out. */
  only?: number[];
  mode: 'agree' | 'settle' | 'answer';
  answering?: string;
  onDone: () => Promise<void>;
  onCancel?: () => void;
}) {
  const [decisions, setDecisions] = useState<UnitDecision[] | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [rows, setRows] = useState<Record<number, Row>>({});
  const [busy, setBusy] = useState(false);

  const start = (d: UnitDecision): Row => ({
    // The pick is the starting answer; with none, agreeing leaves it open and a question waits for a choice.
    take: d.pick ? 'pick' : mode === 'answer' ? 'letters' : 'later',
    letters: d.pickLetters, note: '', noting: false,
  });

  useEffect(() => {
    let live = true;
    getUnitDecisions(project, unit)
      .then(r => {
        if (!live) return;
        const open = only ? only.map(n => r.open.find(d => d.number === n)).filter((d): d is UnitDecision => !!d) : r.open;
        setDecisions(open);
        setRows(Object.fromEntries(open.map(d => [d.number, start(d)])));
      })
      .catch(err => live && setFailed(err instanceof Error ? err.message : String(err)));
    return () => { live = false; };
    // `only` is a fresh array each render; what it names is what matters.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project, unit, only?.join(',')]);

  const set = (n: number, change: Partial<Row>) => setRows(r => ({ ...r, [n]: { ...r[n], ...change } }));

  // What each row answers, or null for one left open; undefined for a row that has no answer yet.
  const choice = (d: UnitDecision, r: Row): UnitDecisionChoice | null | undefined => {
    const note = r.note.trim() || null;
    if (r.take === 'later') return null;
    if (r.take === 'own') return note ? { number: d.number, note } : undefined;
    if (r.take === 'pick') return { number: d.number, note };
    return r.letters.length > 0 ? { number: d.number, option: r.letters.join(','), note } : undefined;
  };

  const summary = useMemo(() => {
    const all = decisions ?? [];
    const answered = (d: UnitDecision) => !!rows[d.number] && choice(d, rows[d.number]) !== undefined;
    const of = (t: Take) => all.filter(d => rows[d.number]?.take === t && answered(d)).length;
    return { picked: of('pick'), changed: of('letters') + of('own'), later: of('later'),
      missing: all.filter(d => rows[d.number] && !answered(d)).length };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [decisions, rows]);

  if (failed && !decisions) return <div style={{ font: `400 12px ${BODY}`, color: TIER.blocked.c }}>{failed}</div>;
  if (!decisions) return <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t500)' }}>Reading its decisions...</div>;
  if (decisions.length === 0 && mode !== 'agree') {
    return <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t500)' }}>Its spec leaves nothing open any more: these have been decided.</div>;
  }

  const chosen = decisions.map(d => choice(d, rows[d.number])).filter((c): c is UnitDecisionChoice => !!c);
  const ready = summary.missing === 0 && (mode === 'agree' || chosen.length > 0);
  const send = async () => {
    setBusy(true); setFailed(null);
    try {
      await decideStewardUnit(project, unit, { decisions: chosen, agree: mode === 'agree', answering: answering ?? null });
      await onDone();
    } catch (err) { setFailed(err instanceof Error ? err.message : String(err)); setBusy(false); }
  };
  const allPicks = () => setRows(Object.fromEntries(decisions.map(d => [d.number, { ...start(d), take: d.pick ? 'pick' : rows[d.number].take }])));

  const verb = mode === 'agree' ? `Agree unit ${unit}${chosen.length ? ' with these' : ''}`
    : mode === 'answer' ? 'Answer with these' : `Record ${chosen.length === 1 ? 'this decision' : `these ${chosen.length} decisions`}`;

  return (
    <div>
      {decisions.length > 0 && (
        <div className="flex items-center gap-x-3 gap-y-2 flex-wrap" style={{ marginBottom: 10 }}>
          <span style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>
            {decisions.length} decision{decisions.length === 1 ? '' : 's'}: {summary.picked} as picked
            {summary.changed > 0 && <>, {summary.changed} your own</>}
            {summary.later > 0 && <>, {summary.later} left open</>}
          </span>
          {summary.picked < decisions.filter(d => d.pick).length && (
            <GhostButton onClick={allPicks} title="Every row back to the spec's own pick">Take every pick</GhostButton>
          )}
        </div>
      )}

      <div className="flex flex-col">
        {decisions.map(d => (
          <DecisionRow key={d.number} d={d} row={rows[d.number]} mode={mode} onChange={c => set(d.number, c)} />
        ))}
      </div>

      <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 12 }}>
        <GhostButton tone="go" onClick={send} disabled={busy || !ready}>{busy ? 'Recording' : verb}</GhostButton>
        {onCancel && <GhostButton disabled={busy} onClick={onCancel}>Not yet</GhostButton>}
        {summary.missing > 0 && (
          <span style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)' }}>
            {summary.missing} still need{summary.missing === 1 ? 's' : ''} an answer.
          </span>
        )}
      </div>
      {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 6 }}>{failed}</div>}
    </div>
  );
}

function DecisionRow({ d, row, mode, onChange }: {
  d: UnitDecision; row: Row; mode: 'agree' | 'settle' | 'answer'; onChange: (c: Partial<Row>) => void;
}) {
  if (!row) return null;
  // A decision that asks two things at once picks two letters, so its options toggle; any other picks one.
  const many = d.pickLetters.length > 1;
  const pickSet = d.pickLetters.join(',');
  const toggle = (l: string) => {
    const letters = many
      ? (row.letters.includes(l) ? row.letters.filter(x => x !== l) : [...row.letters, l].sort())
      : [l];
    onChange({ letters, take: letters.join(',') === pickSet && d.pick ? 'pick' : 'letters' });
  };
  const lettered = row.take === 'pick' || row.take === 'letters';
  const unanswered = (row.take === 'letters' && row.letters.length === 0) || (row.take === 'own' && !row.note.trim());
  const state = unanswered ? 'to answer' : row.take === 'pick' ? 'as picked' : row.take === 'later' ? 'left open'
    : d.pick ? 'changed' : 'answered';
  const tone = unanswered ? TIER.blocked.c : row.take === 'pick' ? 'var(--w-t500)' : row.take === 'later' ? 'var(--w-t600)' : 'var(--w-accent-text)';
  // What the spec says about it, without the options and the pick, which are shown as rows below.
  const context = d.text.replace(/\*\*Pick:[\s\S]*$/i, '').replace(/\bOptions?:[\s\S]*$/i, '').trim();

  return (
    <div style={{ padding: '12px 0', borderTop: '1px solid rgb(var(--w-line-rgb) / .1)' }}>
      <div className="flex items-baseline gap-2 flex-wrap">
        <span style={{ font: `700 11.5px ${MONO}`, color: 'var(--w-t500)' }}>#{d.number}</span>
        <span className="min-w-0" style={{ font: `600 13.5px/1.4 ${BODY}`, color: 'var(--w-t100)' }}>{d.title}</span>
        <span style={{ ...label(tone), marginLeft: 'auto' }}>{state}</span>
      </div>
      {context && (
        <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t400)', marginTop: 4 }}>{inline(context)}</div>
      )}

      <div className="flex flex-col gap-1.5" style={{ marginTop: 8 }}>
        {d.options.map(o => (
          <OptionRow key={o.letter} on={lettered && row.letters.includes(o.letter)} onClick={() => toggle(o.letter)}
            title={<>({o.letter}) {inline(o.text)}</>} tag={d.pickLetters.includes(o.letter) ? 'the pick' : undefined} />
        ))}
        {d.options.length === 0 && d.pick && (
          <OptionRow on={row.take === 'pick'} onClick={() => onChange({ take: 'pick' })} title={inline(d.pick)} tag="the pick" />
        )}
        <OptionRow on={row.take === 'own'} onClick={() => onChange({ take: 'own', noting: true })} title="Something else" />
        {mode !== 'answer' && (
          <OptionRow on={row.take === 'later'} onClick={() => onChange({ take: 'later' })} title="Leave it open for now"
            detail="The steward asks about it before it files anything that leans on it." />
        )}
      </div>

      {d.pickWhy && lettered && (
        <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t500)', marginTop: 6 }}>
          Why the pick: {inline(d.pickWhy)}
        </div>
      )}

      {row.take === 'own' || row.noting
        ? (
          <textarea
            autoFocus={row.take === 'own'} value={row.note} onChange={e => onChange({ note: e.target.value })} rows={2}
            placeholder={row.take === 'own' ? 'What you decide, in your own words.' : 'Anything to add (optional)'}
            className="mt-2 w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
          />
        )
        : row.take !== 'later' && (
          <div style={{ marginTop: 6 }}>
            <GhostButton onClick={() => onChange({ noting: true })}>Add a note</GhostButton>
          </div>
        )}
    </div>
  );
}
