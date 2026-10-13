import { useEffect, useState } from 'react';
import { Chip, Panel } from './ui';
import { BODY, MONO, TIER, chamfer, fade } from './orbit';
import { Markdown } from './Markdown';
import { Quiet, label, remember, remembered } from './stewardParts';
import type {
  StewardPlanOutcome, StewardPlanUnitView, StewardPlanView, StewardStatus,
} from '../api/steward';

/**
 * The plan, as something to read.
 *
 * A plan is one document and the units cut from it, and a person needs three things of it at
 * different moments: where the whole stands (the rail), what one unit promised and how far each
 * promise is (its outcomes, in the spec's own words and order), and the text itself - the unit's
 * spec, or the document all of them come from. Nothing here is a control. Agreeing a unit is still
 * a line its owner commits.
 */

type Selected = number | 'master';

const STATE_TIER: Record<StewardPlanOutcome['state'], keyof typeof TIER> = {
  NotStarted: 'backlog', InProgress: 'active', Done: 'done',
};
const STATE_WORD: Record<StewardPlanOutcome['state'], string> = {
  NotStarted: 'not started', InProgress: 'in progress', Done: 'done',
};

/** Where a unit stands, in a word, and the tier that colours it. */
function standing(u: StewardPlanUnitView, current?: number | null): { word: string; tier: keyof typeof TIER } {
  if (u.done) return { word: 'done', tier: 'done' };
  if (u.agreed) return u.unit === current ? { word: 'building', tier: 'active' } : { word: 'agreed', tier: 'queue' };
  if (u.draft) return { word: 'draft - yours to agree', tier: 'gate' };
  return { word: 'not agreed', tier: 'backlog' };
}

export function StewardPlan({ view, status }: { view: StewardPlanView | null; status: StewardStatus | null }) {
  const units = view?.plan?.units ?? [];
  const current = view?.plan?.current;
  const [picked, setPicked] = useState<Selected | null>(null);

  // Until somebody picks, the page is on the unit being built: that is the one being asked about.
  const selected: Selected | null = picked ?? current ?? (units.length > 0 ? units[units.length - 1].unit : view?.master ? 'master' : null);
  const unit = typeof selected === 'number' ? units.find(u => u.unit === selected) : undefined;

  if (!view) return <Quiet>Reading the plan.</Quiet>;
  if (units.length === 0 && !view.master) return <Quiet>{view.why ?? 'This project has no plan.'}</Quiet>;

  return (
    <div className="mb-8">
      <div className="flex flex-wrap gap-2" style={{ marginBottom: 14 }}>
        {view.master && (
          <RailItem on={selected === 'master'} onClick={() => setPicked('master')} lead="All" title="The whole plan" sub={view.master.file} />
        )}
        {units.map(u => {
          const s = standing(u, current);
          return (
            <RailItem
              key={u.unit} on={selected === u.unit} onClick={() => setPicked(u.unit)}
              lead={String(u.unit).padStart(2, '0')} title={u.title} colour={TIER[s.tier].c}
              sub={`${s.word}${u.agreed && !u.done ? ` - ${u.progress.percent}%` : ''}`}
              percent={u.agreed ? u.progress.percent : undefined}
            />
          );
        })}
      </div>

      {selected === 'master' && view.master && (
        <Panel key="master" className="orbit-swap">
          <div style={{ maxWidth: 780 }}><Markdown doc text={view.master.text} /></div>
        </Panel>
      )}
      {unit && <UnitView key={unit.unit} unit={unit} current={current} status={status} />}
    </div>
  );
}

