import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { Chip, GhostButton, PageHead, Panel, SectionRule } from './ui';
import { BODY, DISPLAY, TIER, chamfer } from './orbit';
import { AgentMascot } from './AgentMascot';
import type { Mood } from '../theme';
import { ActivityFeed } from './ActivityFeed';
import { Markdown } from './Markdown';
import { StewardPlan } from './StewardPlan';
import { StewardRecord } from './StewardRecord';
import { StewardDecisions } from './StewardDecisions';
import { StewardTicketFlags } from './StewardTicketFlags';
import { DecisionSheet } from './DecisionSheet';
import { OptionRow, ago, label, remember, remembered } from './stewardParts';
import { isStewardEvent } from '../types/events';
import type { DashboardState, HeartbeatEvent } from '../types/events';
import {
  answerStewardQuestion, getSteward, getStewardEntries, getStewardPlan, getStewardRegister,
  getStewardStatus, keepStewardRuling, markStewardEntry, reverseStewardRuling, setStewardMode, unseenRuling,
} from '../api/steward';
import type {
  StewardEntry, StewardMode, StewardOwnerFlag, StewardPage, StewardPlanView, StewardQuestion, StewardRegister, StewardStatus,
} from '../api/steward';

/**
 * A project's steward, as its owner sees it.
 *
 * The steward runs unattended and brings a person only what is theirs to decide, so this page is
 * built around that: what is waiting on you comes first and is the only thing here you have to act
 * on. The plan it is working from, the decisions it works by, and the record of what it did in your
 * place are each a view of their own beside that one: different reasons to open this page, and none
 * of them is helped by scrolling past the others.
 */

/** The page's views, in the order of how often each is the reason somebody came. */
type View = 'now' | 'plan' | 'decisions' | 'record';

const MODES: { id: StewardMode; label: string; says: string }[] = [
  { id: 'off', label: 'Off', says: 'No steward. Nothing runs.' },
  { id: 'propose', label: 'Propose', says: 'It runs and decides, and only says what it would do.' },
  { id: 'manage', label: 'Manage', says: 'It files, answers and sends back. Every merge stays yours.' },
  { id: 'full', label: 'Full', says: 'It also merges finished tickets into the base branch.' },
];

