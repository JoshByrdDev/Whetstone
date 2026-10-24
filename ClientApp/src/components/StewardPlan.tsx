import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { Chip, GhostButton, Panel } from './ui';
import { BODY, MONO, TIER, chamfer, fade } from './orbit';
import { Markdown } from './Markdown';
import { UnitReport } from './UnitReport';
import { DecisionSheet } from './DecisionSheet';
import { Fold, Quiet, ago, label, remember, remembered } from './stewardParts';
import { agreeStewardUnit, setStewardUnitReleased, startStewardPlan } from '../api/steward';
import type {
  StewardPlanOutcome, StewardPlanUnitView, StewardPlanView, StewardStatus, UnitStage,
} from '../api/steward';

/**
 * The plan, as something to read and to move along.
 *
 * A plan is its slices, a charter that decides nothing, and the register of decisions
 * (docs/plans-as-slices.md). A person needs three things of it at different moments: where the
 * whole stands (the rail), what one slice promised and how far each promise is (its outcomes, in the
 * spec's own words and order), and the plan as read - generated from the charter, the project's
 * decisions and the slices, never maintained by hand.
 *
 * A slice goes one way: an idea, a draft, agreed, building, built, released. Two of those steps are
 * a person's and are taken here - agreeing a draft, and saying a built slice has gone out. Ideas are
 * the owner's too, written in the repo or committed from a steward's proposal. Everything else is
 * the steward's and the fleet's, and is only shown.
 */

type Selected = number | 'plan' | 'master';

const STATE_TIER: Record<StewardPlanOutcome['state'], keyof typeof TIER> = {
  NotStarted: 'backlog', InProgress: 'active', Done: 'done',
};
const STATE_WORD: Record<StewardPlanOutcome['state'], string> = {
  NotStarted: 'not started', InProgress: 'in progress', Done: 'done',
};

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

export function StewardPlan({ project, view, status, onPlan, onChanged, onNotes }: {
  project: string;
  view: StewardPlanView | null;
  status: StewardStatus | null;
  /** Read another of the project's plans; null is the one being worked. */
  onPlan: (key: string | null) => void;
  /** Something here changed what the page shows. */
  onChanged: () => Promise<void>;
  /** Show a unit's notes for its release, in the record. */
  onNotes: (unit: number) => void;
}) {
  const current = view?.plan?.current;
  // A server from before units had stages says only agreed, draft and done: read the stage off those.
  const units = (view?.plan?.units ?? []).map(u => ({
    ...u,
    stage: u.stage ?? (u.done ? 'built' : u.agreed ? (u.unit === current ? 'building' : 'agreed') : u.draft ? 'draft' : 'notAgreed'),
  }));
  const plans = view?.plan?.plans ?? [];
  const plan = view?.plan?.plan;
  const [picked, setPicked] = useState<Selected | null>(null);
  // Another plan's units are another set: whatever was picked in the last one means nothing here.
  useEffect(() => { setPicked(null); }, [plan?.key]);

  // Until somebody picks, the page is on the unit that wants something: a draft to agree or a
  // built unit to release first, then the one being built.
  const asking = units.find(u => u.stage === 'built') ?? units.find(u => u.stage === 'draft' && !u.cannotAgree);
  const selected: Selected | null = picked ?? asking?.unit ?? current
    ?? (units.length > 0 ? units[units.length - 1].unit : view?.readable ? 'plan' : view?.master ? 'master' : null);
  const unit = typeof selected === 'number' ? units.find(u => u.unit === selected) : undefined;

  if (!view) return <Quiet>Reading the plan.</Quiet>;

  const working = !plan || plan.working;
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
            <div className="flex flex-wrap gap-2" style={{ marginBottom: 14 }}>
              {view.readable && (
                <RailItem on={selected === 'plan'} onClick={() => setPicked('plan')} lead="All"
                  title={plan?.name && plan.name !== 'The plan' ? plan.name : 'The whole plan'}
                  sub={view.charter ? 'charter, decisions, slices' : 'decisions and slices'} />
              )}
              {view.master && (
                <RailItem on={selected === 'master'} onClick={() => setPicked('master')} lead="Doc"
                  title={view.charter ? 'The older plan document' : 'The plan document'} sub={view.master.file} />
              )}
              {units.map(u => {
                const s = STAGE[u.stage];
                return (
                  <RailItem
                    key={u.unit} on={selected === u.unit} onClick={() => setPicked(u.unit)}
                    lead={String(u.unit).padStart(2, '0')} title={u.title} colour={TIER[s.tier].c}
                    sub={`${s.word}${u.stage === 'building' || u.stage === 'agreed' ? ` - ${u.progress.percent}%` : ''}`}
                    percent={u.agreed ? u.progress.percent : undefined}
                  />
                );
              })}
            </div>

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
              />
            )}
          </>
        )}

      {working && <NewPlan project={project} has={units.length > 0 || !!view.master || !!view.charter} onStarted={onChanged} />}
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

