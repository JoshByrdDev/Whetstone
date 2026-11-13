import { useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { Chip, GhostButton, Panel } from './ui';
import { BODY, MONO, TIER, chamfer, fade } from './orbit';
import { Markdown } from './Markdown';
import { UnitReport } from './UnitReport';
import { DecisionSheet } from './DecisionSheet';
import { Quiet, ago, label, remember, remembered } from './stewardParts';
import {
  Between, Counts, DecisionsBeside, LOOK, StoryView, Strip, isWalkthrough, phaseWord, servedBy, sliceDecisions, standings, storyOf,
} from './stewardSlice';
import type { Standing } from './stewardSlice';
import { useNarrow } from '../hooks/useNarrow';
import { abandonStewardUnit, agreeStewardUnit, setStewardUnitReleased } from '../api/steward';
import type {
  StewardMode, StewardPlanOutcome, StewardPlanUnitView, StewardPlanView, StewardQuestion, StewardRegister, StewardStatus, UnitStage,
} from '../api/steward';

/**
 * The slices, as something to read and to move along.
 *
 * There is no plan around them (docs/slice-branches.md): each slice is a spec in the repo, its own
 * branch while it is open, and its decisions in the register. A person needs two things of them at
 * different moments: where they all stand (the slices in lanes by stage, each with the strip of its
 * outcomes), and what one slice promised and how far each promise is (its story step by step, its
 * outcomes in the spec's own words and order, what stands between it and built, the decisions it
 * is held to - see stewardSlice.tsx).
 *
 * A slice goes one way: an idea, a draft, agreed, building, built, released. Two of those steps are
 * a person's and are taken here - agreeing a draft, and saying a built slice has gone out. Ideas are
 * the owner's too, written in the repo or committed from a steward's proposal. Everything else is
 * the steward's and the fleet's, and is only shown.
 */

type Selected = number;

/** Where a unit stands, in words, and the tier that colours it. */
const STAGE: Record<UnitStage, { word: string; tier: keyof typeof TIER }> = {
  idea: { word: 'idea', tier: 'backlog' },
  draft: { word: 'draft - yours to agree', tier: 'gate' },
  notAgreed: { word: 'not agreed', tier: 'backlog' },
  agreed: { word: 'agreed', tier: 'queue' },
  building: { word: 'building', tier: 'active' },
  built: { word: 'built - yours to release', tier: 'gate' },
  released: { word: 'released', tier: 'done' },
  abandoned: { word: 'abandoned', tier: 'backlog' },
};

/** The way a unit goes, for the strip that shows how far along it is. */
const STEPS: { stage: UnitStage; word: string }[] = [
  { stage: 'idea', word: 'Idea' },
  { stage: 'draft', word: 'Draft' },
  { stage: 'agreed', word: 'Agreed' },
  { stage: 'building', word: 'Building' },
  { stage: 'built', word: 'Built' },
  { stage: 'released', word: 'Released' },
];

export function StewardPlan({ project, view, status, waiting, register, mode, onChanged, onNotes, onNow, onDecisions }: {
  project: string;
  view: StewardPlanView | null;
  status: StewardStatus | null;
  /** The questions on Now: what holds an outcome, and which decisions an open question touches. */
  waiting: StewardQuestion[];
  register: StewardRegister | null;
  /** What the steward may do, which says whose a merge is. */
  mode?: StewardMode;
  /** Something here changed what the page shows. */
  onChanged: () => Promise<void>;
  /** Show a unit's notes for its release, in the record. */
  onNotes: (unit: number) => void;
  /** Go to Now, where questions are answered. */
  onNow: () => void;
  /** Go to the register. */
  onDecisions: () => void;
}) {
  const current = view?.plan?.current;
  const narrow = useNarrow();
  // A server from before units had stages says only agreed, draft and done: read the stage off those.
  // An agreed slice with work done or moving is being built, whichever one the board counts as current:
  // a slice held open by one last outcome must not hide the one actually being worked.
  const units = (view?.plan?.units ?? []).map(u => {
    const stage: UnitStage = u.stage ?? (u.done ? 'built' : u.agreed ? (u.unit === current ? 'building' : 'agreed') : u.draft ? 'draft' : 'notAgreed');
    return { ...u, stage: stage === 'agreed' && u.progress.done + u.progress.inProgress > 0 ? 'building' as UnitStage : stage };
  });
  const [picked, setPicked] = useState<Selected | null>(null);

  // Until somebody picks, the page is on the slice being worked - the latest one being built - and
  // then on one that wants something of you. A release waits in its own lane, so it no longer
  // decides where the page opens.
  const building = [...units].reverse().find(u => u.stage === 'building');
  const asking = units.find(u => u.stage === 'draft' && !u.cannotAgree) ?? units.find(u => u.stage === 'built');
  const selected: Selected | null = picked ?? building?.unit ?? current ?? asking?.unit
    ?? (units.length > 0 ? units[units.length - 1].unit : null);
  const unit = selected != null ? units.find(u => u.unit === selected) : undefined;

  if (!view) return <Quiet>Reading the slices.</Quiet>;

  return (
    <div className="mb-8">
      {(view.plan?.moved?.length ?? 0) > 0 && (
        <Panel tone={TIER.blocked.c} className="mb-3">
          {view.plan!.moved!.map((m, i) => (
            <div key={i} style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>{m}</div>
          ))}
        </Panel>
      )}

      {units.length === 0
        ? <Quiet>{view.why ?? 'There are no slices yet. A slice starts as an idea: a spec file in the slices folder whose status line says idea.'}</Quiet>
        : (
          <>
            {narrow
              ? <SliceStrip units={units} selected={selected} onPick={setPicked} />
              : <Lanes units={units} selected={selected} onPick={setPicked} />}

            {unit && (
              <UnitView
                key={unit.unit} project={project} unit={unit}
                status={unit.unit === current ? status : null} onChanged={onChanged} onNotes={onNotes}
                waiting={waiting} register={register} mode={mode} onNow={onNow} onDecisions={onDecisions}
              />
            )}
          </>
        )}
    </div>
  );
}

type Unit = StewardPlanUnitView;

/**
 * The slices' stages as lanes, so where they stand reads at once: what is still an idea, what is yours to
 * agree, what is waiting, what is moving, what is yours to release, what has gone out. Left to right,
 * the way a ticket crosses the board - so each of the owner's two steps sits where it falls.
 */
const LANES: { title: string; stages: UnitStage[]; c: string; empty: string }[] = [
  { title: 'Ideas', stages: ['idea'], c: 'var(--w-t400)', empty: 'No ideas left.' },
  { title: 'To agree', stages: ['draft', 'notAgreed'], c: TIER.gate.c, empty: 'No draft to agree.' },
  { title: 'Agreed', stages: ['agreed'], c: TIER.queue.c, empty: 'Nothing agreed and waiting.' },
  { title: 'Building', stages: ['building'], c: TIER.active.c, empty: 'Nothing being built.' },
  { title: 'To release', stages: ['built'], c: TIER.gate.c, empty: 'Nothing to release.' },
  { title: 'Released', stages: ['released', 'abandoned'], c: TIER.done.c, empty: 'Nothing released yet.' },
];

function Lanes({ units, selected, onPick }: { units: Unit[]; selected: Selected | null; onPick: (s: Selected) => void }) {
  return (
    <Panel pad={false} className="mb-3">
      <div className="grid" style={{ gridTemplateColumns: `repeat(${LANES.length}, minmax(0, 1fr))` }}>
        {LANES.map((l, i) => {
          const here = units.filter(u => l.stages.includes(u.stage));
          return (
            <div key={l.title} className="min-w-0 flex flex-col" style={{ padding: '10px 10px 12px', gap: 7, borderLeft: i ? '1px solid rgb(var(--w-line-rgb) / .08)' : undefined }}>
              <div className="flex justify-between" style={label(l.c)}>
                <span>{l.title}</span><span style={{ color: 'var(--w-t500)' }}>{here.length}</span>
              </div>
              {here.length === 0
                ? <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t600)' }}>{l.empty}</div>
                : here.map(u => <SliceCard key={u.unit} u={u} on={selected === u.unit} onClick={() => onPick(u.unit)} />)}
            </div>
          );
        })}
      </div>
    </Panel>
  );
}

