import { useMemo, useState } from 'react';
import { Chip, GhostButton, Panel } from './ui';
import { BODY, MONO, TIER } from './orbit';
import { Fold, Quiet, label, remember, remembered } from './stewardParts';
import {
  recordStewardDecision, settleDecisionPair, withdrawStewardDecision,
} from '../api/steward';
import type { DecisionPair, NewDecision, StewardDecision, StewardRegister } from '../api/steward';

/**
 * The register of decisions, as its owner reads it: what is in force for the whole project and for
 * each slice, what is provisional, and what no longer holds with what replaced it.
 *
 * The register is the one place a decision's status lives, so this is also where it changes: a
 * decision recorded directly, one replaced by a newer one (the replacing is said on the newer, and
 * the older's status follows), one withdrawn. And it is where the pairs the import thought may
 * disagree wait for the owner - the import never assumes one replaces the other.
 */

type Shown = 'force' | 'provisional' | 'history';

export function StewardDecisions({ project, register, onChanged }: {
  project: string; register: StewardRegister | null; onChanged: () => Promise<void>;
}) {
  const [shown, setShown] = useState<Shown>(() => {
    const v = remembered('decisions.shown');
    return v === 'provisional' || v === 'history' ? v : 'force';
  });
  const pick = (s: Shown) => { setShown(s); remember('decisions.shown', s); };
  const [find, setFind] = useState('');
  // A new decision, or a change to one: the number it replaces when it is a change.
  const [writing, setWriting] = useState<{ replaces?: number } | null>(null);

  const all = register?.decisions ?? [];
  const byNumber = useMemo(() => new Map(all.map(d => [d.number, d])), [all]);
  const needle = find.trim().toLowerCase();
  const matches = (d: StewardDecision) => !needle
    || `#${d.number} ${d.title ?? ''} ${d.rule} ${d.key ?? ''} ${d.question ?? ''} ${d.appliesTo}`.toLowerCase().includes(needle);

  const force = all.filter(d => d.status === 'inForce');
  const provisional = all.filter(d => d.status === 'provisional');
  const history = all.filter(d => d.status === 'replaced' || d.status === 'withdrawn');
  const list = (shown === 'force' ? force : shown === 'provisional' ? provisional : history).filter(matches);

  // In force: the whole project first, then each slice in order - the order the file is written in.
  const groups = useMemo(() => {
    if (shown !== 'force') return [{ key: shown, title: '', rows: list }];
    const out: { key: string; title: string; rows: StewardDecision[] }[] = [];
    const whole = list.filter(d => d.unit == null);
    if (whole.length > 0) out.push({ key: 'project', title: 'The whole project', rows: whole });
    const units = [...new Set(list.filter(d => d.unit != null).map(d => `${d.plan ?? ''}:${d.unit}`))];
    units.sort((a, b) => Number(a.split(':')[1]) - Number(b.split(':')[1]));
    for (const u of units) {
      const rows = list.filter(d => `${d.plan ?? ''}:${d.unit}` === u);
      const named = (register?.plan ?? '') === (rows[0].plan ?? '') ? register?.units.find(x => x.unit === rows[0].unit)?.title : undefined;
      out.push({ key: u, title: named ?? `Unit ${rows[0].unit}${rows[0].plan ? ` of ${rows[0].plan}` : ''}`, rows });
    }
    return out;
  }, [list, shown, register]);

  if (!register) return <Quiet>Reading the register.</Quiet>;

  return (
    <>
      <Pairs project={project} pairs={register.mayConflict} byNumber={byNumber} onChanged={onChanged} />

      <div className="flex flex-wrap items-center gap-2" style={{ marginBottom: 10 }}>
        <Chip on={shown === 'force'} onClick={() => pick('force')} title="Decided, and still so">In force {force.length}</Chip>
        <Chip on={shown === 'provisional'} onClick={() => pick('provisional')} title="In force until a named point">
          Provisional {provisional.length}
        </Chip>
        <Chip on={shown === 'history'} onClick={() => pick('history')} title="Replaced or withdrawn, with what replaced each">
          History {history.length}
        </Chip>
        <span className="max-sm:hidden" style={{ marginLeft: 'auto' }} />
        <GhostButton tone="go" onClick={() => setWriting(writing ? null : {})}>
          {writing && writing.replaces == null ? 'Close' : 'Record a decision'}
        </GhostButton>
      </div>

      {writing && (
        <Writer
          key={writing.replaces ?? 'new'} project={project} register={register} replaces={writing.replaces}
          old={writing.replaces != null ? byNumber.get(writing.replaces) : undefined}
          onDone={async () => { setWriting(null); await onChanged(); }} onCancel={() => setWriting(null)}
        />
      )}

      <input
        value={find} onChange={e => setFind(e.target.value)}
        placeholder="Find a number, a word, a question's key"
        aria-label="Find a decision"
        className="w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
        style={{ maxWidth: 360, margin: '4px 0 14px' }}
      />

      <div key={shown} className="orbit-swap">
        {list.length === 0
          ? <Quiet>{needle ? 'Nothing matches.' : shown === 'force' ? 'Nothing is decided yet.' : shown === 'provisional' ? 'Nothing is provisional.' : 'Nothing has been replaced or withdrawn.'}</Quiet>
          : groups.map(g => (
            <div key={g.key} className="mb-6">
              {g.title && <div style={{ ...label('var(--w-t300)'), margin: '0 0 6px 2px' }}>{g.title} - {g.rows.length}</div>}
              <Panel pad={false}>
                {g.rows.map(d => (
                  <DecisionRow key={d.number} d={d} project={project} onChanged={onChanged}
                    onChange={() => setWriting({ replaces: d.number })} />
                ))}
              </Panel>
            </div>
          ))}
      </div>
    </>
  );
}

