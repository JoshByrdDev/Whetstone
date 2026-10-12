import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { Chip, GhostButton, PageHead, Panel, SectionRule } from './ui';
import { BODY, CAPS, DISPLAY, MONO, TIER, chamfer, fade } from './orbit';
import { track } from '../theme';
import { ActivityFeed } from './ActivityFeed';
import { isStewardEvent } from '../types/events';
import type { DashboardState, HeartbeatEvent } from '../types/events';
import {
  answerStewardQuestion, getSteward, getStewardEntries, getStewardStatus, setStewardMode, tellSteward,
} from '../api/steward';
import type {
  StewardEntry, StewardMode, StewardOutcomeRow, StewardPage, StewardQuestion, StewardStatus,
} from '../api/steward';

/**
 * A project's steward, as its owner sees it.
 *
 * The steward runs unattended and brings a person only what is theirs to decide, so this page is
 * built around that: what is waiting on you comes first and is the only thing here you have to act
 * on. Everything under it is the record of what it did in your place - there to be read back, and
 * reversed where you disagree.
 */

const MODES: { id: StewardMode; label: string; says: string }[] = [
  { id: 'off', label: 'Off', says: 'No steward. Nothing runs.' },
  { id: 'propose', label: 'Propose', says: 'It runs and decides, and only says what it would do.' },
  { id: 'manage', label: 'Manage', says: 'It files, answers and sends back. Every merge stays yours.' },
  { id: 'full', label: 'Full', says: 'It also merges finished tickets into the base branch.' },
];

const STATE_TIER: Record<StewardOutcomeRow['state'], keyof typeof TIER> = {
  NotStarted: 'backlog', InProgress: 'active', Done: 'done',
};

/** The small tracked caps this app labels things with. */
const label = (colour = 'var(--w-t500)'): CSSProperties => ({
  font: `600 9px ${DISPLAY}`, letterSpacing: track(0.3), color: colour, textTransform: CAPS,
});

function ago(iso?: string | null): string {
  if (!iso) return 'never';
  const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  if (mins < 60 * 24) return `${Math.round(mins / 60)}h ago`;
  return `${Math.round(mins / (60 * 24))}d ago`;
}

export function StewardPanel({ events, state }: { events: HeartbeatEvent[]; state: DashboardState }) {
  const [projects, project, setProject] = useProjectList('steward');
  const [page, setPage] = useState<StewardPage | null>(null);
  const [status, setStatus] = useState<StewardStatus | null>(null);
  const [entries, setEntries] = useState<StewardEntry[]>([]);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!project) return;
    try {
      const [p, s, e] = await Promise.all([getSteward(project), getStewardStatus(project), getStewardEntries(project)]);
      setPage(p); setStatus(s); setEntries(e); setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  }, [project]);

  // A steward's run changes this page from the outside, so it is re-read on a slow tick. Slow
  // because nothing here is urgent to the second: a question waits for a person either way.
  useEffect(() => {
    setPage(null); setStatus(null); setEntries([]);
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

          <Fold id="tell" open title="Tell your steward" colour="var(--w-accent)" meta="a standing instruction, read by every run">
            <Tell onSend={async text => { await tellSteward(page.project, text); await load(); }} />
          </Fold>

          <PlanSection status={status} />

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

          <EntryList title="Calls it made for you" colour={TIER.gate.c} kind="Call" entries={entries}
            empty="No calls recorded yet." />
          <EntryList title="To read before a release" colour={TIER.active.c} kind="BeforeRelease" entries={entries}
            empty="Nothing noted for release yet." />
          <EntryList title="What you told it" colour="var(--w-accent)" kind="OwnerSaid" entries={entries}
            empty="No standing instructions." />

          <Fold id="answered" title="Answered" colour={TIER.done.c} count={page.answered.length}>
          {page.answered.length === 0
            ? <Quiet>No questions answered yet.</Quiet>
            : (
              <div className="flex flex-col gap-2 mb-8">
                {page.answered.map(q => (
                  <Panel key={q.key}>
                    <div style={{ font: `600 12.5px ${BODY}`, color: 'var(--w-t200)' }}>{q.question}</div>
                    <div style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t400)', marginTop: 4 }}>{q.answer}</div>
                    <div style={{ ...label(), marginTop: 6 }}>{q.key} - answered {ago(q.answeredAt)}</div>
                  </Panel>
                ))}
              </div>
            )}
          </Fold>
        </>
      )}
    </div>
  );
}

/** Which sections a person last left open or shut, kept per machine so the page opens as they left it. */
const FOLD_PREFIX = 'whetstone.steward.fold.';

function rememberedFold(id: string): boolean | null {
  try {
    const v = localStorage.getItem(FOLD_PREFIX + id);
    return v === null ? null : v === '1';
  } catch {
    return null;   // private mode, blocked storage: the section just opens on its default
  }
}

/**
 * One section of the page: its heading, and a body that folds away under it.
 *
 * The heading is the control, whole, because it is the only thing left to aim at when the section
 * is shut. `open` is where the section starts for someone who has never touched it: what needs a
 * person, and what says how things stand, start open; the record, which is there to be looked
 * things up in, starts shut and still shows its count.
 */