/** On a phone, the slices in their order as one strip that scrolls sideways, opened at the one being read. */
function SliceStrip({ units, selected, onPick }: { units: Unit[]; selected: Selected | null; onPick: (s: Selected) => void }) {
  const box = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const el = box.current?.querySelector<HTMLElement>('[aria-current="true"]');
    if (el && box.current) box.current.scrollLeft = el.offsetLeft - 8;
  }, [selected]);
  return (
    <div ref={box} className="flex gap-2 overflow-x-auto mb-3" style={{ paddingBottom: 6 }}>
      {units.map(u => (
        <div key={u.unit} style={{ flex: '0 0 auto', width: 168 }}>
          <SliceCard u={u} on={selected === u.unit} onClick={() => onPick(u.unit)} />
        </div>
      ))}
    </div>
  );
}

/** One slice: its number and title, where it stands, and the strip of its outcomes. */
function SliceCard({ u, on, onClick }: { u: Unit; on: boolean; onClick: () => void }) {
  const s = STAGE[u.stage];
  const p = u.progress;
  const sub = u.stage === 'idea' ? 'idea'
    : u.stage === 'draft' ? `draft${(u.openDecisions?.length ?? 0) > 0 ? ` - ${u.openDecisions!.length} to settle` : ''}`
    : u.stage === 'built' ? (u.release && u.release.outstanding > 0 ? `${u.release.outstanding} to tick off first` : 'ready to release')
    : u.stage === 'released' ? `released${u.releasedAt ? ` ${u.releasedAt.slice(0, 10)}` : ''}`
    : u.stage === 'abandoned' ? `abandoned${u.abandonedAt ? ` ${u.abandonedAt.slice(0, 10)}` : ''}`
    : u.agreed ? `${p.done} of ${p.outcomes} done` : s.word;
  return (
    <button
      onClick={onClick}
      aria-current={on ? 'true' : undefined}
      className="orbit-pick pick-lift text-left min-w-0 w-full block"
      title={u.title}
      style={{
        clipPath: chamfer(7),
        padding: '7px 10px 8px',
        background: on ? 'rgb(var(--w-accent-rgb) / .13)' : u.stage === 'idea' ? 'transparent' : 'rgb(var(--w-line-rgb) / .04)',
        border: `1px ${u.stage === 'idea' ? 'dashed' : 'solid'} ${on ? 'rgb(var(--w-accent-rgb) / .5)' : 'rgb(var(--w-line-rgb) / .16)'}`,
      }}
    >
      <div className="flex items-baseline gap-2 min-w-0">
        <span style={{ font: `700 10.5px ${MONO}`, color: on ? 'var(--w-accent-text-lit)' : 'var(--w-t400)', flex: 'none' }}>{String(u.unit).padStart(2, '0')}</span>
        <span className="line-clamp-2 min-w-0" style={{ font: `600 12px/1.35 ${BODY}`, color: on ? 'var(--w-accent-text-lit)' : 'var(--w-t200)' }}>{shortTitle(u)}</span>
      </div>
      <div className="truncate" style={{ font: `400 11px ${BODY}`, color: u.stage === 'built' || u.stage === 'draft' ? TIER.gate.c : 'var(--w-t500)', marginTop: 3 }}>{sub}</div>
      {u.stage !== 'idea' && u.groups.some(g => g.outcomes.length > 0) && <Strip unit={u} mini />}
    </button>
  );
}