/** The pairs the import thought may disagree, each waiting for the owner to say which holds. */
function Pairs({ project, pairs, byNumber, onChanged }: {
  project: string; pairs: DecisionPair[]; byNumber: Map<number, StewardDecision>; onChanged: () => Promise<void>;
}) {
  if (pairs.length === 0) return null;
  return (
    <Fold id="decisions.pairs" open title="May disagree" colour={TIER.blocked.c} count={pairs.length}
      meta="found by the import: none is assumed">
      <div className="flex flex-col gap-2 mb-8">
        {pairs.map(p => <PairCard key={p.id} project={project} p={p} byNumber={byNumber} onChanged={onChanged} />)}
      </div>
    </Fold>
  );
}

function PairCard({ project, p, byNumber, onChanged }: {
  project: string; p: DecisionPair; byNumber: Map<number, StewardDecision>; onChanged: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const older = byNumber.get(p.older);
  const newer = byNumber.get(p.newer);
  const settle = async (replaces: boolean, why?: string) => {
    setBusy(true); setFailed(null);
    try { await settleDecisionPair(project, p.id, replaces, why); await onChanged(); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); setBusy(false); }
  };
  const side = (d: StewardDecision | undefined, n: number, word: string) => (
    <div className="min-w-0 flex-1" style={{ minWidth: 220 }}>
      <div style={label()}>{word} - #{n}{d ? ` - ${d.on} - ${d.appliesTo}` : ''}</div>
      <div style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)', marginTop: 3 }}>{d?.rule ?? 'No longer in the register.'}</div>
    </div>
  );
  return (
    <Panel>
      <div className="flex gap-4 flex-wrap">
        {side(older, p.older, 'Older')}
        {side(newer, p.newer, 'Newer')}
      </div>
      <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: 'var(--w-t500)', marginTop: 8 }}>{p.why}</div>
      <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 10 }}>
        <GhostButton tone="go" disabled={busy} onClick={() => settle(true)}>#{p.newer} replaces #{p.older}</GhostButton>
        {/* One decision kept in two places: kept once, under the newer number, the older pointing to it. */}
        <GhostButton disabled={busy} onClick={() => settle(true, `The same decision, kept once as #${p.newer}.`)}
          title={`One decision written twice: #${p.older} goes to history, pointing to #${p.newer}`}>Same decision</GhostButton>
        <GhostButton disabled={busy} onClick={() => settle(false)}>Both stand</GhostButton>
        {failed && <span style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c }}>{failed}</span>}
      </div>
    </Panel>
  );
}