function Fold({ id, open: startsOpen = false, title, colour, count, meta, right, children }: {
  id: string; open?: boolean; title: string; colour?: string; count?: number; meta?: string;
  right?: ReactNode; children: ReactNode;
}) {
  const [open, setOpen] = useState(() => rememberedFold(id) ?? startsOpen);
  const toggle = () => {
    const next = !open;
    setOpen(next);
    try { localStorage.setItem(FOLD_PREFIX + id, next ? '1' : '0'); } catch { /* still stands for this visit */ }
  };
  return (
    <>
      <button
        onClick={toggle}
        aria-expanded={open}
        title={open ? 'Hide this section' : 'Show this section'}
        className="orbit-tap block w-full text-left"
      >
        <SectionRule
          title={title} colour={colour} count={count} meta={meta}
          right={(
            <>
              {right}
              <span style={{ font: `700 10px ${MONO}`, color: 'var(--w-t500)', width: 10, textAlign: 'center' }}>
                {open ? '-' : '+'}
              </span>
            </>
          )}
        />
      </button>
      {/* Keyed on the state so the body arrives each time it is opened, not only the first. */}
      {open
        ? <div key="open" className="orbit-swap">{children}</div>
        : <div style={{ height: 12 }} />}
    </>
  );
}

function Quiet({ children }: { children: ReactNode }) {
  return <div className="mb-8" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)', padding: '4px 0 0 15px' }}>{children}</div>;
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

function PlanSection({ status }: { status: StewardStatus | null }) {
  if (!status) return null;
  const plan = status.plan;
  const p = status.progress;
  return (
    <Fold
      id="plan" open title="The plan" colour={TIER.active.c}
      meta={p ? `unit ${p.unit}: ${p.done} of ${p.outcomes} outcomes done, ${p.inProgress} in progress` : undefined}
      right={p ? <span style={{ font: `700 13px ${MONO}`, color: TIER.active.c }}>{p.percent}%</span> : undefined}
    >
      {typeof plan === 'string'
        ? <Quiet>{plan}</Quiet>
        : (
          <Panel pad={false} className="mb-8">
            <div className="flex flex-wrap gap-x-5 gap-y-1" style={{ padding: '10px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .1)' }}>
              {plan.map(u => (
                <span key={u.unit} style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t400)' }}>
                  <span style={{ font: `700 11px ${MONO}`, color: 'var(--w-t200)' }}>{String(u.unit).padStart(2, '0')}</span>{' '}
                  <span style={label(u.agreed ? TIER.done.c : u.draft ? TIER.gate.c : 'var(--w-t600)')}>
                    {u.agreed ? 'agreed' : u.draft ? 'draft - yours to agree' : 'not agreed'}
                  </span>{' '}
                  {u.outcomes} outcome{u.outcomes === 1 ? '' : 's'}
                </span>
              ))}
              <span style={{ font: `400 11.5px ${BODY}`, color: 'var(--w-t600)', marginLeft: 'auto' }}>
                filed today {status.limits.createdToday} - open {status.limits.open}
              </span>
            </div>
            {(status.outcomes ?? []).map(o => {
              const c = TIER[STATE_TIER[o.state]].c;
              return (
                <div
                  // On a phone the tickets and the note each take a line of their own under the
                  // outcome: side by side, a dozen ticket numbers are wider than the screen.
                  key={o.outcome} className="flex items-baseline gap-x-3 gap-y-1 max-sm:flex-wrap"
                  style={{ padding: '7px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)' }}
                >
                  <span style={{ font: `700 11.5px ${MONO}`, color: 'var(--w-t200)', width: 34, flex: 'none' }}>{o.outcome}</span>
                  <span style={{ ...label(c), width: 92, flex: 'none' }}>
                    {o.state === 'NotStarted' ? 'not started' : o.state === 'InProgress' ? 'in progress' : 'done'}
                  </span>
                  <span className="flex-none max-sm:flex-auto max-sm:basis-full max-sm:min-w-0" style={{ font: `400 11.5px ${MONO}`, color: 'var(--w-t400)' }}>{o.tickets.join('  ')}</span>
                  <span className="max-sm:basis-full max-sm:empty:hidden" style={{ font: `400 11.5px ${BODY}`, color: fade('var(--w-t400)', 80), minWidth: 0 }}>
                    {o.moreToFile && <span style={{ color: TIER.active.c }}>more to file. </span>}{o.note}
                  </span>
                </div>
              );
            })}
          </Panel>
        )}
    </Fold>
  );
}

function EntryList({ title, colour, kind, entries, empty }: {
  title: string; colour: string; kind: StewardEntry['kind']; entries: StewardEntry[]; empty: string;
}) {
  const rows = entries.filter(e => e.kind === kind);
  return (
    <Fold id={kind} title={title} colour={colour} count={rows.length}>
      {rows.length === 0
        ? <Quiet>{empty}</Quiet>
        : (
          <Panel pad={false} className="mb-8">
            {rows.map(e => (
              <div key={e.id} style={{ padding: '9px 16px', borderBottom: '1px solid rgb(var(--w-line-rgb) / .06)' }}>
                <div style={label()}>
                  {e.ticketId != null && <>#{e.ticketId} {e.ticketTitle} - </>}{ago(e.at)}
                </div>
                <div className="whitespace-pre-wrap" style={{ font: `400 12.5px/1.55 ${BODY}`, color: 'var(--w-t200)', marginTop: 3 }}>{e.text}</div>
              </div>
            ))}
          </Panel>
        )}
    </Fold>
  );
}