/** "Slice 5: the desk's day" reads as its words once the number is beside it. */
const shortTitle = (u: Unit) => u.title.replace(new RegExp(`^(?:slice|unit)\\s+0*${u.unit}\\s*[:.-]\\s*`, 'i'), '');

/** How far along its one way a unit is: every step named, the ones behind it lit. */
function Steps({ stage }: { stage: UnitStage }) {
  const at = stage === 'notAgreed' ? -1 : STEPS.findIndex(s => s.stage === stage);
  return (
    <div className="flex items-center flex-wrap gap-x-2 gap-y-1" style={{ marginTop: 10 }}>
      {STEPS.map((s, i) => {
        const c = i < at ? TIER.done.c : i === at ? TIER[STAGE[stage].tier].c : 'var(--w-t600)';
        return (
          <span key={s.stage} className="flex items-center gap-2">
            {i > 0 && <span style={{ width: 14, height: 1, background: i <= at ? fade(TIER.done.c, 60) : 'rgb(var(--w-line-rgb) / .2)' }} />}
            <span style={{ ...label(c), fontWeight: i === at ? 700 : 600 }}>{s.word}</span>
          </span>
        );
      })}
    </div>
  );
}

type Show = 'outcomes' | 'spec' | 'report';

function UnitView({ project, unit, status, onChanged, onNotes, waiting, register, mode, onNow, onDecisions }: {
  project: string; unit: StewardPlanUnitView; status: StewardStatus | null;
  onChanged: () => Promise<void>; onNotes: (unit: number) => void;
  waiting: StewardQuestion[]; register: StewardRegister | null; mode?: StewardMode;
  onNow: () => void; onDecisions: () => void;
}) {
  // A report has something to say once anything of the unit is done, and is the thing to read
  // before a built unit is released.
  const reportable = unit.agreed && (unit.progress.done > 0 || unit.stage === 'built' || unit.stage === 'released');
  const [show, setShow] = useState<Show>(() => {
    // An idea is only its words. A draft now opens on its story and outcomes like any slice.
    if (unit.stage === 'idea') return 'spec';
    const was = remembered('plan.show');
    return was === 'spec' || (was === 'report' && reportable) ? was : 'outcomes';
  });
  const pick = (v: Show) => { setShow(v); remember('plan.show', v); };
  // A group of outcomes opens while any of it is left and folds once all of it is done; a press
  // overrides either way, until the slice changes.
  const [folded, setFolded] = useState<Record<string, boolean>>({});

  // One outcome the slice is narrowed to, from a citation, a cell or a line of what is left.
  const [focus, setFocus] = useState<string | null>(null);
  const toggle = (id: string) => setFocus(f => (f === id ? null : id));
  // One decision lit in the column beside, from a #n in an outcome.
  const [lit, setLit] = useState<number | null>(null);

  const s = STAGE[unit.stage];
  const c = TIER[s.tier].c;
  const anyOutcomes = unit.groups.some(g => g.outcomes.length > 0);
  const steps = useMemo(() => storyOf(unit.text), [unit.text]);
  const serves = useMemo(() => servedBy(steps), [steps]);
  const standing = useMemo(() => standings(unit, waiting, mode), [unit, waiting, mode]);
  const decisions = useMemo(() => sliceDecisions(unit, register, waiting), [unit, register, waiting]);
  const known = decisions?.known;
  const lightDecision = (n: number) => {
    setLit(n);
    document.getElementById(`decision-${n}`)?.scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  };
  const render = (text: string) => <Sentence text={text} known={known} onDecision={lightDecision} />;
  const sliceView = show === 'outcomes' && (anyOutcomes || !!steps);

  return (
    <Panel pad={false} className="orbit-swap">
      <div style={{ padding: '14px 16px 12px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .1)' }}>
        <div className="flex items-baseline gap-x-3 gap-y-1 flex-wrap">
          <span className="min-w-0" style={{ font: `700 16px ${BODY}`, color: 'var(--w-t100)' }}>{unit.title}</span>
          <span style={label(c)}>{s.word}</span>
        </div>
        <Steps stage={unit.stage} />
        {unit.agreed && anyOutcomes && (
          <div className="flex items-end gap-x-5 gap-y-2 flex-wrap" style={{ marginTop: 12 }}>
            <Strip unit={unit} standing={standing} focus={focus} onPick={toggle} />
            <Counts unit={unit} standing={standing} />
          </div>
        )}

        <Yours project={project} unit={unit} onChanged={onChanged} onNotes={onNotes}
          onReport={show === 'report' ? undefined : () => pick('report')} />
        {unit.agreed && (unit.openDecisions?.length ?? 0) > 0 && (
          <StillOpen project={project} unit={unit} onChanged={onChanged} />
        )}
        {unit.builtOn != null && unit.sliceBranch && (
          <div style={{ marginTop: 10, font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>
            Built on slice {unit.builtOn}: its branch keeps up with slice {unit.builtOn}'s, and it is released after it.
          </div>
        )}
        {unit.sliceBranch && unit.stage !== 'released' && (
          <Abandon project={project} unit={unit} onChanged={onChanged} />
        )}
        {unit.agreed && unit.stage !== 'built' && unit.stage !== 'released' && (
          <Between unit={unit} standing={standing} onPick={id => { toggle(id); pick('outcomes'); }} onNow={onNow} render={render} />
        )}

        <div className="flex items-center gap-x-4 gap-y-2 flex-wrap" style={{ marginTop: 12 }}>
          <span className="truncate min-w-0" style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>{unit.file}</span>
          <span className="flex items-center gap-2 flex-wrap" style={{ marginLeft: 'auto' }}>
            <Chip on={show === 'outcomes'} onClick={() => pick('outcomes')} title="Its story, its outcomes and the decisions it is held to">The slice</Chip>
            <Chip on={show === 'spec'} onClick={() => pick('spec')} title="The unit's spec, as committed">The spec</Chip>
            {reportable && (
              <Chip on={show === 'report'} onClick={() => pick('report')} title="One Classroom report on the whole slice">Report</Chip>
            )}
          </span>
        </div>
        {status && (
          <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t600)', marginTop: 8 }}>
            Tickets filed today {status.limits.createdToday} - open {status.limits.open}
          </div>
        )}
      </div>

      {show === 'report'
        ? <div key="report" className="orbit-swap"><UnitReport project={project} unit={unit.unit} title={unit.title} onNotes={onNotes} /></div>
        : show === 'spec' || !sliceView
        ? (
          <div key="spec" className="orbit-swap" style={{ padding: '14px 16px 18px', maxWidth: 812 }}>
            {show === 'outcomes' && unit.stage !== 'idea' && (
              <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)', marginBottom: 10 }}>
                This spec has no story or outcomes the board can read, so here it is as written.
              </div>
            )}
            <Markdown doc text={unit.text} />
          </div>
        )
        : (
          <div key="slice" className="orbit-swap grid lg:grid-cols-[minmax(0,1fr)_300px]">
            <div className="min-w-0">
              {steps && (
                <div style={{ padding: '14px 16px 12px' }}>
                  <StoryView steps={steps} standing={standing} focus={focus} onPick={toggle} agreed={unit.agreed} render={render} />
                </div>
              )}
              {sortGroups(unit).map(g => {
                if (g.outcomes.length === 0) return null;
                const done = g.outcomes.filter(o => o.state === 'Done').length;
                const complete = unit.agreed && done === g.outcomes.length;
                // An outcome pointed at from elsewhere on the page is never left folded away.
                const open = g.outcomes.some(o => o.outcome === focus) || !(folded[g.heading] ?? complete);
                return (
                  <div key={g.heading}>
                    <button
                      type="button"
                      aria-expanded={open}
                      onClick={() => setFolded(f => ({ ...f, [g.heading]: open }))}
                      className="orbit-tap flex items-baseline gap-3 w-full text-left"
                      style={{ padding: '12px 16px 6px', background: 'rgb(var(--w-line-rgb) / .03)', borderTop: '1px solid rgb(var(--w-line-rgb) / .08)', borderBottom: open ? '1px solid rgb(var(--w-line-rgb) / .08)' : undefined }}
                    >
                      <span aria-hidden style={{ font: `700 10px ${MONO}`, color: 'var(--w-t500)', width: 10, flex: 'none' }}>{open ? '-' : '+'}</span>
                      <span className="min-w-0" style={{ font: `700 12px ${BODY}`, color: complete ? 'var(--w-t400)' : 'var(--w-t100)' }}>{g.heading || 'Outcomes'}</span>
                      {unit.agreed && (
                        <span style={{ ...label(complete ? TIER.done.c : 'var(--w-t500)'), marginLeft: 'auto', flex: 'none' }}>
                          {done} of {g.outcomes.length} done
                        </span>
                      )}
                    </button>
                    {open && <div key="open" className="orbit-swap">{g.outcomes.map(o => (
                      <OutcomeRow key={o.outcome} o={o} agreed={unit.agreed} standing={standing.get(o.outcome)}
                        serves={serves.get(o.outcome)} hasStory={!!steps} render={render}
                        spot={focus ? (focus === o.outcome ? 'lit' : 'dim') : undefined} onFocus={() => toggle(o.outcome)} />
                    ))}</div>}
                  </div>
                );
              })}
            </div>
            {decisions && (
              <aside className="min-w-0 lg:border-l max-lg:border-t" style={{ padding: '14px 14px 16px', borderColor: 'rgb(var(--w-line-rgb) / .1)', background: 'rgb(var(--w-line-rgb) / .02)' }}>
                <DecisionsBeside set={decisions} lit={lit} onLight={setLit} onAll={onDecisions} />
              </aside>
            )}
          </div>
        )}
    </Panel>
  );
}