function UnitView({ project, plan, unit, working, status, onChanged, onNotes }: {
  project: string; plan: string | undefined; unit: StewardPlanUnitView; working: boolean; status: StewardStatus | null;
  onChanged: () => Promise<void>; onNotes: (unit: number) => void;
}) {
  // A report has something to say once anything of the unit is done, and is the thing to read
  // before a built unit is released.
  const reportable = unit.agreed && (unit.progress.done > 0 || unit.stage === 'built' || unit.stage === 'released');
  const [show, setShow] = useState<Show>(() => {
    // A draft is there to be read before it is agreed, and an idea is only its words.
    if (unit.stage === 'draft' || unit.stage === 'idea') return 'spec';
    const was = remembered('plan.show');
    return was === 'spec' || (was === 'report' && reportable) ? was : 'outcomes';
  });
  const [leftOnly, setLeftOnly] = useState(() => remembered('plan.left') === '1');
  const pick = (v: Show) => { setShow(v); remember('plan.show', v); };
  const left = (v: boolean) => { setLeftOnly(v); remember('plan.left', v ? '1' : '0'); };

  const s = STAGE[unit.stage];
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
        <Steps stage={unit.stage} />

        {working && <Yours project={project} unit={unit} onChanged={onChanged} onNotes={onNotes}
          onReport={show === 'report' ? undefined : () => pick('report')} />}
        {working && unit.agreed && (unit.openDecisions?.length ?? 0) > 0 && (
          <StillOpen project={project} unit={unit} onChanged={onChanged} />
        )}

        <div className="flex items-center gap-x-4 gap-y-2 flex-wrap" style={{ marginTop: 12 }}>
          <span style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t400)' }}>
            {p.outcomes} outcome{p.outcomes === 1 ? '' : 's'}
            {unit.agreed && <>: {p.done} done, {p.inProgress} in progress, {p.notStarted} not started</>}
          </span>
          <span className="truncate min-w-0" style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>{unit.file}</span>
          <span className="flex items-center gap-2 flex-wrap" style={{ marginLeft: 'auto' }}>
            {show === 'outcomes' && unit.agreed && p.done > 0 && p.done < p.outcomes && (
              <Chip on={leftOnly} onClick={() => left(!leftOnly)} title="Hide the outcomes that are done">What is left</Chip>
            )}
            <Chip on={show === 'outcomes'} onClick={() => pick('outcomes')}>Outcomes</Chip>
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
        : show === 'spec'
        ? <div key="spec" className="orbit-swap" style={{ padding: '14px 16px 18px', maxWidth: 812 }}><Markdown doc text={unit.text} /></div>
        : !anyOutcomes
          ? <div style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)', padding: '14px 16px' }}>
            {unit.stage === 'idea' ? 'An idea has no outcomes yet: the steward writes them when it expands the idea into a draft.' : 'This spec lists no outcomes the board can read. Open the spec to read it as written.'}
          </div>
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
                      {unit.agreed && (
                        <span style={{ ...label(done === g.outcomes.length ? TIER.done.c : 'var(--w-t500)'), marginLeft: 'auto', flex: 'none' }}>
                          {done} of {g.outcomes.length} done
                        </span>
                      )}
                    </div>
                    {shown.map(o => <OutcomeRow key={o.outcome} o={o} agreed={unit.agreed} />)}
                  </div>
                );
              })}
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