export function StewardPanel({ events, state }: { events: HeartbeatEvent[]; state: DashboardState }) {
  const [projects, project, setProject] = useProjectList('steward');
  const [page, setPage] = useState<StewardPage | null>(null);
  const [status, setStatus] = useState<StewardStatus | null>(null);
  const [entries, setEntries] = useState<StewardEntry[]>([]);
  const [plan, setPlan] = useState<StewardPlanView | null>(null);
  const [register, setRegister] = useState<StewardRegister | null>(null);
  // Which of the project's plans is being read: null is the one being worked.
  const [planKey, setPlanKey] = useState<string | null>(null);
  // The unit whose notes the record is showing, when somebody came from that unit's release.
  const [notesUnit, setNotesUnit] = useState<number | null>(null);
  // Another project's plans and units are another project's.
  useEffect(() => { setPlanKey(null); setNotesUnit(null); }, [project]);
  const [view, setView] = useState<View>(() => {
    const v = remembered('view');
    return v === 'plan' || v === 'decisions' || v === 'record' ? v : 'now';
  });
  const show = (v: View) => { setView(v); remember('view', v); };
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!project) return;
    try {
      const [p, s, e, pl, r] = await Promise.all([
        getSteward(project), getStewardStatus(project), getStewardEntries(project),
        // The plan is read from the project's repo, and a repo that cannot be read must not be
        // what stops a person answering a question.
        getStewardPlan(project, planKey).catch(() => ({ why: 'The plan could not be read just now.' } as StewardPlanView)),
        // So is the register's list of units; the decisions themselves are the board's.
        getStewardRegister(project).catch(() => null),
      ]);
      setPage(p); setStatus(s); setEntries(e); setPlan(pl); setRegister(r); setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [project, planKey]);

  // A steward's run changes this page from the outside, so it is re-read on a slow tick. Slow
  // because nothing here is urgent to the second: a question waits for a person either way.
  useEffect(() => {
    setPage(null); setStatus(null); setEntries([]); setPlan(null); setRegister(null);
    load();
    const t = setInterval(load, 15000);
    return () => clearInterval(t);
  }, [load]);

  const chooseMode = async (mode: StewardMode) => {
    if (!project || !page || mode === page.modeSetting) return;
    try { await setStewardMode(project, mode); await load(); }
    catch (err) { setError(err instanceof Error ? err.message : String(err)); }
  };

  // What this project's steward is thinking, saying and doing, as it happens: its own strand of
  // the feed the Heartbeat page shows whole, from the same stream, so the two can never differ.
  const live = useMemo(
    () => events.filter(e => isStewardEvent(e) && e.project === project),
    [events, project],
  );
  // The fleet's state hears a run begin and end within the second; the page's own answer is only
  // as fresh as its last slow read.
  const runningNow = !!project && state.steward?.project === project;
  // A run that has just ended has changed what is below - its summary, its calls, its questions.
  const wasRunning = useRef(false);
  useEffect(() => {
    if (wasRunning.current && !runningNow) load();
    wasRunning.current = runningNow;
  }, [runningNow, load]);

  const off = page?.mode === 'off';
  const progress = status?.progress;
  // A ticket in review whose merge waits on a flag only its owner can clear. Answering the
  // question that asked about it lifts nothing, so the ticket stays here until the flag is
  // cleared or the ticket leaves review; one an open question names is already shown under it.
  const asked = new Set((page?.waiting ?? []).map(q => q.ticketId).filter((id): id is number => id != null));
  const inReview = (status?.tickets ?? []).filter(t => t.phase === 'NeedsReview' && !asked.has(t.id));
  const [held, setHeld] = useState<Record<number, boolean>>({});
  const hear = useCallback((id: number, flags: StewardOwnerFlag[]) => {
    const on = flags.some(f => !f.cleared);
    setHeld(h => (h[id] === on ? h : { ...h, [id]: on }));
  }, []);
  const heldCount = inReview.filter(t => held[t.id]).length;
  // Reading a ticket's flags takes git a few seconds; until each has answered, "nothing is
  // waiting" is not yet known to be true.
  const hearing = inReview.some(t => held[t.id] === undefined);
  const waitingCount = (page?.waiting.length ?? 0) + heldCount;
  // What a release still asks of its owner: the notes that are something to do, not only to know.
  const outstanding = entries.filter(e => e.kind === 'BeforeRelease' && !e.handledAt && e.release !== 'Note').length;
  // Rulings the steward made that the owner has not yet seen: they apply already, so they count.
  const unseen = entries.filter(unseenRuling).length;
  const recordCount = outstanding + unseen;
  const inForce = (register?.decisions ?? []).filter(d => d.status === 'inForce' || d.status === 'provisional').length;
  const pairs = register?.mayConflict.length ?? 0;
  // Where the steward stands is the card's to say, on the Now tab; the head only says what the
  // page cannot show at all.
  const sub = !page ? 'Loading' : off ? 'No steward for this project' : undefined;

  return (
    <div className="max-w-7xl mx-auto px-3 sm:px-6 py-4">
      <PageHead
        eyebrow="Whetstone // stand-in"
        title="Steward"
        sub={sub}
        left={<div style={{ paddingBottom: 5 }}><ProjectPicker projects={projects} project={project} onChange={setProject} /></div>}
        right={page && (
          <div className="flex items-center gap-2">
            {MODES.map(m => (
              <Chip
                key={m.id}
                on={page.modeSetting === m.id}
                onClick={() => chooseMode(m.id)}
                disabled={m.id === 'full' && !!page.fullUnavailable}
                title={m.id === 'full' && page.fullUnavailable ? page.fullUnavailable : m.says}
              >
                {m.label}
              </Chip>
            ))}
          </div>
        )}
      />

      {error && (
        <Panel tone={TIER.blocked.c} className="mb-4">
          <span style={{ font: `400 12px ${BODY}`, color: 'var(--w-t200)' }}>{error}</span>
        </Panel>
      )}

      {page && !off && (
        <>
          <div className="flex flex-wrap gap-2" style={{ margin: '6px 0 18px' }}>
            <Chip on={view === 'now'} onClick={() => show('now')} title="What is waiting on you, and what it is doing">
              Now{waitingCount > 0 ? ` ${waitingCount}` : ''}
            </Chip>
            <Chip on={view === 'plan'} onClick={() => show('plan')} title="The plan it works from, and how far each part is">
              The plan{progress ? ` ${progress.percent}%` : ''}
            </Chip>
            <Chip on={view === 'decisions'} onClick={() => show('decisions')}
              title={`The register: what is decided and whether it still is${pairs > 0 ? ` - ${pairs} pair${pairs === 1 ? '' : 's'} that may disagree` : ''}`}>
              Decisions {pairs > 0 ? pairs : inForce > 0 ? inForce : ''}
            </Chip>
            <Chip on={view === 'record'} onClick={() => show('record')}
              title={`What it did in your place, and what to read before a release${recordCount > 0
                ? ` - ${[unseen > 0 ? `${unseen} new ruling${unseen === 1 ? '' : 's'}` : '', outstanding > 0 ? `${outstanding} to deal with before a release` : ''].filter(Boolean).join(', ')}`
                : ''}`}>
              The record{recordCount > 0 ? ` ${recordCount}` : ''}
            </Chip>
          </div>

          {view === 'now' && (
            <div key="now" className="orbit-swap">
              {/* The steward itself on the left, what it needs from you on the right. One column
                  on a phone, the steward first: it says why the right side is what it is. */}
              <div className="grid grid-cols-1 sm:grid-cols-[minmax(0,5fr)_minmax(0,7fr)] gap-6 items-start" style={{ marginBottom: 32 }}>
                <StewardCard page={page} running={runningNow || page.running} fleet={state.status} live={live} />

                <div className="min-w-0">
                  <SectionRule title="Waiting on you" colour={TIER.blocked.c} count={waitingCount} />
                  {/* Mounted whatever the count, since reading their flags is what makes the count. */}
                  <div className="flex flex-col gap-3">
                    {page.waiting.map(q => (
                      <QuestionCard key={q.key} project={page.project} q={q} onSettled={load}
                        onAnswer={async (a, replaces, story) => { await answerStewardQuestion(page.project, q.key, a, replaces, story); await load(); }} />
                    ))}
                    {inReview.map(t => (
                      <StewardTicketFlags key={t.id} framed project={page.project} ticketId={t.id} title={t.title} onFlags={f => hear(t.id, f)} />
                    ))}
                  </div>
                  {waitingCount === 0 && <NothingWaiting checking={hearing} />}
                </div>
              </div>

            </div>
          )}

          {view === 'plan' && (
            <div key="plan" className="orbit-swap">
              <StewardPlan
                project={page.project} view={plan} status={status} onChanged={load}
                onPlan={key => { setPlan(null); setPlanKey(key); }}
                onNotes={unit => { setNotesUnit(unit); show('record'); }}
              />
            </div>
          )}

          {view === 'decisions' && (
            <div key="decisions" className="orbit-swap">
              <StewardDecisions project={page.project} register={register} onChanged={load} />
            </div>
          )}

          {view === 'record' && (
            <div key="record" className="orbit-swap">
              <StewardRecord
                entries={entries} answered={page.answered} unit={notesUnit} onUnit={setNotesUnit}
                onMark={async (id, mark) => { await markStewardEntry(page.project, id, mark); await load(); }}
                onKeep={async id => { await keepStewardRuling(page.project, id); await load(); }}
                onReverse={async (id, because) => { await reverseStewardRuling(page.project, id, because); await load(); }}
              />
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * One question, answered by picking. The steward's own pick is marked and pre-selected, because
 * the commonest answer is "yes, that one" and it should cost a single press.
 */
function QuestionCard({ project, q, onAnswer, onSettled }: {
  project: string; q: StewardQuestion; onAnswer: (answer: string, replaces?: number[], story?: string) => Promise<void>;
  /** After it was answered on a unit's own decisions rather than through `onAnswer`. */
  onSettled: () => Promise<void>;
}) {
  // A question about a unit's open decisions is answered on those decisions, as the spec writes
  // them, and the answers go into the spec - not into a reply that points at it.
  if (q.unit != null && q.decisions && q.decisions.length > 0) {
    return (
      <Panel tone={TIER.blocked.c}>
        <div style={{ font: `600 14px/1.4 ${BODY}`, color: 'var(--w-t100)' }}>{q.question}</div>
        <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t400)', marginTop: 5 }}>{q.why}</div>
        <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)', margin: '8px 0 12px' }}>
          Held up meanwhile: <span style={{ color: 'var(--w-t300)' }}>{q.heldUp}</span>
          <span style={{ color: 'var(--w-t600)' }}> - asked {ago(q.askedAt)}</span>
        </div>
        <DecisionSheet project={project} unit={q.unit} only={q.decisions} mode="answer" answering={q.key} onDone={onSettled} />
      </Panel>
    );
  }
  return <OptionsCard project={project} q={q} onAnswer={onAnswer} />;
}

function OptionsCard({ project, q, onAnswer }: {
  project: string; q: StewardQuestion; onAnswer: (answer: string, replaces?: number[], story?: string) => Promise<void>;
}) {
  const recommended = q.options.findIndex(o => o.recommended);
  const [pick, setPick] = useState<number | 'other'>(recommended >= 0 ? recommended : 'other');
  // Which touched decisions the answer replaces: what the picked option says it changes, until
  // the owner says otherwise. Shown, never assumed - the override is the owner's to see.
  const changesOf = (p: number | 'other') => p === 'other' ? [] : (q.changes?.[q.options[p].label] ?? []);
  const [replaces, setReplaces] = useState<number[]>(() => changesOf(recommended >= 0 ? recommended : 'other'));
  // The sentence of the slice's story this answer rewrites, as the picked option words it, and yours to edit.
  const rewordingOf = (p: number | 'other') => p === 'other' ? '' : (q.story?.rewordings?.[q.options[p].label] ?? '');
  const [story, setStory] = useState(() => rewordingOf(recommended >= 0 ? recommended : 'other'));
  const choose = (p: number | 'other') => { setPick(p); setReplaces(changesOf(p)); setStory(rewordingOf(p)); };
  const touches = q.touches ?? [];
  const [other, setOther] = useState('');
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  // The answer is what a later run reads as a decision made, so it has to stand on its own: the
  // option in words, not a letter, plus anything added.
  const compose = () => {
    const added = note.trim() ? ` ${note.trim()}` : '';
    if (pick === 'other') return other.trim();
    const o = q.options[pick];
    return `${o.label}${o.detail ? ` (${o.detail.replace(/\.$/, '')})` : ''}.${added}`;
  };
  const answer = compose();

  const send = async () => {
    setBusy(true); setFailed(null);
    try { await onAnswer(answer, touches.length > 0 ? replaces : undefined, q.story && story.trim() ? story.trim() : undefined); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); setBusy(false); }
  };

  const c = TIER.blocked.c;
  return (
    <Panel tone={c}>
      <div style={{ font: `600 14px/1.4 ${BODY}`, color: 'var(--w-t100)' }}>{q.question}</div>
      <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t400)', marginTop: 5 }}>{q.why}</div>

      {q.ticketId != null && <StewardTicketFlags project={project} ticketId={q.ticketId} />}

      {q.idea && (
        <div style={{ marginTop: 12 }}>
          <div style={label()}>The idea, as it would be committed - {q.idea.file}</div>
          <div style={{
            marginTop: 6, padding: '10px 14px', clipPath: chamfer(8),
            background: 'rgb(var(--w-line-rgb) / .04)', border: '1px solid rgb(var(--w-line-rgb) / .14)',
          }}><Markdown text={q.idea.text} /></div>
        </div>
      )}

      <div className="flex flex-col gap-1.5" style={{ marginTop: 12 }}>
        {q.options.map((o, i) => (
          <OptionRow key={i} on={pick === i} onClick={() => choose(i)} title={o.label} detail={o.detail}
            tag={o.recommended ? "the steward's pick" : undefined} />
        ))}
        <OptionRow on={pick === 'other'} onClick={() => choose('other')} title="Something else" />
      </div>

      {q.story && (
        <div style={{ marginTop: 12 }}>
          <div style={label()}>The slice's story now says</div>
          <div style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 4 }}>{q.story.sentence}</div>
          <div style={{ ...label(), marginTop: 8 }}>With your answer it will say</div>
          <textarea value={story} onChange={e => setStory(e.target.value)} rows={2}
            placeholder="Leave empty to keep the sentence as it is"
            aria-label="The story sentence, reworded"
            className="mt-1 w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none" />
        </div>
      )}

      {touches.length > 0 && (
        <div style={{ marginTop: 12 }}>
          <div style={label()}>Decided already, and touched by this</div>
          <div className="flex flex-col gap-1.5" style={{ marginTop: 6 }}>
            {touches.map(t => {
              const on = replaces.includes(t.number);
              return (
                <button key={t.number} type="button" role="checkbox" aria-checked={on}
                  onClick={() => setReplaces(r => on ? r.filter(n => n !== t.number) : [...r, t.number])}
                  className="orbit-tap text-left"
                  style={{
                    clipPath: chamfer(8), padding: '7px 11px',
                    background: on ? 'rgb(var(--w-blocked-rgb) / .1)' : 'rgb(var(--w-line-rgb) / .04)',
                    border: `1px solid ${on ? 'rgb(var(--w-blocked-rgb) / .45)' : 'rgb(var(--w-line-rgb) / .14)'}`,
                  }}>
                  <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>
                    <span style={{ fontWeight: 600 }}>#{t.number}</span> {t.rule ?? ''}
                  </div>
                  <div style={{ ...label(on ? TIER.blocked.c : undefined), marginTop: 3 }}>
                    {on ? `Your answer replaces #${t.number}` : `#${t.number} stands beside your answer`}
                  </div>
                </button>
              );
            })}
          </div>
        </div>
      )}

      {pick === 'other'
        ? (
          <textarea
            autoFocus value={other} onChange={e => setOther(e.target.value)} rows={4}
            placeholder="Your answer, in your own words. The steward reads it as a decision made."
            className="mt-2 w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
          />
        )
        : (
          <textarea
            value={note} onChange={e => setNote(e.target.value)} rows={2}
            placeholder="Anything to add (optional)"
            className="mt-2 w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
          />
        )}

      <div className="flex items-center justify-between gap-4 flex-wrap" style={{ marginTop: 12 }}>
        <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)' }}>
          Held up meanwhile: <span style={{ color: 'var(--w-t300)' }}>{q.heldUp}</span>
          <span style={{ color: 'var(--w-t600)' }}> - asked {ago(q.askedAt)}</span>
        </div>
        <GhostButton tone="go" onClick={send} disabled={busy || !answer}>{busy ? 'Recording' : 'Answer'}</GhostButton>
      </div>
      {failed && <div style={{ font: `400 11.5px ${BODY}`, color: c, marginTop: 6 }}>{failed}</div>}
    </Panel>
  );
}