/** A slice's outcome groups with what is left first, then by heading: A, B, ... Z. */
function sortGroups(unit: StewardPlanUnitView) {
  const complete = (g: StewardPlanUnitView['groups'][number]) => unit.agreed && g.outcomes.every(o => o.state === 'Done');
  return [...unit.groups].sort((a, b) => Number(complete(a)) - Number(complete(b))
    || a.heading.localeCompare(b.heading, undefined, { numeric: true, sensitivity: 'base' }));
}

/**
 * The step that is the owner's, when the unit is at one: agree a draft, or say a built unit has
 * gone out. Both ask twice, because both are said once and acted on by something unattended.
 */
function Yours({ project, unit, onChanged, onNotes, onReport }: {
  project: string; unit: StewardPlanUnitView; onChanged: () => Promise<void>; onNotes: (unit: number) => void;
  /** Open the unit's report - offered beside the release, as the thing to read first. */
  onReport?: () => void;
}) {
  const [sure, setSure] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  // The decisions ticked to keep, starting on the steward's suggestions.
  const [keep, setKeep] = useState<Set<number>>(() => new Set((unit.decisions ?? []).filter(d => d.suggested && !d.promoted).map(d => d.number)));
  const run = async (act: () => Promise<unknown>) => {
    setBusy(true); setFailed(null);
    try { await act(); await onChanged(); setSure(false); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  const r = unit.release;
  const counts = r ? [
    [r.migrations, 'migration', 'migrations'], [r.config, 'thing to set up', 'things to set up'], [r.actions, 'thing yours to do', 'things yours to do'],
  ].filter(([n]) => (n as number) > 0).map(([n, one, many]) => `${n} ${n === 1 ? one : many}`) : [];
  const box = (tone: string, children: ReactNode) => (
    <div
      style={{
        marginTop: 12, padding: '10px 12px', clipPath: chamfer(8),
        background: fade(tone, 7), border: `1px solid ${fade(tone, 30)}`,
      }}
    >
      {children}
      {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 6 }}>{failed}</div>}
    </div>
  );
  const say = (text: ReactNode) => <div style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>{text}</div>;

  if (unit.stage === 'draft') {
    const open = unit.openDecisions?.length ?? 0;
    // A draft with choices left open is agreed together with its answers to them: agreeing it
    // alone is how a spec ends up "agreed" with nothing under "Decided".
    if (open > 0 && sure && !unit.cannotAgree) {
      return box(TIER.gate.c, (
        <>
          {say(<>Agreeing unit {unit.unit} settles its decisions with it. Each starts on the spec's own pick; change any you would decide differently, or leave one open for the steward to ask about later.</>)}
          <div style={{ marginTop: 10 }}>
            <DecisionSheet project={project} unit={unit.unit} mode="agree"
              onDone={async () => { await onChanged(); setSure(false); }} onCancel={() => setSure(false)} />
          </div>
        </>
      ));
    }
    return box(TIER.gate.c, (
      <>
        {say(unit.cannotAgree
          ?? <>A draft. Read the spec below; nothing is filed against it until you agree it. Agreeing commits its status line to the base branch, and the steward starts filing tickets against its outcomes on its next run.{open > 0 && <> Its spec leaves <b>{open} decision{open === 1 ? '' : 's'}</b> open, which you answer as you agree it.</>}</>)}
        {!unit.cannotAgree && open > 0 && (
          <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 8 }}>
            <GhostButton tone="go" onClick={() => setSure(true)}>Agree this slice</GhostButton>
          </div>
        )}
        {!unit.cannotAgree && open === 0 && (
          <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 8 }}>
            {sure
              ? (
                <>
                  <GhostButton tone="go" disabled={busy} onClick={() => run(() => agreeStewardUnit(project, unit.unit))}>
                    {busy ? 'Agreeing' : `Yes, agree unit ${unit.unit}`}
                  </GhostButton>
                  <GhostButton disabled={busy} onClick={() => setSure(false)}>Not yet</GhostButton>
                </>
              )
              : <GhostButton tone="go" onClick={() => setSure(true)}>Agree this slice</GhostButton>}
          </div>
        )}
      </>
    ));
  }

  if (unit.stage === 'notAgreed') {
    return box('var(--w-t500)', say('Its spec does not say it is a draft or agreed, so nothing is filed against it. Mark it agreed in the file and commit that.'));
  }

  if (unit.stage === 'released') {
    return (
      <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 12 }}>
        <span style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>
          Released {ago(unit.releasedAt)}{unit.releasedByMerge ? ', merged into the base here. Taking it back is a revert, by hand' : ''}.
        </span>
        {!unit.releasedByMerge && (
          <GhostButton disabled={busy} onClick={() => run(() => setStewardUnitReleased(project, unit.unit, false))}
            title="Put it back to built: it has not gone out after all">
            Not released after all
          </GhostButton>
        )}
        {failed && <span style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c }}>{failed}</span>}
      </div>
    );
  }

  if (!r) return null;
  const notes = counts.length > 0 || r.toKnow > 0;
  const seeNotes = notes && (
    <GhostButton onClick={() => onNotes(unit.unit)} title="This unit's notes, in the record">Read its notes</GhostButton>
  );

  if (unit.stage === 'built' && unit.sliceBranch) {
    const decisions = (unit.decisions ?? []).filter(d => !d.promoted);
    const ticked = decisions.filter(d => keep.has(d.number)).map(d => d.number);
    const flip = (n: number) => setKeep(was => {
      const next = new Set(was);
      if (!next.delete(n)) next.add(n);
      return next;
    });
    return box(r.outstanding > 0 ? TIER.gate.c : TIER.done.c, (
      <>
        {say(r.outstanding > 0
          ? <>Every outcome is done. Before it goes out: <b>{counts.join(', ')}</b> still to tick off{r.toKnow > 0 ? `, and ${r.toKnow} to know` : ''}.</>
          : <>Every outcome is done and nothing for its release is outstanding{r.toKnow > 0 ? ` (${r.toKnow} to know, which go out with it)` : ''}. Releasing merges <span style={{ font: `400 12px ${MONO}` }}>{unit.sliceBranch}</span> into the base here: it is brought up to the base first and the merge checks run on it, and nothing is pushed.</>)}
        {r.outstanding === 0 && unit.conventions && decisions.length > 0 && (
          <div style={{ marginTop: 10 }}>
            <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)', marginBottom: 6 }}>
              Tick the decisions that outlive this slice. Each goes into <span style={{ font: `400 11.5px ${MONO}` }}>{unit.conventions}</span> in the release's own commit; the rest retire with it.
            </div>
            <div className="flex flex-col gap-1.5">
              {decisions.map(d => (
                <label key={d.number} className="flex items-start gap-2.5 cursor-pointer group">
                  <input type="checkbox" checked={keep.has(d.number)} disabled={busy} onChange={() => flip(d.number)}
                    className="mt-[3px] w-3.5 h-3.5 flex-none rounded accent-amber-500 cursor-pointer disabled:cursor-default" />
                  <span className="min-w-0" style={{ font: `400 12.5px/1.45 ${BODY}`, color: 'var(--w-t200)' }}>
                    <span style={{ font: `400 11.5px ${MONO}`, color: 'var(--w-t400)' }}>#{d.number}</span> {d.title ? <b>{d.title}. </b> : null}{d.rule}
                    {d.suggested && <span className="block" style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t400)' }}>The steward suggests keeping it{d.why ? `: ${d.why}` : '.'}</span>}
                  </span>
                </label>
              ))}
            </div>
          </div>
        )}
        <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 8 }}>
          {onReport && <GhostButton onClick={onReport} title="One Classroom report on the whole slice">Read the report first</GhostButton>}
          {seeNotes}
          {r.outstanding === 0 && (sure
            ? (
              <>
                <GhostButton tone="go" disabled={busy} onClick={() => run(() => setStewardUnitReleased(project, unit.unit, true, ticked))}>
                  {busy ? 'Merging' : `Yes, merge slice ${unit.unit} into the base${ticked.length > 0 ? ` and keep ${ticked.length}` : ''}`}
                </GhostButton>
                <GhostButton disabled={busy} onClick={() => setSure(false)}>Not yet</GhostButton>
              </>
            )
            : <GhostButton tone="go" onClick={() => setSure(true)}>Release it</GhostButton>)}
        </div>
      </>
    ));
  }

  if (unit.stage === 'built') {
    return box(r.outstanding > 0 ? TIER.gate.c : TIER.done.c, (
      <>
        {say(r.outstanding > 0
          ? <>Every outcome is done. Before it goes out: <b>{counts.join(', ')}</b> still to tick off{r.toKnow > 0 ? `, and ${r.toKnow} to know` : ''}.</>
          : <>Every outcome is done and nothing for its release is outstanding{r.toKnow > 0 ? ` (${r.toKnow} to know, which go out with it)` : ''}. Releasing it is yours: merge and push by hand, then say so here.</>)}
        <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 8 }}>
          {onReport && <GhostButton onClick={onReport} title="One Classroom report on the whole slice">Read the report first</GhostButton>}
          {seeNotes}
          {r.outstanding === 0 && (sure
            ? (
              <>
                <GhostButton tone="go" disabled={busy} onClick={() => run(() => setStewardUnitReleased(project, unit.unit, true))}>
                  {busy ? 'Recording' : `Yes, unit ${unit.unit} has been released`}
                </GhostButton>
                <GhostButton disabled={busy} onClick={() => setSure(false)}>Not yet</GhostButton>
              </>
            )
            : <GhostButton tone="go" onClick={() => setSure(true)}>It has been released</GhostButton>)}
        </div>
      </>
    ));
  }

  // Agreed or building: nothing is the owner's yet, but what its release will ask is already known.
  return notes ? (
    <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 12 }}>
      <span style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>
        For its release so far: {[...counts, r.toKnow > 0 ? `${r.toKnow} to know` : ''].filter(Boolean).join(', ')}.
      </span>
      {seeNotes}
    </div>
  ) : null;
}

