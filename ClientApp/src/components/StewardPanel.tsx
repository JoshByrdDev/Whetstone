import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { Chip, GhostButton, PageHead, Panel } from './ui';
import { BODY, MONO, TIER, chamfer } from './orbit';
import { ActivityFeed } from './ActivityFeed';
import { StewardPlan } from './StewardPlan';
import { StewardRecord } from './StewardRecord';
import { Fold, Quiet, ago, label, remember, remembered } from './stewardParts';
import { isStewardEvent } from '../types/events';
import type { DashboardState, HeartbeatEvent } from '../types/events';
import {
  answerStewardQuestion, getSteward, getStewardEntries, getStewardPlan, getStewardStatus, markStewardEntry,
  setStewardMode, tellSteward,
} from '../api/steward';
import type {
  StewardEntry, StewardMode, StewardPage, StewardPlanView, StewardQuestion, StewardStatus,
} from '../api/steward';

/**
 * A project's steward, as its owner sees it.
 *
 * The steward runs unattended and brings a person only what is theirs to decide, so this page is
 * built around that: what is waiting on you comes first and is the only thing here you have to act
 * on. The plan it is working from, and the record of what it did in your place, are each a view of
 * their own beside that one: three different reasons to open this page, and none of them is helped
 * by scrolling past the other two.
 */