function DecisionRow({ d, project, onChanged, onChange }: {
  d: StewardDecision; project: string; onChanged: () => Promise<void>; onChange: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [withdrawing, setWithdrawing] = useState(false);
  const [because, setBecause] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const live = d.status === 'inForce' || d.status === 'provisional';

  const withdraw = async () => {
    setBusy(true); setFailed(null);
    try { await withdrawStewardDecision(project, d.number, because.trim()); await onChanged(); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); setBusy(false); }
  };

  return (
    <div className="flex gap-3" style={{ padding: '9px 14px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)', opacity: live ? 1 : 0.7 }}>
      <span className="flex-none" style={{
        font: `700 11px ${MONO}`, color: live ? 'var(--w-accent-text)' : 'var(--w-t500)', minWidth: 34, paddingTop: 1,
        textDecoration: live ? undefined : 'line-through',
      }}>#{d.number}</span>
      <div className="min-w-0 flex-1">
        <button onClick={() => setOpen(!open)} aria-expanded={open} aria-current={open ? 'true' : undefined}
          className="orbit-pick block w-full text-left min-w-0">
          <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t200)' }}>
            {d.title && <span style={{ fontWeight: 600 }}>{d.title}. </span>}
            <span className={open ? '' : 'line-clamp-3'}>{d.rule}</span>
          </div>
          <div className="truncate" style={{ ...label(), marginTop: 4 }}>
            {d.on} - {d.cameFrom}{d.key ? ` (${d.key})` : ''}{d.outcomes ? ` - ${d.outcomes}` : ''}
            {d.provisionalUntil ? ` - until ${d.provisionalUntil}` : ''}
          </div>
        </button>

        {d.replaces && (
          <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 4 }}>
            Replaces {d.replaces.map(r => `#${r.number} (${r.why.replace(/\.$/, '')})`).join(', ')}.
          </div>
        )}
        {d.replacedBy && (
          <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: TIER.gate.c, marginTop: 4 }}>
            Replaced by {d.replacedBy.map(r => `#${r.number}: ${r.why.replace(/\.$/, '')}`).join('; ')}.
          </div>
        )}
        {d.withdrawn && (
          <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: TIER.gate.c, marginTop: 4 }}>
            Withdrawn {d.withdrawn.on}{d.withdrawn.because ? `: ${d.withdrawn.because}` : ''}
          </div>
        )}

        {open && (
          <div key="open" className="orbit-swap" style={{ marginTop: 8 }}>
            {d.question && (
              <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t400)' }}>Asked: {d.question}</div>
            )}
            {d.offered && (
              <div className="flex flex-col gap-1" style={{ marginTop: 6 }}>
                {d.offered.map((o, i) => (
                  <div key={i} style={{ font: `400 12px/1.45 ${BODY}`, color: o.picked ? 'var(--w-t100)' : 'var(--w-t500)' }}>
                    {o.picked ? 'Picked: ' : 'Not picked: '}{o.label}{o.detail ? ` - ${o.detail}` : ''}
                    {o.stewardsPick && <span style={{ ...label('var(--w-accent-text)'), marginLeft: 6 }}>the steward's pick</span>}
                  </div>
                ))}
              </div>
            )}
            {d.where && <div style={{ ...label(), marginTop: 6, textTransform: 'none', letterSpacing: 0, fontWeight: 400 }}>{d.where}</div>}

            {live && !withdrawing && (
              <div className="flex gap-2 flex-wrap" style={{ marginTop: 10 }}>
                <GhostButton onClick={onChange} title="Record a newer decision that replaces this one">Change it</GhostButton>
                <GhostButton tone="danger" onClick={() => setWithdrawing(true)} title="It no longer holds, and nothing takes its place">Withdraw</GhostButton>
              </div>
            )}
            {withdrawing && (
              <div style={{ marginTop: 10 }}>
                <input value={because} onChange={e => setBecause(e.target.value)} autoFocus
                  placeholder="Why it no longer holds, in a line"
                  aria-label="Why it no longer holds"
                  className="w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none" />
                <div className="flex gap-2 flex-wrap" style={{ marginTop: 8 }}>
                  <GhostButton tone="danger" disabled={busy || !because.trim()} onClick={withdraw}>Withdraw #{d.number}</GhostButton>
                  <GhostButton disabled={busy} onClick={() => setWithdrawing(false)}>Keep it</GhostButton>
                </div>
              </div>
            )}
            {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 6 }}>{failed}</div>}
          </div>
        )}
      </div>
    </div>
  );
}