/**
 * Abandoning an open slice: asked twice, and when slices are built on it, the board's question
 * about them - re-cut each from the base, or abandon each too - answered here before anything moves.
 */
function Abandon({ project, unit, onChanged }: { project: string; unit: StewardPlanUnitView; onChanged: () => Promise<void> }) {
  const [sure, setSure] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const [asked, setAsked] = useState<string | null>(null);
  const go = async (children?: 'recut' | 'abandon') => {
    setBusy(true); setFailed(null);
    try {
      const r = await abandonStewardUnit(project, unit.unit, children);
      if (r.answer === 'children') { setAsked(r.why); return; }
      setSure(false); setAsked(null);
      await onChanged();
    } catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };
  const kids = unit.builtOnIt ?? [];
  return (
    <div style={{ marginTop: 10 }}>
      <div className="flex items-center gap-2 flex-wrap">
        {!sure && <GhostButton onClick={() => setSure(true)} title="Stop this slice unreleased; its branch is kept">Abandon this slice</GhostButton>}
        {sure && !asked && (
          <>
            <span className="min-w-0" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t300)' }}>
              Its spec is marked abandoned and nothing more is filed against it. {unit.sliceBranch} is kept.
              {kids.length > 0 && <> Slice{kids.length === 1 ? '' : 's'} {kids.join(' and ')} {kids.length === 1 ? 'is' : 'are'} built on it.</>}
            </span>
            <GhostButton tone="danger" disabled={busy} onClick={() => go()}>{busy ? 'Abandoning' : `Yes, abandon slice ${unit.unit}`}</GhostButton>
            <GhostButton disabled={busy} onClick={() => setSure(false)}>Keep it</GhostButton>
          </>
        )}
        {sure && asked && (
          <>
            <span className="min-w-0" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t200)' }}>{asked}</span>
            <GhostButton tone="go" disabled={busy} onClick={() => go('recut')}>Re-cut from the base</GhostButton>
            <GhostButton tone="danger" disabled={busy} onClick={() => go('abandon')}>Abandon {kids.length === 1 ? 'it' : 'them'} too</GhostButton>
            <GhostButton disabled={busy} onClick={() => { setSure(false); setAsked(null); }}>Keep all of them</GhostButton>
          </>
        )}
      </div>
      {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 6 }}>{failed}</div>}
    </div>
  );
}