/**
 * The steward as a presence: the theme's mascot in the mood of what it is doing, where it stands
 * in one honest sentence, what its last run did, and under that its own strand of the live feed.
 * The mascot animates itself by mood, so a working steward bobs, one with a run due stands by,
 * and one with nothing to find sleeps.
 */
function StewardCard({ page, running, fleet, live }: {
  page: StewardPage; running: boolean; fleet: DashboardState['status']; live: HeartbeatEvent[];
}) {
  // Only the heartbeat starts a run, so a run that is due is due "shortly" only while it is going.
  const held = page.due && !running && (fleet === 'stopped' || fleet === 'off-hours');
  const mood: Mood = running ? 'running' : held ? 'stopped' : page.due ? 'idle' : 'sleeping';
  const word = running ? 'Working' : page.due ? 'A run is due' : page.resting ? 'Resting' : 'Standing by';
  const when = running
    ? page.lastRunStartedAt ? `started ${ago(page.lastRunStartedAt)}` : null
    : page.lastRunEndedAt ? `last run ${ago(page.lastRunEndedAt)}` : null;
  const line = running ? 'A run is in progress. What it thinks, says and does shows below as it happens.'
    : held ? fleet === 'off-hours'
      ? 'Waiting for working hours: the heartbeat starts no run until then.'
      : 'Waiting for the heartbeat. Nothing starts a run while it is stopped.'
    : page.due ? "Starts on the heartbeat's next pass."
    : page.resting ? 'Nothing to do until a ticket comes back, you answer something, or the base branch moves.'
    : 'Waiting for something to change.';
  const heard = live.length > 0 ? `last heard ${ago(live[live.length - 1].timestamp)}` : null;

  return (
    <Panel tone={running ? TIER.active.c : undefined} className="min-w-0">
      <div className="flex items-center gap-4" style={{ marginBottom: 14 }}>
        <div style={{ padding: 6 }}><AgentMascot mood={mood} size={64} /></div>
        <div className="min-w-0">
          <div style={{ font: `700 17px ${DISPLAY}`, color: 'var(--w-t100)', letterSpacing: '.02em' }}>Steward</div>
          {/* Why it is due, on hover: the list repeats what the page already shows, so it is not spelled out. */}
          <div style={{ ...label(running ? TIER.active.c : 'var(--w-t400)'), marginTop: 3 }}
            title={page.due && page.dueReason ? `Due because ${page.dueReason}` : undefined}>
            {word}{when ? ` - ${when}` : ''}
          </div>
          <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 5 }}>{line}</div>
        </div>
      </div>

      <LastRun summary={page.lastRunSummary} />

      <div className="flex items-center justify-between" style={{ ...label(), margin: '16px 0 6px' }}>
        <span>Live</span>
        {heard && <span style={{ textTransform: 'none', letterSpacing: 0, fontWeight: 400 }}>{heard}</span>}
      </div>
      <ActivityFeed
        events={live} solo
        empty="Nothing from this steward in the recent feed. When a run starts, it shows here as it happens."
      />
    </Panel>
  );
}