/** A decision written by the owner: a new one, or (with `replaces`) a change to one in force. */
function Writer({ project, register, replaces, old, onDone, onCancel }: {
  project: string; register: StewardRegister; replaces?: number; old?: StewardDecision;
  onDone: () => Promise<void>; onCancel: () => void;
}) {
  const [rule, setRule] = useState('');
  const [unit, setUnit] = useState<number | null>(old?.unit ?? null);
  const [outcomes, setOutcomes] = useState(old?.outcomes ?? '');
  const [provisional, setProvisional] = useState(false);
  const [until, setUntil] = useState<number | null>(null);
  const [why, setWhy] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  const ready = rule.trim().length > 0 && (replaces == null || why.trim().length > 0);
  const save = async () => {
    setBusy(true); setFailed(null);
    const body: NewDecision = {
      rule: rule.trim(),
      unit,
      outcomes: unit != null ? outcomes.split(',').map(o => o.trim()).filter(Boolean) : undefined,
      provisional,
      provisionalUnit: provisional ? until : null,
      replaces: replaces != null ? [{ number: replaces, why: why.trim() }] : undefined,
    };
    try { await recordStewardDecision(project, body); await onDone(); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); setBusy(false); }
  };

  const field = 'w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none';
  return (
    <div className="orbit-swap mb-6">
      <Panel tone="var(--w-accent)">
        <div style={label('var(--w-accent-text)')}>
          {replaces != null ? `A change to #${replaces}: a new decision, #${register.next}, that replaces it` : `A new decision: #${register.next}`}
        </div>
        {old && <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t500)', marginTop: 6 }}>Now: {old.rule}</div>}
        <textarea value={rule} onChange={e => setRule(e.target.value)} rows={3} autoFocus
          placeholder="What is now true, in a sentence or two."
          aria-label="The decision"
          className={`${field} mt-2 resize-y`} />
        {replaces != null && (
          <input value={why} onChange={e => setWhy(e.target.value)} placeholder={`Why #${replaces} no longer holds, in a line`}
            aria-label="Why the old one no longer holds" className={`${field} mt-2`} />
        )}

        <div className="flex flex-wrap items-center gap-2" style={{ marginTop: 10 }}>
          <span style={label()}>Applies to</span>
          <Chip on={unit == null} onClick={() => setUnit(null)}>The project</Chip>
          {register.units.map(u => (
            <Chip key={u.unit} on={unit === u.unit} onClick={() => setUnit(u.unit)} title={u.title}>Unit {String(u.unit).padStart(2, '0')}</Chip>
          ))}
        </div>
        {unit != null && (
          <input value={outcomes} onChange={e => setOutcomes(e.target.value)} placeholder="Only these outcomes, like A1, B2 (optional)"
            aria-label="Outcomes it applies to" className={`${field} mt-2`} style={{ maxWidth: 320 }} />
        )}

        <div className="flex flex-wrap items-center gap-2" style={{ marginTop: 10 }}>
          <Chip on={provisional} onClick={() => setProvisional(!provisional)} title="In force only until a named point">Provisional</Chip>
          {provisional && (
            <>
              <span style={label()}>until</span>
              <Chip on={until == null} onClick={() => setUntil(null)}>You say</Chip>
              {register.units.map(u => (
                <Chip key={u.unit} on={until === u.unit} onClick={() => setUntil(u.unit)} title={u.title}>Unit {String(u.unit).padStart(2, '0')} is built</Chip>
              ))}
            </>
          )}
        </div>

        <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 12 }}>
          <GhostButton tone="go" disabled={busy || !ready} onClick={save}>{busy ? 'Recording' : 'Record it'}</GhostButton>
          <GhostButton disabled={busy} onClick={onCancel}>Cancel</GhostButton>
          {failed && <span style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c }}>{failed}</span>}
        </div>
      </Panel>
    </div>
  );
}