/**
 * An agreed unit whose spec still leaves choices open: agreed before its decisions were answered,
 * or with some left for later. Whatever leans on one waits, so they are offered here to settle.
 */
function StillOpen({ project, unit, onChanged }: { project: string; unit: StewardPlanUnitView; onChanged: () => Promise<void> }) {
  const [settling, setSettling] = useState(false);
  const n = unit.openDecisions?.length ?? 0;
  const tone = TIER.gate.c;
  return (
    <div style={{ marginTop: 12, padding: '10px 12px', clipPath: chamfer(8), background: fade(tone, 7), border: `1px solid ${fade(tone, 30)}` }}>
      <div className="flex items-center gap-x-3 gap-y-2 flex-wrap">
        <span className="min-w-0" style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>
          Its spec still leaves <b>{n} decision{n === 1 ? '' : 's'}</b> open. Nothing that leans on one is filed until it is settled.
        </span>
        {!settling && <GhostButton tone="go" onClick={() => setSettling(true)}>Settle {n === 1 ? 'it' : 'them'}</GhostButton>}
      </div>
      {settling && (
        <div key="sheet" className="orbit-swap" style={{ marginTop: 10 }}>
          <DecisionSheet project={project} unit={unit.unit} mode="settle"
            onDone={async () => { setSettling(false); await onChanged(); }} onCancel={() => setSettling(false)} />
        </div>
      )}
    </div>
  );
}