/** One promise of the unit: what it says, where it stands, and the tickets carrying it. */
function OutcomeRow({ o, agreed }: { o: StewardPlanOutcome; agreed: boolean }) {
  const c = TIER[STATE_TIER[o.state]].c;
  const open = o.tickets.filter(t => t.phase !== 'Done');
  return (
    <div
      className="flex gap-x-3 gap-y-1 max-sm:flex-wrap"
      style={{ padding: '9px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)' }}
    >
      <div className="flex items-baseline gap-3 flex-none" style={{ width: agreed ? 128 : 30 }}>
        <span style={{ font: `700 11.5px ${MONO}`, color: 'var(--w-t200)', width: 30, flex: 'none' }}>{o.outcome}</span>
        {/* A draft's outcomes have no standing yet: saying "not started" of each would be noise. */}
        {agreed && <span style={label(c)}>{STATE_WORD[o.state]}</span>}
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

const FIELD = 'w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none';

/**
 * Start a plan: its charter, and the ideas it starts from - a title and a paragraph each. The
 * charter is written afterwards, in the repo; the steward expands the first idea into a draft for
 * you to agree. Ideas are yours, so they are written here, not by the steward.
 */
function NewPlan({ project, has, onStarted }: { project: string; has: boolean; onStarted: () => Promise<void> }) {
  const [name, setName] = useState('');
  const [key, setKey] = useState('');
  const [folder, setFolder] = useState('');
  const [ideas, setIdeas] = useState<{ title: string; paragraph: string }[]>([{ title: '', paragraph: '' }]);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const [made, setMade] = useState<{ charter: string; ideas: string[]; settings: string } | null>(null);

  // The key follows the name until somebody types one of their own.
  const [keyTouched, setKeyTouched] = useState(false);
  const slug = (v: string) => v.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);
  const theKey = keyTouched ? key : slug(name);
  const theFolder = folder.trim() || (theKey ? `docs/plans/${theKey}` : '');
  const written = ideas.filter(i => i.title.trim());
  const ready = name.trim().length >= 3 && !!theKey && written.length > 0 && written.every(i => i.paragraph.trim());
  const setIdea = (n: number, v: Partial<{ title: string; paragraph: string }>) =>
    setIdeas(all => all.map((x, i) => (i === n ? { ...x, ...v } : x)));

  const start = async () => {
    setBusy(true); setFailed(null);
    try {
      const r = await startStewardPlan(project, {
        key: theKey, name: name.trim(), folder: folder.trim() || undefined,
        ideas: written.map(i => ({ title: i.title.trim(), paragraph: i.paragraph.trim() })),
      });
      setMade({ charter: r.data.charter, ideas: r.data.ideas, settings: r.settings });
      await onStarted();
    } catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  return (
    <div style={{ marginTop: 22 }}>
      <Fold id="new-plan" open={!has} title="Start a new plan" colour="var(--w-accent)"
        meta={has ? 'once this one is finished' : undefined}>
        <Panel className="mb-2">
          {made
            ? (
              <div style={{ font: `400 12.5px/1.6 ${BODY}`, color: 'var(--w-t200)' }}>
                The plan is started. Its charter and ideas are on the base branch:
                <div style={{ font: `400 11.5px ${MONO}`, color: 'var(--w-t300)', margin: '6px 0' }}>
                  {made.charter}{made.ideas.map(i => <span key={i}><br />{i}</span>)}
                </div>
                Write the charter in the repo: why the plan exists, its words, where things are. The steward expands the
                first idea into a draft for you to agree here. The project's settings now point at this plan
                (<span style={{ font: `400 11.5px ${MONO}` }}>{made.settings}</span>): commit that file in the project's folder.
              </div>
            )
            : (
              <>
                <div style={{ font: `400 12px/1.55 ${BODY}`, color: 'var(--w-t400)', marginBottom: 10 }}>
                  {has
                    ? 'A project works one plan at a time. Starting another keeps this one to be read and moves the steward to the new one, so it waits until every ticket of this plan is closed.'
                    : 'A plan is a charter that says why, and its slices in order. Each slice starts as an idea: a title and a paragraph. The steward expands one idea at a time into a draft when its turn comes.'}
                </div>
                <div className="grid gap-2 sm:grid-cols-3">
                  <label className="min-w-0">
                    <div style={{ ...label(), marginBottom: 4 }}>What it is called</div>
                    <input value={name} onChange={e => setName(e.target.value)} placeholder="Customer accounts" className={FIELD} />
                  </label>
                  <label className="min-w-0">
                    <div style={{ ...label(), marginBottom: 4 }}>Its key</div>
                    <input value={theKey} onChange={e => { setKeyTouched(true); setKey(slug(e.target.value)); }} placeholder="customer-accounts" className={FIELD} />
                  </label>
                  <label className="min-w-0">
                    <div style={{ ...label(), marginBottom: 4 }}>Where in the repo</div>
                    <input value={folder} onChange={e => setFolder(e.target.value)} placeholder={theFolder || 'docs/plans/its-key'} className={FIELD} />
                  </label>
                </div>

                <div style={{ ...label(), margin: '14px 0 6px' }}>Its ideas, in order</div>
                <div className="flex flex-col gap-2">
                  {ideas.map((idea, n) => (
                    <div key={n} className="flex flex-col gap-1.5">
                      <div className="flex items-center gap-2 min-w-0">
                        <span style={{ font: `700 11px ${MONO}`, color: 'var(--w-t400)', flex: 'none' }}>{String(n + 1).padStart(2, '0')}</span>
                        <input value={idea.title} onChange={e => setIdea(n, { title: e.target.value })}
                          placeholder="A title: what a person can do at the end" aria-label={`Idea ${n + 1} title`} className={FIELD} />
                        {ideas.length > 1 && (
                          <GhostButton onClick={() => setIdeas(all => all.filter((_, i) => i !== n))} title="Take this idea out">Remove</GhostButton>
                        )}
                      </div>
                      <textarea value={idea.paragraph} onChange={e => setIdea(n, { paragraph: e.target.value })} rows={2}
                        placeholder="One paragraph: who, what they can do at the end, what it builds on, what it is not."
                        aria-label={`Idea ${n + 1} paragraph`} className={`${FIELD} resize-y`} />
                    </div>
                  ))}
                </div>

                <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 10 }}>
                  <GhostButton onClick={() => setIdeas(all => [...all, { title: '', paragraph: '' }])}>Another idea</GhostButton>
                  <GhostButton tone="go" disabled={busy || !ready} onClick={start}>
                    {busy ? 'Starting' : 'Start the plan'}
                  </GhostButton>
                  {theFolder && (
                    <span className="min-w-0 truncate" style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>
                      {theFolder}/charter.md, {theFolder}/slices/
                    </span>
                  )}
                </div>
                {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 8 }}>{failed}</div>}
              </>
            )}
        </Panel>
      </Fold>
    </div>
  );
}