/** The page's three views, in the order of how often each is the reason somebody came. */
type View = 'now' | 'plan' | 'record';

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
  const [view, setView] = useState<View>(() => {
    const v = remembered('view');
    return v === 'plan' || v === 'record' ? v : 'now';
  });
  const show = (v: View) => { setView(v); remember('view', v); };
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!project) return;
    try {
      const [p, s, e, pl] = await Promise.all([
        getSteward(project), getStewardStatus(project), getStewardEntries(project),
        // The plan is read from the project's repo, and a repo that cannot be read must not be
        // what stops a person answering a question.
        getStewardPlan(project).catch(() => ({ why: 'The plan could not be read just now.' } as StewardPlanView)),
      ]);
      setPage(p); setStatus(s); setEntries(e); setPlan(pl); setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [project]);

  // A steward's run changes this page from the outside, so it is re-read on a slow tick. Slow
  // because nothing here is urgent to the second: a question waits for a person either way.
  useEffect(() => {
    setPage(null); setStatus(null); setEntries([]); setPlan(null);
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
  // What a release still asks of its owner: the notes that are something to do, not only to know.
  const outstanding = entries.filter(e => e.kind === 'BeforeRelease' && !e.handledAt && e.release !== 'Note').length;
  const sub = !page ? 'Loading' : off ? 'No steward for this project'
    : runningNow || page.running ? 'A run is in progress'
    : `Last run ${ago(page.lastRunEndedAt)}${page.due ? ` - a run is due: ${page.dueReason}` : page.resting ? ' - resting until something changes' : ''}`;

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

      {page && (
        <div style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t500)', margin: '2px 0 18px' }}>
          {MODES.find(m => m.id === page.mode)?.says}
          {page.mode !== page.modeSetting && page.fullUnavailable && <> {page.fullUnavailable}</>}
        </div>
      )}

      {page && !off && (
        <>
          <div className="flex flex-wrap gap-2" style={{ marginBottom: 18 }}>
            <Chip on={view === 'now'} onClick={() => show('now')} title="What is waiting on you, and what it is doing">
              Now{page.waiting.length > 0 ? ` ${page.waiting.length}` : ''}
            </Chip>
            <Chip on={view === 'plan'} onClick={() => show('plan')} title="The plan it works from, and how far each part is">
              The plan{progress ? ` ${progress.percent}%` : ''}
            </Chip>
            <Chip on={view === 'record'} onClick={() => show('record')} title="What it did in your place, and what to read before a release">
              The record{outstanding > 0 ? ` ${outstanding}` : ''}
            </Chip>
          </div>

          {view === 'now' && (
            <div key="now" className="orbit-swap">
              <Fold id="waiting" open title="Waiting on you" colour={TIER.blocked.c} count={page.waiting.length}>
                {page.waiting.length === 0
                  ? <Quiet>Nothing is waiting on you.</Quiet>
                  : (
                    <div className="flex flex-col gap-3 mb-8">
                      {page.waiting.map(q => (
                        <QuestionCard key={q.key} q={q} onAnswer={async a => { await answerStewardQuestion(page.project, q.key, a); await load(); }} />
                      ))}
                    </div>
                  )}
              </Fold>

              <Fold
                id="live" open title="Live" colour={TIER.queue.c}
                meta={runningNow ? 'a run is in progress'
                  : live.length > 0 ? `last heard ${ago(live[live.length - 1].timestamp)}` : 'no run going'}
              >
                <div className="mb-8">
                  <ActivityFeed
                    events={live} solo
                    empty="Nothing from this steward in the recent feed. When a run starts, what it thinks, says and does shows here as it happens."
                  />
                </div>
              </Fold>

              <Fold id="last-run" open title="Last run" colour={TIER.queue.c} meta={page.lastRunEndedAt ? ago(page.lastRunEndedAt) : undefined}>
                <Panel className="mb-8">
                  <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t200)' }}>
                    {page.lastRunSummary ?? 'No run has finished yet.'}
                  </div>
                  {page.memo && (
                    <details className="mt-3">
                      <summary className="orbit-tap cursor-pointer" style={{ ...label(), display: 'inline-block' }}>
                        Its memo to its next run
                      </summary>
                      <pre
                        className="mt-2 whitespace-pre-wrap"
                        style={{ font: `400 11.5px/1.55 ${MONO}`, color: 'var(--w-t400)' }}
                      >{page.memo}</pre>
                    </details>
                  )}
                </Panel>
              </Fold>

              <Fold id="tell" open title="Tell your steward" colour="var(--w-accent)" meta="a standing instruction, read by every run">
                <Tell onSend={async text => { await tellSteward(page.project, text); await load(); }} />
              </Fold>
            </div>
          )}

          {view === 'plan' && (
            <div key="plan" className="orbit-swap"><StewardPlan view={plan} status={status} /></div>
          )}

          {view === 'record' && (
            <div key="record" className="orbit-swap">
              <StewardRecord
                entries={entries} answered={page.answered}
                onMark={async (id, mark) => { await markStewardEntry(page.project, id, mark); await load(); }}
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
function QuestionCard({ q, onAnswer }: { q: StewardQuestion; onAnswer: (answer: string) => Promise<void> }) {
  const recommended = q.options.findIndex(o => o.recommended);
  const [pick, setPick] = useState<number | 'other'>(recommended >= 0 ? recommended : 'other');
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
    try { await onAnswer(answer); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); setBusy(false); }
  };

  const c = TIER.blocked.c;
  return (
    <Panel tone={c}>
      <div style={{ font: `600 14px/1.4 ${BODY}`, color: 'var(--w-t100)' }}>{q.question}</div>
      <div style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t400)', marginTop: 5 }}>{q.why}</div>

      <div className="flex flex-col gap-1.5" style={{ marginTop: 12 }}>
        {q.options.map((o, i) => (
          <OptionRow key={i} on={pick === i} onClick={() => setPick(i)} title={o.label} detail={o.detail}
            tag={o.recommended ? "the steward's pick" : undefined} />
        ))}
        <OptionRow on={pick === 'other'} onClick={() => setPick('other')} title="Something else" />
      </div>

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

function OptionRow({ on, onClick, title, detail, tag }: {
  on: boolean; onClick: () => void; title: string; detail?: string | null; tag?: string;
}) {
  return (
    <button
      onClick={onClick}
      aria-current={on ? 'true' : undefined}
      className="orbit-pick text-left"
      style={{
        clipPath: chamfer(8),
        padding: '8px 12px',
        background: on ? 'rgb(var(--w-accent-rgb) / .13)' : 'rgb(var(--w-line-rgb) / .04)',
        border: `1px solid ${on ? 'rgb(var(--w-accent-rgb) / .5)' : 'rgb(var(--w-line-rgb) / .14)'}`,
      }}
    >
      <div className="flex items-baseline gap-2 flex-wrap">
        <span style={{ font: `600 12.5px ${BODY}`, color: on ? 'var(--w-accent-text-lit)' : 'var(--w-t200)' }}>{title}</span>
        {tag && <span style={label('var(--w-accent-text)')}>{tag}</span>}
      </div>
      {detail && <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t500)', marginTop: 2 }}>{detail}</div>}
    </button>
  );
}

function Tell({ onSend }: { onSend: (text: string) => Promise<void> }) {
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const send = async () => {
    setBusy(true);
    try { await onSend(text.trim()); setText(''); } finally { setBusy(false); }
  };
  return (
    <div className="mb-8">
      <textarea
        value={text} onChange={e => setText(e.target.value)} rows={2}
        placeholder="Something it should hold to from now on. It reads this as a decision you have made."
        className="w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
      />
      <div className="flex justify-end" style={{ marginTop: 6 }}>
        <GhostButton onClick={send} disabled={busy || !text.trim()}>{busy ? 'Recording' : 'Leave instruction'}</GhostButton>
      </div>
    </div>
  );
}
