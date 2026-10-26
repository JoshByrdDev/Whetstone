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
import { agreeStewardUnit, setStewardUnitReleased } from '../api/steward';
import type {
  StewardMode, StewardPlanOutcome, StewardPlanUnitView, StewardPlanView, StewardQuestion, StewardRegister, StewardStatus, UnitStage,
} from '../api/steward';

/**
 * The plan, as something to read and to move along.
 *
 * A plan is its slices, a charter that decides nothing, and the register of decisions
 * (docs/plans-as-slices.md). A person needs three things of it at different moments: where the
 * whole stands (the slices in lanes by stage, each with the strip of its outcomes), what one slice
 * promised and how far each promise is (its story step by step, its outcomes in the spec's own words
 * and order, what stands between it and built, the decisions it is held to - see stewardSlice.tsx),
 * and the plan as read - generated from the charter, the project's decisions and the slices, never
 * maintained by hand.
 *
 * A slice goes one way: an idea, a draft, agreed, building, built, released. Two of those steps are
 * a person's and are taken here - agreeing a draft, and saying a built slice has gone out. Ideas are
 * the owner's too, written in the repo or committed from a steward's proposal. Everything else is
 * the steward's and the fleet's, and is only shown.
 */

type Selected = number | 'plan' | 'master';

/** Where a unit stands, in words, and the tier that colours it. */
const STAGE: Record<UnitStage, { word: string; tier: keyof typeof TIER }> = {
  idea: { word: 'idea', tier: 'backlog' },
  draft: { word: 'draft - yours to agree', tier: 'gate' },
  notAgreed: { word: 'not agreed', tier: 'backlog' },
  agreed: { word: 'agreed', tier: 'queue' },
  building: { word: 'building', tier: 'active' },
  built: { word: 'built - yours to release', tier: 'gate' },
  released: { word: 'released', tier: 'done' },
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

export function StewardPlan({ project, view, status, waiting, register, mode, onPlan, onChanged, onNotes, onNow, onDecisions, onNew }: {
  project: string;
  view: StewardPlanView | null;
  status: StewardStatus | null;
  /** The questions on Now: what holds an outcome, and which decisions an open question touches. */
  waiting: StewardQuestion[];
  register: StewardRegister | null;
  /** What the steward may do, which says whose a merge is. */
  mode?: StewardMode;
  /** Read another of the project's plans; null is the one being worked. */
  onPlan: (key: string | null) => void;
  /** Something here changed what the page shows. */
  onChanged: () => Promise<void>;
  /** Show a unit's notes for its release, in the record. */
  onNotes: (unit: number) => void;
  /** Go to Now, where questions are answered. */
  onNow: () => void;
  /** Go to the register. */
  onDecisions: () => void;
  /** Go to the tab where a plan is started. */
  onNew: () => void;
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
  const plans = view?.plan?.plans ?? [];
  const plan = view?.plan?.plan;
  const [picked, setPicked] = useState<Selected | null>(null);
  // Another plan's units are another set: whatever was picked in the last one means nothing here.
  useEffect(() => { setPicked(null); }, [plan?.key]);

  // Until somebody picks, the page is on the slice being worked - the latest one being built - and
  // then on one that wants something of you. A release waits in its own band, so it no longer
  // decides where the page opens.
  const building = [...units].reverse().find(u => u.stage === 'building');
  const asking = units.find(u => u.stage === 'draft' && !u.cannotAgree) ?? units.find(u => u.stage === 'built');
  const selected: Selected | null = picked ?? building?.unit ?? current ?? asking?.unit
    ?? (units.length > 0 ? units[units.length - 1].unit : view?.readable ? 'plan' : view?.master ? 'master' : null);
  const unit = typeof selected === 'number' ? units.find(u => u.unit === selected) : undefined;

  if (!view) return <Quiet>Reading the plan.</Quiet>;

  const working = !plan || plan.working;
  const built = working ? units.filter(u => u.stage === 'built') : [];
  return (
    <div className="mb-8">
      {plans.length > 1 && (
        <div className="flex flex-wrap items-center gap-2" style={{ marginBottom: 12 }}>
          <span style={label()}>Plan</span>
          {plans.map(p => (
            <Chip key={p.key} on={plan?.key === p.key} onClick={() => onPlan(p.working ? null : p.key)}
              title={p.working ? 'The plan being worked' : 'A plan this project has finished'}>
              {p.name}{p.working ? '' : ' - finished'}
            </Chip>
          ))}
        </div>
      )}

      {(view.plan?.moved?.length ?? 0) > 0 && (
        <Panel tone={TIER.blocked.c} className="mb-3">
          {view.plan!.moved!.map((m, i) => (
            <div key={i} style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>{m}</div>
          ))}
        </Panel>
      )}

      {units.length === 0 && !view.master && !view.readable
        ? <Quiet>{view.why ?? 'This plan has no slices yet.'}</Quiet>
        : (
          <>
            {(view.readable || view.master) && (
              <div className="flex flex-wrap items-center gap-2" style={{ marginBottom: 10 }}>
                {view.readable && (
                  <Chip on={selected === 'plan'} onClick={() => setPicked('plan')} title={view.charter ? 'The charter, decisions and slices, generated' : 'The decisions and slices, generated'}>
                    Whole plan
                  </Chip>
                )}
                {view.master && (
                  <Chip on={selected === 'master'} onClick={() => setPicked('master')} title={view.master.file}>
                    {view.charter ? 'Original plan (retired)' : 'Plan document'}
                  </Chip>
                )}
              </div>
            )}

            {narrow
              ? <SliceStrip units={units} selected={selected} onPick={setPicked} />
              : <Lanes units={units} selected={selected} onPick={setPicked} />}

            {built.length > 0 && <ReleaseBand units={built} selected={selected} onPick={setPicked} onNotes={onNotes} />}

            {selected === 'plan' && view.readable && (
              <Panel key="plan" className="orbit-swap">
                <div style={{ maxWidth: 780 }}><Markdown doc text={view.readable} /></div>
              </Panel>
            )}
            {selected === 'master' && view.master && (
              <Panel key="master" className="orbit-swap">
                <div style={{ maxWidth: 780 }}><Markdown doc text={view.master.text} /></div>
              </Panel>
            )}
            {unit && (
              <UnitView
                key={`${plan?.key}.${unit.unit}`} project={project} plan={plan?.key} unit={unit} working={working}
                status={unit.unit === current ? status : null} onChanged={onChanged} onNotes={onNotes}
                waiting={waiting} register={working ? register : null} mode={mode} onNow={onNow} onDecisions={onDecisions}
              />
            )}
          </>
        )}

      {units.length === 0 && !view.master && !view.readable && (
        <div style={{ marginTop: 10 }}><GhostButton tone="go" onClick={onNew}>Start a plan</GhostButton></div>
      )}
    </div>
  );
}

type Unit = StewardPlanUnitView;

/** The plan's stages as lanes, so its shape reads at once: what has gone out, what is yours, what is moving, what is still an idea. */
const LANES: { title: string; stages: UnitStage[]; c: string; empty: string }[] = [
  { title: 'Released', stages: ['released'], c: TIER.done.c, empty: 'Nothing released yet.' },
  { title: 'Yours', stages: ['draft', 'built', 'notAgreed'], c: TIER.gate.c, empty: 'Nothing to agree or release.' },
  { title: 'Building', stages: ['building'], c: TIER.active.c, empty: 'Nothing being built.' },
  { title: 'Agreed', stages: ['agreed'], c: TIER.queue.c, empty: 'Nothing agreed and waiting.' },
  { title: 'Ideas', stages: ['idea'], c: 'var(--w-t400)', empty: 'No ideas left.' },
];

function Lanes({ units, selected, onPick }: { units: Unit[]; selected: Selected | null; onPick: (s: Selected) => void }) {
  return (
    <Panel pad={false} className="mb-3">
      <div className="grid" style={{ gridTemplateColumns: 'repeat(5, minmax(0, 1fr))' }}>
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

/** One slice in the plan: its number and title, where it stands, and the strip of its outcomes. */
function SliceCard({ u, on, onClick }: { u: Unit; on: boolean; onClick: () => void }) {
  const s = STAGE[u.stage];
  const p = u.progress;
  const sub = u.stage === 'idea' ? 'idea'
    : u.stage === 'draft' ? `draft${(u.openDecisions?.length ?? 0) > 0 ? ` - ${u.openDecisions!.length} to settle` : ''}`
    : u.stage === 'built' ? 'built - yours to release'
    : u.stage === 'released' ? `released${u.releasedAt ? ` ${u.releasedAt.slice(0, 10)}` : ''}`
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

/** Built slices waiting to go out, in a band of their own: releasing is its own kind of work. */
function ReleaseBand({ units, selected, onPick, onNotes }: {
  units: Unit[]; selected: Selected | null; onPick: (s: Selected) => void; onNotes: (unit: number) => void;
}) {
  return (
    <div className="flex flex-col gap-1.5 mb-4" style={{ padding: '9px 12px', border: `1px solid ${fade(TIER.gate.c, 30)}`, background: fade(TIER.gate.c, 5) }}>
      <span style={label(TIER.gate.c)}>To release - {units.length}</span>
      {units.map(u => {
        const r = u.release;
        return (
          <div key={u.unit} className="flex items-baseline gap-x-3 gap-y-1 flex-wrap" style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t300)' }}>
            <button type="button" onClick={() => onPick(u.unit)} aria-current={selected === u.unit ? 'true' : undefined} className="orbit-pick text-left min-w-0"
              style={{ font: `600 12.5px ${BODY}`, color: 'var(--w-t100)' }}>
              {String(u.unit).padStart(2, '0')} {shortTitle(u)}
            </button>
            <span className="min-w-0">
              {r && r.outstanding > 0
                ? <><b style={{ fontWeight: 600, color: TIER.gate.c }}>{r.outstanding}</b> to tick off before it goes out{r.toKnow > 0 ? `, ${r.toKnow} to know` : ''}</>
                : <>nothing outstanding{r && r.toKnow > 0 ? `, ${r.toKnow} to know` : ''}</>}
            </span>
            {r && r.outstanding + r.toKnow > 0 && (
              <button type="button" onClick={() => onNotes(u.unit)} className="orbit-tap" style={label('var(--w-t300)')}>Its notes</button>
            )}
          </div>
        );
      })}
    </div>
  );
}

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

function UnitView({ project, plan, unit, working, status, onChanged, onNotes, waiting, register, mode, onNow, onDecisions }: {
  project: string; plan: string | undefined; unit: StewardPlanUnitView; working: boolean; status: StewardStatus | null;
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
  const [leftOnly, setLeftOnly] = useState(() => remembered('plan.left') === '1');
  const pick = (v: Show) => { setShow(v); remember('plan.show', v); };
  const left = (v: boolean) => { setLeftOnly(v); remember('plan.left', v ? '1' : '0'); };

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

        {working && <Yours project={project} unit={unit} onChanged={onChanged} onNotes={onNotes}
          onReport={show === 'report' ? undefined : () => pick('report')} />}
        {working && unit.agreed && (unit.openDecisions?.length ?? 0) > 0 && (
          <StillOpen project={project} unit={unit} onChanged={onChanged} />
        )}
        {working && unit.agreed && unit.stage !== 'built' && unit.stage !== 'released' && (
          <Between unit={unit} standing={standing} onPick={id => { toggle(id); pick('outcomes'); }} onNow={onNow} />
        )}

        <div className="flex items-center gap-x-4 gap-y-2 flex-wrap" style={{ marginTop: 12 }}>
          <span className="truncate min-w-0" style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>{unit.file}</span>
          <span className="flex items-center gap-2 flex-wrap" style={{ marginLeft: 'auto' }}>
            {show === 'outcomes' && unit.agreed && unit.progress.done > 0 && unit.progress.done < unit.progress.outcomes && (
              <Chip on={leftOnly} onClick={() => left(!leftOnly)} title="Hide the outcomes that are done">What is left</Chip>
            )}
            <Chip on={show === 'outcomes'} onClick={() => pick('outcomes')} title="Its story, its outcomes and the decisions it is held to">The slice</Chip>
            <Chip on={show === 'spec'} onClick={() => pick('spec')} title="The unit's spec, as committed">The spec</Chip>
            {reportable && (
              <Chip on={show === 'report'} onClick={() => pick('report')} title="One Classroom report on the whole unit">Report</Chip>
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
        ? <div key="report" className="orbit-swap"><UnitReport project={project} plan={plan} unit={unit.unit} title={unit.title} onNotes={onNotes} /></div>
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
              {unit.groups.map(g => {
                const shown = leftOnly ? g.outcomes.filter(o => o.state !== 'Done') : g.outcomes;
                if (shown.length === 0) return null;
                const done = g.outcomes.filter(o => o.state === 'Done').length;
                return (
                  <div key={g.heading}>
                    <div
                      className="flex items-baseline gap-3"
                      style={{ padding: '12px 16px 6px', background: 'rgb(var(--w-line-rgb) / .03)', borderTop: '1px solid rgb(var(--w-line-rgb) / .08)', borderBottom: '1px solid rgb(var(--w-line-rgb) / .08)' }}
                    >
                      <span className="min-w-0" style={{ font: `700 12px ${BODY}`, color: 'var(--w-t100)' }}>{g.heading || 'Outcomes'}</span>
                      {unit.agreed && (
                        <span style={{ ...label(done === g.outcomes.length ? TIER.done.c : 'var(--w-t500)'), marginLeft: 'auto', flex: 'none' }}>
                          {done} of {g.outcomes.length} done
                        </span>
                      )}
                    </div>
                    {shown.map(o => (
                      <OutcomeRow key={o.outcome} o={o} agreed={unit.agreed} standing={standing.get(o.outcome)}
                        serves={serves.get(o.outcome)} hasStory={!!steps} render={render}
                        spot={focus ? (focus === o.outcome ? 'lit' : 'dim') : undefined} onFocus={() => toggle(o.outcome)} />
                    ))}
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
            <GhostButton tone="go" onClick={() => setSure(true)}>Agree this unit</GhostButton>
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
              : <GhostButton tone="go" onClick={() => setSure(true)}>Agree this unit</GhostButton>}
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
        <span style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>Released {ago(unit.releasedAt)}.</span>
        <GhostButton disabled={busy} onClick={() => run(() => setStewardUnitReleased(project, unit.unit, false))}
          title="Put it back to built: it has not gone out after all">
          Not released after all
        </GhostButton>
        {failed && <span style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c }}>{failed}</span>}
      </div>
    );
  }

  if (!r) return null;
  const notes = counts.length > 0 || r.toKnow > 0;
  const seeNotes = notes && (
    <GhostButton onClick={() => onNotes(unit.unit)} title="This unit's notes, in the record">Read its notes</GhostButton>
  );

  if (unit.stage === 'built') {
    return box(r.outstanding > 0 ? TIER.gate.c : TIER.done.c, (
      <>
        {say(r.outstanding > 0
          ? <>Every outcome is done. Before it goes out: <b>{counts.join(', ')}</b> still to tick off{r.toKnow > 0 ? `, and ${r.toKnow} to know` : ''}.</>
          : <>Every outcome is done and nothing for its release is outstanding{r.toKnow > 0 ? ` (${r.toKnow} to know, which go out with it)` : ''}. Releasing it is yours: merge and push by hand, then say so here.</>)}
        <div className="flex items-center gap-2 flex-wrap" style={{ marginTop: 8 }}>
          {onReport && <GhostButton onClick={onReport} title="One Classroom report on the whole unit">Read the report first</GhostButton>}
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