/** One stop on the rail: a unit, or the document they are all cut from. */
function RailItem({ on, onClick, lead, title, sub, colour = 'var(--w-t400)', percent }: {
  on: boolean; onClick: () => void; lead: string; title: string; sub: string; colour?: string; percent?: number;
}) {
  return (
    <button
      onClick={onClick}
      aria-current={on ? 'true' : undefined}
      className="orbit-pick pick-lift text-left min-w-0 max-sm:w-full"
      style={{
        clipPath: chamfer(9),
        padding: '8px 14px 9px',
        maxWidth: 340,
        background: on ? 'rgb(var(--w-accent-rgb) / .13)' : 'rgb(var(--w-line-rgb) / .04)',
        border: `1px solid ${on ? 'rgb(var(--w-accent-rgb) / .5)' : 'rgb(var(--w-line-rgb) / .14)'}`,
      }}
    >
      <div className="flex items-baseline gap-2 min-w-0">
        <span style={{ font: `700 11px ${MONO}`, color: on ? 'var(--w-accent-text-lit)' : 'var(--w-t400)', flex: 'none' }}>{lead}</span>
        <span className="truncate min-w-0" style={{ font: `600 12.5px ${BODY}`, color: on ? 'var(--w-accent-text-lit)' : 'var(--w-t200)' }}>{title}</span>
      </div>
      <div className="truncate" style={{ ...label(colour), marginTop: 4 }}>{sub}</div>
      {percent != null && <Bar percent={percent} colour={colour} />}
    </button>
  );
}

function Bar({ percent, colour, height = 2 }: { percent: number; colour: string; height?: number }) {
  return (
    <div style={{ height, marginTop: 6, background: 'rgb(var(--w-line-rgb) / .12)' }}>
      <div style={{ height, width: `${Math.max(0, Math.min(100, percent))}%`, background: colour }} />
    </div>
  );
}

function UnitView({ unit, current, status }: { unit: StewardPlanUnitView; current?: number | null; status: StewardStatus | null }) {
  const [show, setShow] = useState<'outcomes' | 'spec'>(() => (remembered('plan.show') === 'spec' ? 'spec' : 'outcomes'));
  const [leftOnly, setLeftOnly] = useState(() => remembered('plan.left') === '1');
  useEffect(() => { remember('plan.show', show); }, [show]);
  useEffect(() => { remember('plan.left', leftOnly ? '1' : '0'); }, [leftOnly]);

  const s = standing(unit, current);
  const c = TIER[s.tier].c;
  const p = unit.progress;
  const anyOutcomes = unit.groups.some(g => g.outcomes.length > 0);

  return (
    <Panel pad={false} className="orbit-swap">
      <div style={{ padding: '14px 16px 12px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .1)' }}>
        <div className="flex items-baseline gap-x-3 gap-y-1 flex-wrap">
          <span className="min-w-0" style={{ font: `700 16px ${BODY}`, color: 'var(--w-t100)' }}>{unit.title}</span>
          <span style={label(c)}>{s.word}</span>
          {unit.agreed && <span style={{ font: `700 13px ${MONO}`, color: c, marginLeft: 'auto' }}>{p.percent}%</span>}
        </div>
        {unit.agreed && <Bar percent={p.percent} colour={c} height={3} />}
        <div className="flex items-center gap-x-4 gap-y-2 flex-wrap" style={{ marginTop: 10 }}>
          <span style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t400)' }}>
            {p.outcomes} outcome{p.outcomes === 1 ? '' : 's'}
            {unit.agreed && <>: {p.done} done, {p.inProgress} in progress, {p.notStarted} not started</>}
          </span>
          <span className="truncate min-w-0" style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>{unit.file}</span>
          <span className="flex items-center gap-2 flex-wrap" style={{ marginLeft: 'auto' }}>
            {show === 'outcomes' && unit.agreed && p.done > 0 && p.done < p.outcomes && (
              <Chip on={leftOnly} onClick={() => setLeftOnly(!leftOnly)} title="Hide the outcomes that are done">What is left</Chip>
            )}
            <Chip on={show === 'outcomes'} onClick={() => setShow('outcomes')}>Outcomes</Chip>
            <Chip on={show === 'spec'} onClick={() => setShow('spec')} title="The unit's spec, as committed">The spec</Chip>
          </span>
        </div>
        {!unit.agreed && (
          <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 10 }}>
            {unit.draft
              ? 'A draft. Nothing is filed against it until you mark it agreed in the file itself and commit that.'
              : 'Not marked agreed, so nothing is filed against it.'}
          </div>
        )}
        {status && unit.unit === current && (
          <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t600)', marginTop: 8 }}>
            Tickets filed today {status.limits.createdToday} - open {status.limits.open}
          </div>
        )}
      </div>

      {show === 'spec'
        ? <div key="spec" className="orbit-swap" style={{ padding: '14px 16px 18px', maxWidth: 812 }}><Markdown doc text={unit.text} /></div>
        : !anyOutcomes
          ? <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)', padding: '14px 16px' }}>This spec lists no outcomes the board can read. Open the spec to read it as written.</div>
          : (
            <div key="outcomes" className="orbit-swap">
              {unit.groups.map(g => {
                const shown = leftOnly ? g.outcomes.filter(o => o.state !== 'Done') : g.outcomes;
                if (shown.length === 0) return null;
                const done = g.outcomes.filter(o => o.state === 'Done').length;
                return (
                  <div key={g.heading}>
                    <div
                      className="flex items-baseline gap-3"
                      style={{ padding: '12px 16px 6px', background: 'rgb(var(--w-line-rgb) / .03)', borderBottom: '1px solid rgb(var(--w-line-rgb) / .08)' }}
                    >
                      <span className="min-w-0" style={{ font: `700 12px ${BODY}`, color: 'var(--w-t100)' }}>{g.heading || 'Outcomes'}</span>
                      <span style={{ ...label(done === g.outcomes.length ? TIER.done.c : 'var(--w-t500)'), marginLeft: 'auto', flex: 'none' }}>
                        {done} of {g.outcomes.length} done
                      </span>
                    </div>
                    {shown.map(o => <OutcomeRow key={o.outcome} o={o} />)}
                  </div>
                );
              })}
            </div>
          )}
    </Panel>
  );
}