/** What the last run did, held to a few lines until asked for. */
function LastRun({ summary }: { summary?: string | null }) {
  const [whole, setWhole] = useState(false);
  const [long, setLong] = useState(false);
  const text = useRef<HTMLDivElement>(null);
  // Only offer "more" when the clamp is actually hiding something.
  useLayoutEffect(() => {
    const el = text.current;
    if (el && !whole) setLong(el.scrollHeight > el.clientHeight + 1);
  }, [summary, whole]);

  return (
    <div>
      <div style={{ ...label(), marginBottom: 6 }}>Last run</div>
      <div ref={text} className={whole ? '' : 'line-clamp-5'}
        style={{ font: `400 12.5px/1.55 ${BODY}`, color: summary ? 'var(--w-t200)' : 'var(--w-t500)' }}>
        {summary ?? 'No run has finished yet.'}
      </div>
      {(long || whole) && (
        <button type="button" onClick={() => setWhole(w => !w)} className="orbit-tap"
          style={{ ...label('var(--w-t300)'), marginTop: 4 }}>
          {whole ? 'less' : 'more'}
        </button>
      )}
    </div>
  );
}

/** The right column with nothing in it: the space stays, and says why it is empty. */
function NothingWaiting({ checking }: { checking: boolean }) {
  return (
    <div
      className="flex flex-col items-center justify-center text-center"
      style={{
        minHeight: 260, padding: '32px 24px', clipPath: chamfer(13),
        border: '1px dashed rgb(var(--w-line-rgb) / .22)', background: 'rgb(var(--w-line-rgb) / .04)',
      }}
    >
      <div style={{ font: `600 14px ${BODY}`, color: 'var(--w-t200)' }}>
        {checking ? 'Checking finished tickets' : 'Nothing is waiting on you'}
      </div>
      <div style={{ font: `400 12px/1.55 ${BODY}`, color: 'var(--w-t500)', marginTop: 6, maxWidth: 340 }}>
        {checking
          ? 'Reading the flags on tickets in review. A flag only you can clear will show here.'
          : 'When the steward needs a decision, or a finished ticket has a flag only you can clear, it shows up here.'}
      </div>
    </div>
  );
}