/** One promise of the unit: what it says, where it stands, the steps it serves, and the tickets carrying it. */
function OutcomeRow({ o, agreed, standing, serves, hasStory, render, spot, onFocus }: {
  o: StewardPlanOutcome; agreed: boolean; standing?: Standing; serves?: number[]; hasStory: boolean;
  render: (text: string) => ReactNode; spot?: 'lit' | 'dim'; onFocus: () => void;
}) {
  const look = standing?.look ?? (o.state === 'Done' ? 'done' : o.state === 'InProgress' ? 'moving' : 'todo');
  const c = LOOK[look].c;
  const open = o.tickets.filter(t => t.phase !== 'Done');
  // What the row is for in the story: the steps it makes true, or that it is a check no step cites.
  const role = !hasStory ? null
    : serves ? `step ${serves.join(', ')}`
    : isWalkthrough(o) ? 'drives the whole story' : 'a check: no step cites it';
  return (
    <div
      className="orbit-spot spot-flat flex gap-x-3 gap-y-1 max-sm:flex-wrap" data-spot={spot}
      style={{ padding: '9px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)', background: spot === 'lit' ? 'rgb(var(--w-accent-rgb) / .05)' : undefined }}
    >
      <div className="flex items-baseline gap-3 flex-none" style={{ width: agreed ? 128 : 30 }}>
        <button type="button" onClick={onFocus} className="orbit-tap" title="Narrow the slice to this outcome"
          style={{ font: `700 11.5px ${MONO}`, color: 'var(--w-t200)', width: 30, flex: 'none', textAlign: 'left' }}>{o.outcome}</button>
        {/* A draft's outcomes have no standing yet: saying "not started" of each would be noise. */}
        {agreed && <span style={label(c)}>{LOOK[look].word}</span>}
      </div>
      <div className="min-w-0 flex-1 max-sm:basis-full">
        <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: look === 'done' ? 'var(--w-t400)' : 'var(--w-t200)' }}>
          {render(o.text)}
        </div>
        {(o.note || o.moreToFile) && (
          <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: fade('var(--w-t400)', 80), marginTop: 3 }}>
            {o.moreToFile && <span style={{ color: TIER.active.c }}>More to file. </span>}{o.note}
          </div>
        )}
        {(o.tickets.length > 0 || role) && (
          <div className="flex flex-wrap gap-x-2.5 gap-y-0.5" style={{ marginTop: 4 }}>
            {role && <span style={{ font: `400 11px ${MONO}`, color: 'var(--w-t500)' }}>{role}</span>}
            {/* What is still moving is named; what is merged is only counted, or a dozen numbers
                bury the one ticket anybody is waiting for. */}
            {open.map(t => (
              <span key={t.id} title={t.title} style={{ font: `600 11px ${MONO}`, color: t.phase === 'Blocked' ? TIER.blocked.c : TIER.active.c }}>
                #{t.id} <span style={{ fontWeight: 400, color: 'var(--w-t400)' }}>{phaseWord(t.phase)}</span>
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

/**
 * An outcome's sentence, with the spec's own emphasis and code kept, and each decision it cites by
 * number made a link to that decision beside the slice.
 */
function Sentence({ text, known, onDecision }: { text: string; known?: Set<number>; onDecision?: (n: number) => void }) {
  // One line of prose: the document renderer would wrap it in a paragraph with its own size.
  const parts = text.split(/(\*\*.+?\*\*|`[^`]+`|#\d+\b)/g);
  return (
    <>
      {parts.map((p, i) => {
        if (p.startsWith('**') && p.endsWith('**') && p.length > 4) return <strong key={i} style={{ fontWeight: 600, color: 'var(--w-t100)' }}>{p.slice(2, -2)}</strong>;
        if (p.startsWith('`') && p.endsWith('`') && p.length > 2) return <code key={i} style={{ font: `400 11.5px ${MONO}`, color: 'var(--w-t300)' }}>{p.slice(1, -1)}</code>;
        const n = /^#(\d+)$/.exec(p);
        if (n && known?.has(Number(n[1])) && onDecision) {
          return (
            <button key={i} type="button" onClick={() => onDecision(Number(n[1]))} className="orbit-tap" title={`Decision #${n[1]}`}
              style={{ font: `700 11px ${MONO}`, color: 'var(--w-accent-text)', borderBottom: '1px dotted rgb(var(--w-accent-rgb) / .6)' }}>
              {p}
            </button>
          );
        }
        return p;
      })}
    </>
  );
}