/** One promise of the unit: what it says, where it stands, and the tickets carrying it. */
function OutcomeRow({ o }: { o: StewardPlanOutcome }) {
  const c = TIER[STATE_TIER[o.state]].c;
  const open = o.tickets.filter(t => t.phase !== 'Done');
  return (
    <div
      className="flex gap-x-3 gap-y-1 max-sm:flex-wrap"
      style={{ padding: '9px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)' }}
    >
      <div className="flex items-baseline gap-3 flex-none" style={{ width: 128 }}>
        <span style={{ font: `700 11.5px ${MONO}`, color: 'var(--w-t200)', width: 30, flex: 'none' }}>{o.outcome}</span>
        <span style={label(c)}>{STATE_WORD[o.state]}</span>
      </div>
      <div className="min-w-0 flex-1 max-sm:basis-full">
        <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: o.state === 'Done' ? 'var(--w-t400)' : 'var(--w-t200)' }}>
          <Sentence text={o.text} />
        </div>
        {(o.note || o.moreToFile) && (
          <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: fade('var(--w-t400)', 80), marginTop: 3 }}>
            {o.moreToFile && <span style={{ color: TIER.active.c }}>More to file. </span>}{o.note}
          </div>
        )}
        {o.tickets.length > 0 && (
          <div className="flex flex-wrap gap-x-2.5 gap-y-0.5" style={{ marginTop: 4 }}>
            {/* What is still moving is named; what is merged is only counted, or a dozen numbers
                bury the one ticket anybody is waiting for. */}
            {open.map(t => (
              <span key={t.id} title={t.title} style={{ font: `600 11px ${MONO}`, color: TIER.active.c }}>
                #{t.id} <span style={{ fontWeight: 400, color: 'var(--w-t400)' }}>{t.phase}</span>
              </span>
            ))}
            {o.tickets.length > open.length && (
              <span
                title={o.tickets.filter(t => t.phase === 'Done').map(t => `#${t.id} ${t.title}`).join('\n')}
                style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}
              >
                {o.tickets.length - open.length} merged
              </span>
            )}
          </div>
        )}
      </div>
    </div>
  );
}

/** An outcome's sentence, with the spec's own emphasis and code kept. */
function Sentence({ text }: { text: string }) {
  // One line of prose: the document renderer would wrap it in a paragraph with its own size.
  const parts = text.split(/(\*\*.+?\*\*|`[^`]+`)/g);
  return (
    <>
      {parts.map((p, i) => p.startsWith('**') && p.endsWith('**') && p.length > 4
        ? <strong key={i} style={{ fontWeight: 600, color: 'var(--w-t100)' }}>{p.slice(2, -2)}</strong>
        : p.startsWith('`') && p.endsWith('`') && p.length > 2
          ? <code key={i} style={{ font: `400 11.5px ${MONO}`, color: 'var(--w-t300)' }}>{p.slice(1, -1)}</code>
          : p)}
    </>
  );
}
