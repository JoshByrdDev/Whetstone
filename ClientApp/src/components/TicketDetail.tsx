import { useEffect, useLayoutEffect, useRef, useState, useCallback } from 'react';
import type { ReactNode } from 'react';
import type { Phase, PhaseInfo, TicketDetail as TDetail, QaStatusName, BlockPayload, BlockQuestion, JourneyLeg, NoteAttachment, Priority, TrackerKind } from '../types/tickets';
import { PHASE_LABEL, DEFAULT_PRIORITY } from '../types/tickets';
import type { AgentState } from '../types/events';
import * as api from '../api/tickets';
import { ago } from './JourneyTrack';
import { TicketOptions } from './TicketOptions';
import { IndependentReview } from './IndependentReview';
import { PriorityPicker } from './PriorityPicker';
import { LabelEditor } from './LabelEditor';
import { LearningBrief, Markdown } from './LearningBrief';
import { StewardOnTicket } from './StewardOnTicket';
import { ReviewPanel } from './ReviewPanel';
import { JourneyChart } from './JourneyChart';
import { AgentMascot, agentAccent } from './AgentMascot';
import { useShotLightbox } from './ShotLightbox';
import { usePop } from '../hooks/usePop';

const PROGRESS_LIMIT = 4;

// How often to re-pull a ticket that an agent is actively working. Tighter than the board's 8s: the
// drawer is where someone is watching a single ticket tick over item by item, and a checklist that
// updates a beat after the agent moves is the whole point of the marker.
const LIVE_POLL_MS = 3000;

/**
 * Is the agent on this ticket doing the kind of work this list represents?
 *
 * "An agent is here" is not enough on its own. A `StartedAt` outlives the run that set it, so an
 * implement run killed mid-item leaves one behind for good; when the ticket later reaches QA and an
 * agent claims it, the work-items list would draw a bot on that dead item alongside the real one in
 * the QA list — two bots, one of them lying about what is happening right now.
 *
 * An agent with no reported phase still counts. Losing the marker to a missing field would trade a
 * rare wrong bot for a common absent one.
 */
function agentIsIn(agent: AgentState | undefined, phase: Phase): boolean {
  return !!agent && (!agent.phase || agent.phase === phase);
}

const QA_STATUSES: QaStatusName[] = ['Todo', 'Passed', 'Failed', 'Blocked'];
const QA_COLOR: Record<QaStatusName, string> = {
  Todo: 'text-gray-300',
  Passed: 'text-emerald-300',
  Failed: 'text-red-300',
  Blocked: 'text-amber-300',
};

function agoLabel(iso: string): string {
  const a = ago(iso);
  return a === 'just now' ? a : `${a} ago`;
}

/**
 * Inline pull-down drawer with a ticket's full detail — expands in place on the
 * board under the clicked card/row. Deliberately starts at Description: the
 * card/row it stems from already shows phase, title, track and actions.
 */
export function TicketDetail({
  id, agent, onClose, onChanged, flush = false,
}: {
  id: number;
  phases: PhaseInfo[];
  /** The heartbeat agent working this ticket right now, if any. Undefined when nobody is on it. */
  agent?: AgentState;
  onClose: () => void;
  onChanged: () => void;
  /** Rendered inside the board's inspect slide-over, which supplies its own shell and scroll. */
  flush?: boolean;
}) {
  const [t, setT] = useState<TDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [moveOpen, setMoveOpen] = useState(false);
  // Opens upward, off the foot of the drawer, so it arrives from below.
  const move = usePop(moveOpen);
  const [showAllProgress, setShowAllProgress] = useState(false);
  const [journey, setJourney] = useState<JourneyLeg[]>([]);

  const reload = useCallback(async () => {
    try { setT(await api.getTicket(id)); }
    catch (e) { setError(e instanceof Error ? e.message : 'Failed to load ticket'); }
  }, [id]);

  useEffect(() => { reload(); }, [reload]);

  // The trip only gains a leg when the phase changes, so it is fetched on that rather than on the
  // live poll below — which runs every few seconds while an agent holds the ticket and would be
  // re-fetching an identical list each time. A failure just leaves the chart out.
  useEffect(() => {
    if (!t) return;
    let live = true;
    api.getTicketJourney(id).then(j => { if (live) setJourney(j); }).catch(() => {});
    return () => { live = false; };
  }, [id, t?.phase]);

  // Keep the drawer live while an agent is working this ticket. Without this it renders whatever
  // the ticket looked like the moment it was opened: the agent ticks items off one by one and the
  // watcher sees none of it until they close the drawer and open it again, which is what made a
  // 17-item implement look like it went from 0/17 to 17/17 in one jump.
  // Only while someone is actually on it - an idle ticket has nothing to poll for.
  const busyRef = useRef(busy);
  busyRef.current = busy;
  useEffect(() => {
    if (!agent) return;
    // Skip the tick when one of our own mutations is in flight; that request reloads on its own,
    // and a poll landing on top of it would race its response back into the same state.
    const tick = () => { if (!busyRef.current) reload(); };
    const h = setInterval(tick, LIVE_POLL_MS);
    return () => clearInterval(h);
  }, [agent, reload]);

  // Wrap a mutation: run it, reload this ticket, and notify the board.
  const run = useCallback(async (fn: () => Promise<unknown>) => {
    setBusy(true); setError(null);
    try { await fn(); await reload(); onChanged(); }
    catch (e) { setError(e instanceof Error ? e.message : 'Action failed'); }
    finally { setBusy(false); }
  }, [reload, onChanged]);

  const doMove = (to: Phase) => {
    setMoveOpen(false);
    let reason: string | undefined;
    if (to === 'Blocked') {
      reason = window.prompt('Reason for blocking?') ?? undefined;
      if (reason === undefined) return;
    }
    run(() => api.transitionTicket(id, to, reason));
  };

  return (
    <div className={flush ? '' : 'anim-drawerdown rounded-xl border border-hair bg-panel overflow-hidden shadow-[0_18px_40px_-18px_rgba(0,0,0,0.8)]'}>
      {!t ? (
        <div className="px-5 py-6 text-sm text-gray-500">{error ?? 'Loading…'}</div>
      ) : (
        <>
          <div className="px-5 py-4 flex flex-col gap-5">
            {error && (
              <div className="text-xs text-red-400 bg-red-950/40 border border-red-900 rounded-lg px-3 py-2">{error}</div>
            )}

            {/* A stopped ticket asking a question. First thing in the drawer because it is the only
                thing on this ticket that is waiting on the person reading it. */}
            {t.phase === 'Blocked' && (
              <BlockedPanel
                reason={t.blockedReason}
                questions={t.blockedQuestions}
                blockedIn={t.phaseBeforeBlock}
                resumeTo={t.unblockTo}
                otherPhases={t.nextPhases.filter(p => p !== t.unblockTo)}
                busy={busy}
                onResume={(to, answer) => run(() => api.transitionTicket(t.id, to, answer))}
              />
            )}

            {/* Stopped at a gate this ticket's own setting skips, because the plan goes beyond the
                ask. Said here in so many words: otherwise it reads as the skip having broken. */}
            {!!t.heldForExpansion && (
              <div className="rounded-lg border border-amber-900/50 bg-amber-950/10 px-3 py-2.5 text-[12.5px] leading-snug text-amber-200/90">
                <b>Held for approval — the plan includes {t.heldForExpansion} expansion item{t.heldForExpansion === 1 ? '' : 's'}.</b>{' '}
                Speculation review is off for this ticket; it stopped here only because the plan adds
                work the ticket never asked for. Approve to build it, or delete the items marked
                expansion below to keep the ticket to its ask.
                <ul className="mt-1.5 list-disc pl-4 text-amber-100/70">
                  {t.workItems.filter(w => w.scope === 'Expansion' && !w.done).map(w => <li key={w.id}>{w.text}</li>)}
                </ul>
              </div>
            )}

            {/* The review gates, where the reviewer's answer is "yes, but". High in the drawer for
                the same reason the blocked panel is: it is the thing they came here to do. */}
            {(t.phase === 'NeedsReview' || t.phase === 'NeedsImplementationReview') && (
              <RequestChangesPanel
                busy={busy}
                priority={t.priority ?? DEFAULT_PRIORITY}
                canReplan={t.nextPhases.includes('NeedsSpeculation')}
                onSubmit={(body, items, shortLoop, replan, files, priority) =>
                  run(() => api.requestChanges(t.id, { body, changeItems: items, shortLoop, replan, files, priority }))}
              />
            )}

            {/* A change request in flight that is coming straight back here. Said on the ticket's
                face, because "this one is skipping QA" is not something to leave in a log — and
                editable, because the flag is granted at the review gate and not spent until the run
                that follows, and a reviewer whose mind changes in between (the blocking questions
                came back answered, the change turned out to be bigger than it looked) had no way to
                take it back. Only offered while the ticket is on the leg the flag actually routes;
                anywhere else it is either already spent or not yet a question. */}
            {(t.shortLoopReview || t.phase === 'NeedsImplementation' || t.phase === 'Implementing') && (
              <label
                className={`flex items-start gap-2 rounded-lg border px-3 py-2 text-[12px] leading-snug cursor-pointer ${
                  t.shortLoopReview
                    ? 'border-amber-900/50 bg-amber-950/10 text-amber-200/80'
                    : 'border-hair bg-black/20 text-gray-500'
                }`}
              >
                <input
                  type="checkbox"
                  checked={t.shortLoopReview}
                  disabled={busy}
                  onChange={e => run(() => api.updateTicket(t.id, { shortLoopReview: e.target.checked }))}
                  className="mt-[3px]"
                />
                <span>
                  <b>Short loop.</b>{' '}
                  {t.shortLoopReview
                    ? 'The next implementation returns straight to review — QA and refinement are being skipped for this change.'
                    : 'Off — the next implementation goes on to QA and refinement as usual. Tick to send it straight back to review instead.'}
                </span>
              </label>
            )}

            {/* Master notes — human-only, outrank everything else on the ticket */}
            <div>
              <SectionLabel>
                Master Notes
                <span className="normal-case tracking-normal text-gray-600"> · yours only · outranks the description</span>
              </SectionLabel>
              <div className="rounded-lg border border-amber-900/50 bg-amber-950/10 p-2">
                <EditableText
                  value={t.masterNotes ?? ''}
                  placeholder="Standing instructions for Claude on this ticket. Every phase reads these first and they win over the description. Claude can read them but never edit them."
                  onSave={v => run(() => api.updateTicket(t.id, { masterNotes: v }))}
                />
              </div>
            </div>

            {/* Description */}
            <div>
              <SectionLabel>Description</SectionLabel>
              {t.issueKey ? (
                <div className="text-[13.5px] leading-relaxed text-gray-300">
                  {t.description?.trim()
                    ? <ClampText text={t.description} lines={6} />
                    : <p className="text-gray-600">{t.issueUnavailable ? `${t.trackerName} unreachable — description unavailable.` : `No description in ${t.trackerName}.`}</p>}
                  <div className="mt-1.5 text-[11px] text-gray-500">
                    From {t.trackerName} <span className="font-mono">{t.issueKey}</span> · <a href={t.issueUrl ?? '#'} target="_blank" rel="noreferrer" className="text-blue-400 hover:underline">View in {t.trackerName}</a>
                  </div>
                </div>
              ) : (
                <EditableText
                  value={t.description ?? ''}
                  placeholder="No description."
                  onSave={v => run(() => api.updateTicket(t.id, { description: v }))}
                />
              )}
            </div>

            {/* Labels — your filing, and the one thing on this drawer the pipeline cannot see. Sits
                above Priority because everything below it changes how the ticket is WORKED, while
                this only changes how you find it again. */}
            <div>
              <SectionLabel>
                Labels
                <span className="normal-case tracking-normal text-gray-600"> · yours only · no phase ever reads these</span>
              </SectionLabel>
              <LabelEditor ticketId={t.id} disabled={busy} onChanged={onChanged} />
            </div>

            {/* Priority — kept above the pipeline options and separate from them. Those describe how
                this ticket is worked once an agent has it; this one decides whether it gets one
                before the rest of the board does. */}
            <div>
              <SectionLabel>
                Priority
                <span className="normal-case tracking-normal text-gray-600"> · which band the heartbeat claims it from</span>
              </SectionLabel>
              <PriorityPicker
                value={t.priority ?? DEFAULT_PRIORITY}
                disabled={busy}
                onChange={p => run(() => api.updateTicket(t.id, { priority: p }))}
              />
            </div>

            {/* Pipeline options — which human checkpoints this ticket stops at, and how much QA it gets */}
            <div>
              <SectionLabel>Pipeline options</SectionLabel>
              <TicketOptions
                speculation={t.requiresSpeculationReview}
                implementation={t.requiresImplementationReview}
                skipQaTesting={t.skipQaTesting}
                skipClassroomAnalysis={t.skipClassroomAnalysis ?? false}
                disabled={busy}
                track={t.track}
                onChange={o => run(() => api.updateTicket(t.id, o))}
              />
            </div>

            {/* Implementation summary — maintained by the implement phase */}
            {t.implementationSummary?.trim() && (
              <div>
                <SectionLabel>Implementation Summary</SectionLabel>
                <div className="rounded-lg border border-emerald-900/50 bg-emerald-950/20 px-3 py-2.5">
                  <ClampMarkdown text={t.implementationSummary} lines={9} />
                </div>
              </div>
            )}

            {/* Learning brief — optional reading the refine phase leaves for the human */}
            {t.learningBrief?.trim() && (
              <LearningBrief
                label={t.learningLabel || 'Learning brief'}
                brief={t.learningBrief}
                writtenAt={t.learningBriefAt}
              />
            )}

            {/* What the independent analysis raised and what refinement made of it - read at review,
                where a "not a defect" is exactly the call worth checking. */}
            {t.independentReview && <IndependentReview review={t.independentReview} />}

            {/* What the project's steward decided about this ticket, where there is one. */}
            <StewardOnTicket project={t.projectKey} ticketId={t.id} />

            {/* The way out to the deep version. The brief above is the few hundred words refinement
                owed you; this is the one you go and ask for when those were not enough. */}
            {t.branch && (
              <button
                onClick={() => window.dispatchEvent(
                  new CustomEvent('open-classroom', { detail: { ticketId: t.id } }),
                )}
                className="self-start text-[11px] text-indigo-400/80 hover:text-indigo-300 transition-colors"
              >
                Study this ticket in the Classroom &rarr;
              </button>
            )}

            {/* Work items. The agent walks this list top to bottom (and back up when it reworks
                one), so the row it is on is marked while it is on it — see WorkingMarker.

                Absent on a review ticket: work items are a plan for code WE are going to write, and
                add_work_items already refuses on that track. An empty checklist with an Add box
                under it invites filling in something nothing will ever read. */}
            {t.track !== 'Review' && (
            <div>
              <div className="flex items-center gap-2.5 mb-3">
                <ProgressRing done={t.workItems.filter(w => w.done).length} total={t.workItems.length} />
                <SectionLabel inline>Work items</SectionLabel>
              </div>
              <div className="flex flex-col gap-2">
                {t.workItems.map(w => {
                  // The service keeps startedAt on at most one item and never on a done one, so
                  // this needs no arithmetic over the list. It is gated on `agent` because the flag
                  // is intent, not truth: a run killed mid-item leaves it set for good, and the way
                  // that stays harmless is that nothing draws it once the agent is gone.
                  const working = agentIsIn(agent, 'Implementing') && !!w.startedAt && !w.done;
                  const accent = agent ? agentAccent(agent.slot) : null;
                  return (
                    <div
                      key={w.id}
                      className={`flex items-center gap-2.5 group rounded-md transition-colors ${working ? 'px-2 -mx-2 py-1 -my-1' : ''}`}
                      style={working && accent ? { background: accent.wash } : undefined}
                    >
                      <button
                        onClick={() => run(() => api.updateWorkItem(w.id, { done: !w.done }))}
                        disabled={busy}
                        className={`w-4 h-4 rounded flex-none flex items-center justify-center text-[9px] font-bold transition-colors ${
                          w.done
                            ? 'bg-emerald-400 text-[var(--w-void)]'
                            : 'border-[1.5px] border-gray-600 hover:border-gray-400'
                        }`}
                        title={w.done ? 'Mark not done' : 'Mark done'}
                      >
                        {w.done ? '✓' : ''}
                      </button>
                      <span
                        className={`flex-1 min-w-0 break-words text-[13.5px] ${w.done ? 'text-gray-400' : 'text-gray-200'}`}
                        style={working && accent ? { color: accent.hex } : undefined}
                      >
                        {w.text}
                      </span>
                      {/* Required is the unmarked default; only the two unasked kinds say so. */}
                      {w.scope === 'Expansion' && (
                        <span className="flex-none rounded border border-amber-900/60 px-1.5 text-[10px] uppercase tracking-wide text-amber-300/90" title="Beyond what the ticket asked for — needs an approval before it is built">expansion</span>
                      )}
                      {w.scope === 'Consequence' && (
                        <span className="flex-none rounded border border-hair px-1.5 text-[10px] uppercase tracking-wide text-gray-500" title="Not asked for, but the ask is still wrong without it">consequence</span>
                      )}
                      {working && agent && <WorkingMarker agent={agent} since={w.startedAt!} />}
                      <button
                        onClick={() => run(() => api.deleteWorkItem(w.id))}
                        className="opacity-0 group-hover:opacity-100 text-gray-600 hover:text-red-400 text-xs transition-opacity"
                      >
                        ✕
                      </button>
                    </div>
                  );
                })}
                {t.workItems.length === 0 && <p className="text-xs text-gray-600">None yet.</p>}
              </div>
              <AddInput placeholder="Add work item…" onAdd={v => run(() => api.addWorkItem(t.id, v))} />
            </div>
            )}

            {/* QA. Two numbers, because they answer different questions: the ring is how far
                through the list the run is, the label is how it is going. A single "N passed"
                cannot tell a run stalled on item 3 from one that tested everything and failed. */}
            <div>
              <div className="flex items-center gap-2.5 mb-3">
                {/* "Has a current outcome", not "is not Todo" — a Passed that expired at the last
                    rework bounce is back to being untested, and a ring reading full while the label
                    beside it reads 1/3 would be the drawer arguing with itself. Failed and Blocked
                    still count: they are outcomes, and this ring is progress, not success. */}
                <ProgressRing
                  done={t.qaItems.filter(q => q.status === 'Passed' ? q.verified : q.status !== 'Todo').length}
                  total={t.qaItems.length}
                  color="var(--w-queue-300)"
                  title="QA items with a current outcome"
                />
                {/* Counts the passes that still describe THIS branch, not every green row. The two
                    diverge exactly when it matters — after a rework bounce, when a checklist can
                    read all-green about a build that no longer exists. */}
                <SectionLabel inline>
                  QA{t.qaItems.length > 0 && (
                    <span className="normal-case tracking-normal text-gray-600"> · {t.qaItems.filter(q => q.verified).length}/{t.qaItems.length} verified</span>
                  )}
                </SectionLabel>
                {/* An empty QA list means two very different things, and only this says which:
                    "nobody wrote any yet" or "this ticket asked for none". */}
                {t.skipQaTesting && (
                  <span className="text-[10px] font-semibold rounded-full px-2 py-0.5 bg-amber-500/10 text-amber-400/90">
                    code review only
                  </span>
                )}
              </div>
              <div className="flex flex-col gap-2">
                {t.qaItems.map(q => {
                  // Same single-slot flag as the work items, and gated on a live agent for the same
                  // reason. Todo is the state an item sits in both before and during its test, so
                  // this is the only thing that says which one is actually under way.
                  const testing = agentIsIn(agent, 'QaTesting') && !!q.startedAt && q.status === 'Todo';
                  const accent = agent ? agentAccent(agent.slot) : null;
                  // A green row the pipeline no longer counts. Worth saying out loud rather than
                  // just dimming: "Passed" and "Passed, but about code from three bounces ago" look
                  // identical in the status column, and that is how a regression gets signed off.
                  const expired = q.status === 'Passed' && !q.verified;
                  return (
                    <div
                      key={q.id}
                      className={`flex items-center gap-2.5 group rounded-md transition-colors ${testing ? 'px-2 -mx-2 py-1 -my-1' : ''}`}
                      style={testing && accent ? { background: accent.wash } : undefined}
                    >
                      {/* Status leads the row, where the work items keep their checkbox: the two
                          lists are the same ticket at different stages, so an eye going down the
                          drawer finds every item's state in one column. Fixed width because the
                          status words differ in length and the texts beside them should not. */}
                      <select
                        value={q.status}
                        onChange={e => run(() => api.updateQaItem(q.id, { status: e.target.value as QaStatusName }))}
                        className={`flex-none w-[56px] text-center text-[10px] font-semibold rounded-full px-1 py-1 bg-gray-700 border-0 appearance-none cursor-pointer focus:outline-none ${QA_COLOR[q.status]}`}
                      >
                        {QA_STATUSES.map(s => <option key={s} value={s}>{s}</option>)}
                      </select>
                      <span
                        className="flex-1 text-[13.5px] text-gray-300 min-w-0 break-words"
                        style={testing && accent ? { color: accent.hex } : undefined}
                      >
                        {q.text}
                      </span>
                      {expired && (
                        <span
                          className="flex-none text-[10px] font-semibold rounded-full px-2 py-0.5 bg-amber-500/10 text-amber-400/90"
                          title={q.verifiedAt
                            ? `Passed ${new Date(q.verifiedAt).toLocaleString()}, before this ticket last went back for rework. QA must re-run it before the ticket can leave.`
                            : 'Passed before this ticket recorded when its outcomes were taken. QA must re-run it before the ticket can leave.'}
                        >
                          expired
                        </span>
                      )}
                      {q.passedByInspection && !expired && (
                        <span
                          className="flex-none text-[10px] font-semibold rounded-full px-2 py-0.5 bg-gray-600/40 text-gray-400"
                          title="Settled by reading the code, not by driving the app. See the note."
                        >
                          by inspection
                        </span>
                      )}
                      {testing && agent && <WorkingMarker agent={agent} since={q.startedAt!} verb="testing" />}
                      <button
                        onClick={() => run(() => api.deleteQaItem(q.id))}
                        className="opacity-0 group-hover:opacity-100 text-gray-600 hover:text-red-400 text-xs transition-opacity"
                      >
                        ✕
                      </button>
                    </div>
                  );
                })}
                {t.qaItems.length === 0 && (
                  <p className="text-xs text-gray-600">
                    {t.skipQaTesting
                      ? 'None — QA reviews the diff for this ticket and does not run the app.'
                      : 'None yet.'}
                  </p>
                )}
              </div>
              <AddInput placeholder="Add QA item…" onAdd={v => run(() => api.addQaItem(t.id, v))} />
            </div>

            {/* The branch under review and what was staged against it. Above the blockers because on
                a review ticket it is the whole point of the drawer, and absent entirely on a
                delivery one - `review` is null there, and the panel draws nothing. */}
            {t.review && <ReviewPanel t={t} onChanged={reload} />}

            {/* What this ticket is waiting on. The tracker's blocking links, refreshed live, plus
                anything found here - and the local verdict wins over the tracker either way. */}
            <BlockersSection t={t} run={run} />


            {/* QA shots — up to 6 screenshots of the verified work, maintained by the QA phase */}
            {t.qaShots.length > 0 && <QaShotsSection shots={t.qaShots} />}

            {/* No shots, but QA said why. Worth its own line rather than an absent section: the
                reader's question is "did anyone look?", and this is the answer to it. */}
            {t.qaShots.length === 0 && t.qaShotsWaiver && (
              <div>
                <SectionLabel>QA Shots</SectionLabel>
                <p className="text-[12px] leading-snug text-gray-500">
                  <span className="text-gray-400">No screenshots — </span>
                  {t.qaShotsWaiver}
                </p>
              </div>
            )}

            {/* Progress timeline */}
            <div>
              <SectionLabel>Progress</SectionLabel>
              {t.comments.length === 0 ? (
                <p className="text-xs text-gray-600">No progress notes.</p>
              ) : (() => {
                const ordered = t.comments.slice().reverse();
                const shown = showAllProgress ? ordered : ordered.slice(0, PROGRESS_LIMIT);
                const hidden = ordered.length - shown.length;
                return (
                  <div className="relative pl-[17px]">
                    <div className="absolute left-[3.5px] top-1 bottom-1 w-[2px] bg-gray-700" />
                    {shown.map(c => {
                      const who = c.author === 'Human' ? 'You' : c.author;
                      // A note of yours that no run has answered is the one entry on this timeline
                      // that is not history: it is holding the ticket's next hand-off right now.
                      // A steward's note holds it the same way, in the owner's place.
                      const pending = (c.author === 'Human' || c.author === 'Steward') && c.acknowledged === false;
                      const dot = pending ? 'bg-amber-400'
                        : c.author === 'System' ? 'bg-gray-600' : c.author === 'Claude' ? 'bg-violet-400' : 'bg-sky-400';
                      const head = c.author === 'System' ? 'text-gray-500' : c.author === 'Claude' ? 'text-violet-400' : 'text-sky-400';
                      return (
                        <div key={c.id} className="relative mb-3.5 last:mb-0">
                          <span
                            className={`absolute -left-[17px] top-[3px] w-[9px] h-[9px] rounded-full ${dot}`}
                            style={{ boxShadow: '0 0 0 3px var(--w-void)' }}
                          />
                          <div className={`text-[11px] mb-0.5 ${head}`} title={new Date(c.createdAt).toLocaleString()}>
                            <b>{who}</b>{c.phase && <> · {PHASE_LABEL[c.phase]}</>} · {agoLabel(c.createdAt)}
                            {pending && (
                              <span
                                className="ml-1.5 text-amber-400"
                                title={c.deliveredAt
                                  ? `Put in front of a run ${agoLabel(c.deliveredAt)} and still unanswered — the ticket cannot hand off until it is`
                                  : 'Will be delivered to the next run that picks this ticket up, and holds its hand-off until answered'}
                              >
                                · {c.deliveredAt ? 'delivered, awaiting answer' : 'awaiting the next run'}
                              </span>
                            )}
                          </div>
                          {c.body && <ClampText text={c.body} lines={5} className={c.author === 'System' ? 'italic text-gray-500' : 'text-gray-300'} />}
                          {c.attachments && c.attachments.length > 0 && <NoteFiles attachments={c.attachments} />}
                          {/* The answer under the question it answers. Eleven entries apart is how
                              this kind of exchange got lost before it was a first-class thing. */}
                          {c.acknowledgement && (
                            <div className="mt-1.5 border-l-2 border-violet-900 pl-2.5">
                              <div className="text-[10.5px] text-violet-400/80 mb-0.5">
                                Claude answered{c.acknowledgedAt && <> · {agoLabel(c.acknowledgedAt)}</>}
                              </div>
                              <ClampText text={c.acknowledgement} lines={4} className="text-[12px] text-gray-400" />
                            </div>
                          )}
                        </div>
                      );
                    })}
                    {(hidden > 0 || showAllProgress) && (
                      <button
                        onClick={() => setShowAllProgress(!showAllProgress)}
                        className="text-[11px] font-semibold text-sky-400 hover:text-sky-300 mt-1"
                      >
                        {showAllProgress ? 'Show less' : `Show ${hidden} more…`}
                      </button>
                    )}
                  </div>
                );
              })()}
              <NoteComposer
                placeholder="Tell the next run what to change — paste a screenshot or drop a log file…"
                submitLabel="Post note"
                busy={busy}
                onSend={(body, files) => run(() => api.addNote(t.id, body, files))}
              />
              <p className="mt-1.5 text-[10.5px] leading-snug text-gray-600">
                A note goes into the prompt of the next run that picks this ticket up, outranks the
                description, and holds that run's hand-off until it answers.
              </p>
            </div>

            {/* The trip, last because it is the only thing here that is about the ticket's history
                rather than its present. Straight up and to the right means it went through cleanly;
                a saw-tooth means the pipeline had to redo its own work, once per tooth. */}
            {journey.length > 0 && (
              <div>
                <SectionLabel>The trip it took</SectionLabel>
                <JourneyChart legs={journey} track={t.track} />
              </div>
            )}
          </div>

          {/* Footer */}
          <div className="border-t border-hair bg-panel px-5 py-2.5 flex items-center gap-3">
            {!t.issueKey && (
              <RenameControl title={t.title} onSave={v => run(() => api.updateTicket(t.id, { title: v }))} />
            )}
            {/* A review ticket owns no branch — the one it reads lives on its review target, was
                recorded when the ticket was created, and is not ours to retype. Offering "set
                branch" here would write Ticket.Branch, which every other feature reads as "the
                branch this ticket authored". */}
            {t.track !== 'Review' && (
              <BranchChip branch={t.branch ?? ''} onSave={v => run(() => api.updateTicket(t.id, { branch: v }))} />
            )}
            <WorktreeChip path={t.worktreePath ?? ''} />
            <IssueFooter
              issueKey={t.issueKey} issueUrl={t.issueUrl} issueUnavailable={t.issueUnavailable}
              trackerKind={t.trackerKind} trackerName={t.trackerName}
              onLink={key => run(() => api.updateTicket(t.id, { issueKey: key }))}
              onUnlink={title => run(() => api.updateTicket(t.id, { issueKey: '', title }))}
            />
            <span className="flex-1" />
            <button
              onClick={() => { if (window.confirm('Delete this ticket permanently?')) run(async () => { await api.deleteTicket(t.id); onClose(); }); }}
              className="text-[11px] text-red-400/60 hover:text-red-300"
            >
              Delete
            </button>
            <div className="relative">
              <button
                onClick={() => setMoveOpen(!moveOpen)}
                disabled={busy}
                className="btn-act text-gray-400 bg-gray-700 px-3.5 py-1.5 disabled:opacity-50"
              >
                Move ▾
              </button>
              {move.show && (
                <div className="orbit-pop absolute right-0 bottom-full mb-1 z-20 w-48 bg-gray-900 border border-hair rounded-lg shadow-xl py-1"
                     data-fx={move.fx} data-side="above">
                  {t.nextPhases.map(np => (
                    <button
                      key={np}
                      onClick={() => doMove(np)}
                      className="block w-full text-left px-3 py-1.5 text-xs text-gray-300 hover:bg-gray-800"
                    >
                      {PHASE_LABEL[np]}
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  );
}

// ---- Small pieces ----

// The markdown we render inline. This is a deliberately small set: it is exactly what the server
// emits when it flattens the tracker's rich text (see AdfText / HtmlText), plus what Claude writes in a summary or a
// comment. Struck-through text is the reason this exists at all - a crossed-out acceptance criterion
// read as a live one when the marks were only ever shown as literal tildes.
const INLINE_RULES: { open: string; close: string; raw?: boolean; wrap: (kids: ReactNode, key: string) => ReactNode }[] = [
  { open: '~~', close: '~~', wrap: (k, i) => <s key={i} className="text-gray-500">{k}</s> },
  { open: '**', close: '**', wrap: (k, i) => <strong key={i} className="font-semibold text-gray-100">{k}</strong> },
  { open: '==', close: '==', wrap: (k, i) => <mark key={i} className="rounded bg-amber-400/20 px-0.5 text-amber-100">{k}</mark> },
  { open: '<u>', close: '</u>', wrap: (k, i) => <u key={i}>{k}</u> },
  { open: '`', close: '`', raw: true, wrap: (k, i) => <code key={i} className="rounded bg-gray-800 px-1 font-mono text-[12px] text-gray-200">{k}</code> },
  { open: '*', close: '*', wrap: (k, i) => <em key={i}>{k}</em> },
];

// One line's worth. Parsed per line rather than over the whole string so a lone asterisk in prose
// can only ever swallow the rest of its own line, never the paragraphs below it.
function inlineNodes(text: string, keyPrefix: string): ReactNode[] {
  const out: ReactNode[] = [];
  let buf = '';
  let i = 0;
  let k = 0;
  while (i < text.length) {
    const rule = INLINE_RULES.find(r => text.startsWith(r.open, i));
    const close = rule ? text.indexOf(rule.close, i + rule.open.length) : -1;
    // No rule, no closing delimiter, or nothing between the two: it is just a character.
    if (!rule || close < 0 || close === i + rule.open.length) { buf += text[i++]; continue; }
    if (buf) { out.push(buf); buf = ''; }
    const inner = text.slice(i + rule.open.length, close);
    const key = `${keyPrefix}-${k++}`;
    out.push(rule.wrap(rule.raw ? inner : inlineNodes(inner, key), key));
    i = close + rule.close.length;
  }
  if (buf) out.push(buf);
  return out;
}

const HEADING = /^(#{1,6})\s+(.*)$/;

// Renders the flattened-markdown text we store. Block syntax is left as written - "1." and "- " read
// fine as themselves - except headings, whose hashes are noise once the line is styled as one.
function Formatted({ text }: { text: string }) {
  return (
    <>
      {text.split('\n').map((line, i) => {
        const heading = HEADING.exec(line);
        return (
          <span key={i} className={heading ? 'font-semibold text-gray-100' : undefined}>
            {i > 0 && '\n'}
            {inlineNodes(heading ? heading[2] : line, `l${i}`)}
          </span>
        );
      })}
    </>
  );
}

// Clamps text to `lines`, revealing a "more…"/"less" toggle only when it
// actually overflows. Expands inline rather than scrolling, so it never steals
// the outer page scroll.
function ClampText({ text, lines, className = 'text-gray-300' }: { text: string; lines: number; className?: string }) {
  const [expanded, setExpanded] = useState(false);
  const [overflows, setOverflows] = useState(false);
  const ref = useRef<HTMLParagraphElement>(null);

  useLayoutEffect(() => {
    if (expanded) return; // keep the last known overflow state while open
    const el = ref.current;
    if (el) setOverflows(el.scrollHeight > el.clientHeight + 1);
  }, [text, expanded]);

  return (
    <>
      <p
        ref={ref}
        className={`whitespace-pre-wrap text-[13px] leading-relaxed ${className}`}
        style={expanded ? undefined : { display: '-webkit-box', WebkitLineClamp: lines, WebkitBoxOrient: 'vertical', overflow: 'hidden' }}
      >
        <Formatted text={text} />
      </p>
      {(overflows || expanded) && (
        <button
          onClick={() => setExpanded(!expanded)}
          className="text-[11px] font-semibold text-sky-400 hover:text-sky-300 mt-0.5"
        >
          {expanded ? 'less' : 'more…'}
        </button>
      )}
    </>
  );
}

// The same bargain as ClampText - a few lines, then "more…" - for prose we render as real
// markdown blocks. -webkit-line-clamp counts lines of inline text, which a list or a heading is not,
// so the cut here is a max-height in ems and the toggle appears only when the blocks overrun it.
function ClampMarkdown({ text, lines }: { text: string; lines: number }) {
  const [expanded, setExpanded] = useState(false);
  const [overflows, setOverflows] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (expanded) return; // keep the last known overflow state while open
    const el = ref.current;
    if (el) setOverflows(el.scrollHeight > el.clientHeight + 1);
  }, [text, expanded]);

  return (
    <>
      <div ref={ref} style={expanded ? undefined : { maxHeight: `${lines * 1.6}em`, overflow: 'hidden' }}>
        <Markdown text={text} />
      </div>
      {(overflows || expanded) && (
        <button
          onClick={() => setExpanded(!expanded)}
          className="text-[11px] font-semibold text-sky-400 hover:text-sky-300 mt-1"
        >
          {expanded ? 'less' : 'more…'}
        </button>
      )}
    </>
  );
}

/**
 * What this ticket is waiting on.
 *
 * The tracker owns the links it knows about and they are re-read on every claim, so a block cleared
 * upstream clears here. What it cannot do is overrule the person doing the work: "Doesn't block"
 * pins a link off for good, and a blocker added here counts even though the tracker has never heard
 * of it. That is why dismissing keeps the row instead of deleting it — a deleted tracker-derived row
 * would simply come back on the next refresh.
 */
function BlockersSection({ t, run }: { t: TDetail; run: (fn: () => Promise<unknown>) => void }) {
  const blockers = t.blockers ?? [];
  const [adding, setAdding] = useState('');
  const active = blockers.filter(b => !b.satisfied && b.override !== false);

  return (
    <div>
      <SectionLabel>
        Waiting On
        {active.length > 0 && <span className="ml-2 text-violet-300 normal-case tracking-normal">· not claimable yet</span>}
      </SectionLabel>
      <div className="space-y-1.5 mb-2">
        {blockers.map(b => {
          const off = b.override === false;
          const clear = b.satisfied;
          return (
            <div key={b.id} className="group flex items-center gap-2 text-xs">
              <span className={`font-mono ${off ? 'text-gray-600 line-through' : clear ? 'text-emerald-300' : 'text-violet-300'}`}>
                {b.label}
              </span>
              {b.status && <span className="text-gray-500">{b.status}</span>}
              {b.unknown && <span className="text-amber-400/70" title="The tracker has not been reached for this blocker, so it is not being enforced">state unknown</span>}
              <span className="text-[10px] text-gray-600 uppercase tracking-wide">{b.origin}</span>
              {b.note && <span className="text-gray-500 truncate">— {b.note}</span>}
              <span className="flex-1" />
              <button
                onClick={() => run(() => api.setDependencyOverride(b.id, off ? null : false))}
                className="opacity-0 group-hover:opacity-100 text-[10px] text-gray-500 hover:text-gray-200 transition-opacity"
                title={off ? 'Follow the tracker again' : 'Dismiss: this does not actually block us'}
              >
                {off ? 'Restore' : "Doesn't block"}
              </button>
              {b.origin === 'Local' && (
                <button
                  onClick={() => run(() => api.deleteDependency(b.id))}
                  className="opacity-0 group-hover:opacity-100 text-gray-600 hover:text-red-400 transition-opacity"
                >
                  ✕
                </button>
              )}
            </div>
          );
        })}
        {blockers.length === 0 && <p className="text-xs text-gray-600">Nothing.</p>}
      </div>
      <div className="flex gap-2">
        <input
          value={adding}
          onChange={e => setAdding(e.target.value)}
          onKeyDown={e => {
            if (e.key !== 'Enter') return;
            const v = adding.trim();
            if (!v) return;
            // "#12" is always one of our own tickets. A BARE number is only ours when the project's
            // issue keys could not look like one — on Azure Boards a work item IS a bare number, so
            // reading "1234" as local ticket #1234 there would silently record the wrong blocker.
            const hashed = /^#\d+$/.test(v);
            const bare = /^\d+$/.test(v) && t.trackerKind !== 'AzureDevOps';
            const asId = hashed || bare ? Number(v.replace('#', '')) : undefined;
            run(() => api.addDependency(t.id, asId ? { blockerTicketId: asId } : { blockerIssueKey: v }));
            setAdding('');
          }}
          placeholder={t.trackerKind === 'AzureDevOps'
            ? 'Blocked by… (1234, or #12 for a local ticket)'
            : 'Blocked by… (PROJ-123, or #12 for a local ticket)'}
          className="flex-1 bg-panel border border-hair rounded px-2 py-1 text-xs text-gray-300 placeholder-gray-600 focus:outline-none focus:border-gray-600"
        />
      </div>
    </div>
  );
}

/**
 * The bot sitting on the item an agent is on right now — a work item being implemented or a QA
 * item being tested — with how long it has been there.
 *
 * Elapsed time rather than a bare "working" label because the two questions someone opens this
 * drawer with are "where is it" and "is it stuck", and the second one has no other answer here —
 * an item at 40 seconds and an item at 40 minutes look identical otherwise. It re-renders on its
 * own clock so the number keeps moving between the drawer's polls.
 */
function WorkingMarker({ agent, since, verb = 'started' }: { agent: AgentState; since: string; verb?: string }) {
  const [, bump] = useState(0);
  useEffect(() => {
    const h = setInterval(() => bump(n => n + 1), 15000);
    return () => clearInterval(h);
  }, []);
  const accent = agentAccent(agent.slot);
  return (
    <span
      className="flex items-center gap-1.5 flex-none"
      title={`Agent ${agent.slot} has been ${verb === 'testing' ? 'testing' : 'on'} this item ${agoLabel(since)}`}
    >
      <span className="text-[10px] font-mono tabular-nums" style={{ color: accent.hex, opacity: 0.75 }}>
        {ago(since)}
      </span>
      <AgentMascot mood="running" size={18} agent={agent.slot} />
    </span>
  );
}

function SectionLabel({ children, inline }: { children: ReactNode; inline?: boolean }) {
  return (
    <div className={`text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500 ${inline ? '' : 'mb-2.5'}`}>
      {children}
    </div>
  );
}

// The QA screenshots, as thumbnails that open over the drawer rather than in a tab. Its own
// component only so it can hold the lightbox's state without adding another hook to the drawer.
function QaShotsSection({ shots }: { shots: TDetail['qaShots'] }) {
  const { open, lightbox } = useShotLightbox(shots);
  return (
    <div>
      <SectionLabel>QA Shots</SectionLabel>
      <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
        {shots.map((s, i) => (
          <figure key={s.order} className="flex flex-col gap-1.5 min-w-0">
            <button
              type="button"
              onClick={() => open(i)}
              title="Open full screenshot"
              className="block rounded-lg border border-hair bg-black/40 overflow-hidden hover:border-gray-600 transition-colors cursor-zoom-in"
            >
              <img
                src={s.imageUrl}
                alt={s.caption || `QA shot ${s.order + 1}`}
                loading="lazy"
                className="w-full h-32 object-contain"
              />
            </button>
            {s.caption && (
              <figcaption className="text-[12px] leading-snug text-gray-400">{s.caption}</figcaption>
            )}
          </figure>
        ))}
      </div>
      {lightbox}
    </div>
  );
}

// Colour is a parameter because the board now has two of these meaning different things: emerald
// for work items delivered, sky for QA items tested. Sharing emerald would read as "these all
// passed", which is a claim the tested count does not make.
function ProgressRing({ done, total, color = 'var(--w-done-300)', title }: {
  done: number; total: number; color?: string; title?: string;
}) {
  const pct = total > 0 ? Math.round((done / total) * 100) : 0;
  return (
    <div
      className="w-9 h-9 rounded-full flex items-center justify-center flex-none"
      style={{ background: `conic-gradient(${color} 0 ${pct}%, var(--w-s750) ${pct}% 100%)` }}
      title={title ? `${done} of ${total} — ${title}` : undefined}
    >
      <div
        className="w-[26px] h-[26px] rounded-full bg-panel flex items-center justify-center font-mono text-[9px] font-semibold"
        style={{ color }}
      >
        {done}/{total}
      </div>
    </div>
  );
}

function EditableText({ value, placeholder, onSave }: { value: string; placeholder?: string; onSave: (v: string) => void }) {
  const [val, setVal] = useState(value);
  useEffect(() => setVal(value), [value]);
  const dirty = val !== value;
  return (
    <div>
      <textarea
        value={val} onChange={e => setVal(e.target.value)} placeholder={placeholder} rows={5}
        className="w-full bg-panel border border-hair rounded-lg px-3 py-2 text-[13.5px] leading-relaxed text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500"
      />
      {dirty && (
        <div className="flex gap-2 mt-1">
          <button onClick={() => onSave(val)} className="btn-act px-3 py-1 bg-blue-500 text-void">Save</button>
          <button onClick={() => setVal(value)} className="px-2 py-0.5 text-xs text-gray-400">Reset</button>
        </div>
      )}
    </div>
  );
}

const LETTER = (i: number) => String.fromCharCode(97 + i);

/** What a question is currently answered with: a listed option, free text, or nothing. */
type Pick = { kind: 'option'; index: number } | { kind: 'other' };

/** The option this question falls back to when nobody answers it, or -1. */
function recommendedIndex(q: BlockQuestion): number {
  return (q.options ?? []).findIndex(o => o.recommended);
}

/**
 * One question, rendered as something to click rather than something to read.
 *
 * The evidence lives collapsed under the question on purpose. It is the bulk of what the agent
 * wrote and it is worth keeping — the file:line citations are how you check the question is even
 * real — but it is the part you need once, not the part you need to answer.
 */
function BlockedQuestion({ q, n, pick, other, busy, onPick, onOther }: {
  q: BlockQuestion;
  n: number;
  pick: Pick | undefined;
  other: string;
  busy: boolean;
  onPick: (p: Pick) => void;
  onOther: (v: string) => void;
}) {
  const options = q.options ?? [];
  const rec = recommendedIndex(q);
  // Untouched with a recommendation reads as chosen, because it is: the run takes that option if
  // nobody says otherwise. Drawn dimmer than a real answer so the two are still distinguishable.
  const effective: Pick | undefined = pick ?? (rec >= 0 ? { kind: 'option', index: rec } : undefined);
  const isDefault = !pick && rec >= 0;

  const radio = (checked: boolean) =>
    `mt-[3px] h-3 w-3 shrink-0 rounded-full border ${
      checked ? (isDefault ? 'border-gray-400 bg-gray-400' : 'border-red-400 bg-red-400') : 'border-gray-600'}`;

  return (
    <div className="rounded-lg border border-hair bg-panel px-2.5 py-2">
      <div className="text-[13px] font-semibold leading-snug text-gray-100">
        <span className="text-gray-500">{n}.</span> <Formatted text={q.question} />
      </div>

      {q.why?.trim() && (
        <details className="group mt-1">
          <summary className="cursor-pointer list-none text-[11px] text-gray-500 hover:text-gray-300">
            <span className="group-open:hidden">why this is being asked ›</span>
            <span className="hidden group-open:inline">hide the detail ⌄</span>
          </summary>
          <div className="mt-1.5 whitespace-pre-wrap border-l border-hair pl-2.5 text-[12.5px] leading-relaxed text-gray-400">
            <Formatted text={q.why} />
          </div>
        </details>
      )}

      <div className="mt-2 flex flex-col gap-1">
        {options.map((o, j) => {
          const checked = effective?.kind === 'option' && effective.index === j;
          return (
            <button
              key={j}
              disabled={busy}
              onClick={() => onPick({ kind: 'option', index: j })}
              className={`flex items-start gap-2 rounded-md px-2 py-1.5 text-left disabled:opacity-50 ${
                checked ? 'bg-red-950/40 ring-1 ring-red-900/70' : 'hover:bg-gray-800'}`}
            >
              <span className={radio(checked)} />
              <span className="min-w-0">
                <span className="text-[12.5px] text-gray-200">
                  <span className="font-mono text-[11px] text-gray-500">({LETTER(j)})</span> <Formatted text={o.label} />
                </span>
                {o.recommended && (
                  <span className="ml-1.5 rounded bg-gray-800 px-1 py-px align-middle text-[10px] uppercase tracking-wide text-gray-400">
                    recommended
                  </span>
                )}
                {o.detail?.trim() && (
                  <span className="block text-[11.5px] leading-snug text-gray-500"><Formatted text={o.detail} /></span>
                )}
              </span>
            </button>
          );
        })}

        {/* Always offered when the agent allowed it, and the only route on an open question. */}
        {(q.allowOther ?? true) && (
          <button
            disabled={busy}
            onClick={() => onPick({ kind: 'other' })}
            className={`flex items-start gap-2 rounded-md px-2 py-1.5 text-left disabled:opacity-50 ${
              effective?.kind === 'other' ? 'bg-red-950/40 ring-1 ring-red-900/70' : 'hover:bg-gray-800'}`}
          >
            <span className={radio(effective?.kind === 'other')} />
            <span className="text-[12.5px] text-gray-400">{options.length ? 'Something else…' : 'Answer…'}</span>
          </button>
        )}
      </div>

      {effective?.kind === 'other' && (
        <textarea
          autoFocus
          value={other}
          onChange={e => onOther(e.target.value)}
          rows={2}
          placeholder="Your answer. It is quoted to the run that picks this ticket up."
          className="mt-1.5 w-full resize-y rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none"
        />
      )}

      {isDefault && (
        <div className="mt-1.5 text-[11px] text-gray-600">
          Not answered — Claude takes its own recommendation and records it as your decision.
        </div>
      )}
      {!pick && rec < 0 && (
        <div className="mt-1.5 text-[11px] text-amber-600/80">
          Not answered, and Claude offered no default. It will have to block again.
        </div>
      )}
    </div>
  );
}

/**
 * The blocked ticket's questions, and the answers that close the loop.
 *
 * The reason used to live only on the board row and in the comment timeline, so the drawer — where
 * someone goes to deal with it — showed everything about the ticket except the one thing stopping
 * it. Answering and resuming are deliberately the same action: what is answered here rides along as
 * the unblock reason, which is what the dashboard quotes to the next run that picks the ticket up.
 * Left in a comment instead, it is not read.
 *
 * Two shapes, one panel. A block that asks the owner to DECIDE arrives as `questions` and is
 * rendered as a form: the agent already knew the choices — one ticket stopped with four questions
 * and eleven lettered options, all of it flattened into four thousand characters of prose — so
 * asking the reader to reconstruct that list by reading was the whole defect. A block that only
 * REPORTS a fault has nothing to pick and stays a paragraph and a text box. `reason` is rendered
 * server-side from the questions either way, so it is the fallback whenever the structure is absent.
 */
function BlockedPanel({ reason, questions, blockedIn, resumeTo, otherPhases, busy, onResume }: {
  reason?: string | null;
  /** The structured form, when the agent asked in questions. Absent for a fault report or a legacy block. */
  questions?: BlockPayload | null;
  blockedIn?: Phase | null;
  /** The recommended resume phase (server-computed from the pre-block phase), if any. */
  resumeTo?: Phase | null;
  otherPhases: Phase[];
  busy: boolean;
  onResume: (to: Phase, answer: string | undefined) => void;
}) {
  const [answer, setAnswer] = useState('');
  const [picks, setPicks] = useState<Record<number, Pick>>({});
  const [others, setOthers] = useState<Record<number, string>>({});

  const qs = questions?.questions ?? [];
  const structured = qs.length > 0;
  const unanswered = qs.filter((_, i) => !picks[i]).length;

  /**
   * The answers as the one string the unblock carries. Composed here rather than posted as a new
   * shape because the whole downstream path — the transition log, the comment trail, the prompt the
   * next run reads — already takes exactly one string, and a second channel would be a second thing
   * that can disagree with the first.
   *
   * Questions left alone are stated as such, with what will happen to them. A silent omission would
   * read to the next run as a question that was never asked.
   */
  const compose = (): string | undefined => {
    const note = answer.trim();
    if (!structured) return note || undefined;

    const parts = qs.map((q, i) => {
      const head = `**${i + 1}. ${q.question.trim()}**`;
      const pick = picks[i];

      if (pick?.kind === 'other') {
        const text = others[i]?.trim();
        return `${head}\n${text ? text : '(left empty — treat this question as unanswered.)'}`;
      }
      if (pick?.kind === 'option') {
        const o = q.options![pick.index];
        return `${head}\n(${LETTER(pick.index)}) ${o.label.trim()}`;
      }

      const rec = recommendedIndex(q);
      if (rec >= 0) {
        return `${head}\n(${LETTER(rec)}) ${q.options![rec].label.trim()}\n`
          + '_Not answered. Your own recommendation was accepted as the decision — take it and proceed._';
      }
      return `${head}\n_Not answered, and you offered no recommendation. Do not guess: proceed only if you can now settle `
        + 'it from the repo, the master notes or the linked issue, and say in a comment which reading you took. Otherwise '
        + 'block again on this question alone._';
    });

    if (note) parts.push(`**Also:**\n${note}`);
    return parts.join('\n\n');
  };

  const resume = (to: Phase) => onResume(to, compose());
  const needs = (questions?.needs ?? []).filter(n => n.trim());

  return (
    <div className="rounded-lg border border-red-900/60 bg-red-950/20 px-3 py-2.5">
      <SectionLabel>
        <span className="text-red-300">⚠ Blocked{blockedIn ? ` while ${PHASE_LABEL[blockedIn].toLowerCase()}` : ''}</span>
        <span className="normal-case tracking-normal text-gray-500">
          {structured
            ? ` · ${qs.length} question${qs.length === 1 ? '' : 's'} for you`
            : ' · waiting on you'}
        </span>
      </SectionLabel>

      {/* The framing sentence for a structured block; the entire block for every other kind. */}
      {(structured ? questions?.preamble?.trim() : reason?.trim())
        ? (
          <div className="whitespace-pre-wrap text-[13.5px] leading-relaxed text-gray-200">
            <Formatted text={(structured ? questions!.preamble! : reason!).trim()} />
          </div>
        )
        : !structured && <div className="text-[13.5px] text-gray-500">No reason was recorded.</div>}

      {structured && (
        <div className="mt-2.5 flex flex-col gap-2">
          {qs.map((q, i) => (
            <BlockedQuestion
              key={i}
              q={q}
              n={i + 1}
              pick={picks[i]}
              other={others[i] ?? ''}
              busy={busy}
              onPick={p => setPicks(prev => ({ ...prev, [i]: p }))}
              onOther={v => setOthers(prev => ({ ...prev, [i]: v }))}
            />
          ))}
        </div>
      )}

      {/* Not questions: things to go and find. Listed rather than made actionable — the dashboard has
          no route to a tracker attachment, and pretending otherwise would be worse than saying it. */}
      {needs.length > 0 && (
        <div className="mt-2.5 rounded-lg border border-hair bg-panel px-2.5 py-2">
          <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">Would settle this fastest</div>
          <ul className="mt-1 list-inside list-disc text-[12.5px] leading-relaxed text-gray-400">
            {needs.map((n, i) => <li key={i}><Formatted text={n} /></li>)}
          </ul>
          <div className="mt-1 text-[11px] text-gray-600">
            Attach these to the linked issue or paste them into the master notes — the next run reads both.
          </div>
        </div>
      )}

      <textarea
        value={answer}
        onChange={e => setAnswer(e.target.value)}
        rows={structured ? 2 : 3}
        placeholder={structured
          ? 'Anything else the next run should know. Optional.'
          : 'Answer the question, then resume. Claude reads this on the run that picks the ticket up — so answer it here rather than in a progress note. Leave it empty if you fixed the ticket itself instead.'}
        className="mt-2.5 w-full bg-panel border border-hair rounded-lg px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500 resize-y"
      />
      <div className="mt-2 flex items-center gap-2 flex-wrap">
        {resumeTo && (
          <button
            disabled={busy}
            onClick={() => resume(resumeTo)}
            className="px-3 py-1.5 text-xs font-semibold bg-red-900/60 hover:bg-red-800 disabled:opacity-50 rounded-lg text-red-100"
          >
            Resume → {PHASE_LABEL[resumeTo]}
          </button>
        )}
        {/* Every unblock target stays available; the button above is only the one that picks the work
            back up where it stopped. */}
        <select
          disabled={busy}
          value=""
          onChange={e => { if (e.target.value) resume(e.target.value as Phase); }}
          className="bg-panel border border-hair rounded-lg px-2 py-1.5 text-[11.5px] text-gray-400 focus:outline-none focus:border-gray-500"
        >
          <option value="">{resumeTo ? 'Send somewhere else…' : 'Unblock into…'}</option>
          {otherPhases.map(p => <option key={p} value={p}>{PHASE_LABEL[p]}</option>)}
        </select>
        {structured && unanswered > 0 ? (
          <span className="text-[11px] text-gray-600">
            {unanswered} of {qs.length} left to Claude's own recommendation
          </span>
        ) : resumeTo && (
          <span className="text-[11px] text-gray-600">
            picks the {PHASE_LABEL[resumeTo].replace(/^Needs /, '').toLowerCase()} work back up
          </span>
        )}
      </div>
    </div>
  );
}

/**
 * "This is nearly right, but —" at the review gate, without leaving the dashboard.
 *
 * This is the panel that exists because the alternative was a person checking out the worktree by
 * hand and finishing the ticket in another tool. The move it makes was always available; what was
 * missing is that it carried NOTHING — the reviewer clicked NeedsImplementation and the next run
 * was handed a phase change with no reason, so the only way to say what you wanted was to go and
 * do it yourself.
 *
 * The three inputs map onto the three things that were being lost. The note says why (and takes the
 * screenshot of what is wrong). The change list says what, as work items, because the implement
 * phase is built around that checklist and prose it cannot see is prose it will not track. The
 * short loop says where to send it back to, which is the reviewer's call and nobody else's.
 */
/**
 * The reviewer's one composer: what is wrong, what to change, how fast it comes back, and which
 * band it comes back in.
 *
 * The priority control is here as well as in the drawer's own section on purpose — the same setting
 * reached from the sentence a reviewer is already in the middle of writing. "Fix this and get it
 * back to me" is one thought, and making them close this panel to finish saying it is how the
 * second half goes unsaid. It opens on the ticket's CURRENT band and is only sent when the reviewer
 * moved it, so leaving it alone says nothing rather than re-asserting Normal.
 */
function RequestChangesPanel({ busy, priority, canReplan, onSubmit }: {
  busy: boolean;
  /** The ticket's band right now — where the picker starts, and what counts as "unchanged". */
  priority: Priority;
  /** Whether this gate can send the ticket back to speculation — the final review, on Delivery. */
  canReplan: boolean;
  onSubmit: (body: string, items: string[], shortLoop: boolean, replan: boolean, files: File[], priority?: Priority) => void;
}) {
  const [open, setOpen] = useState(false);
  const [body, setBody] = useState('');
  const [items, setItems] = useState<string[]>([]);
  const [draft, setDraft] = useState('');
  const [shortLoop, setShortLoop] = useState(false);
  const [replan, setReplan] = useState(false);
  const [files, setFiles] = useState<File[]>([]);
  const [band, setBand] = useState<Priority>(priority);
  const pick = useRef<HTMLInputElement>(null);

  const addFiles = (incoming: FileList | File[] | null) => {
    if (incoming) setFiles(f => [...f, ...Array.from(incoming)]);
  };
  const addItem = () => {
    const v = draft.trim();
    if (v) { setItems(i => [...i, v]); setDraft(''); }
  };
  const reset = () => {
    setOpen(false); setBody(''); setItems([]); setDraft(''); setShortLoop(false); setReplan(false); setFiles([]);
    setBand(priority);
  };

  if (!open) {
    return (
      <button
        // Re-seed the band on open rather than only at mount, so a priority changed elsewhere in
        // the drawer is what the picker shows the next time this is unfolded.
        onClick={() => { setBand(priority); setOpen(true); }}
        className="w-full rounded-lg border border-amber-900/70 bg-amber-950/20 px-3 py-2 text-[12.5px] font-semibold text-amber-300 hover:bg-amber-950/40"
      >
        Request changes…
      </button>
    );
  }

  return (
    <div
      className="rounded-lg border border-amber-900/70 bg-amber-950/10 p-3"
      onDragOver={e => e.preventDefault()}
      onDrop={e => { e.preventDefault(); addFiles(e.dataTransfer.files); }}
    >
      <div className="flex items-center mb-2">
        <SectionLabel>Request changes</SectionLabel>
        <button onClick={reset} className="ml-auto text-[11px] text-gray-500 hover:text-gray-300">Cancel</button>
      </div>

      <textarea
        value={body}
        autoFocus
        onChange={e => setBody(e.target.value)}
        onPaste={e => { if (e.clipboardData.files.length > 0) { e.preventDefault(); addFiles(e.clipboardData.files); } }}
        placeholder="What is wrong, and what you want instead. Paste a screenshot of it…"
        rows={4}
        className="w-full resize-y rounded-md border border-hair bg-panel px-2.5 py-2 text-[12.5px] leading-snug text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500"
      />
      <PendingFiles files={files} onRemove={i => setFiles(f => f.filter((_, j) => j !== i))} />

      <div className="mt-2.5">
        <div className="text-[11px] text-gray-500 mb-1">
          Changes to make <span className="text-gray-600">— {replan
            ? 'each becomes a work item the new plan starts from'
            : 'each becomes a work item the run ticks off'}</span>
        </div>
        {items.map((it, i) => (
          <div key={i} className="flex items-start gap-2 text-[12.5px] text-gray-300 mb-1">
            <span className="text-gray-600 mt-[1px]">☐</span>
            <span className="flex-1">{it}</span>
            <button onClick={() => setItems(v => v.filter((_, j) => j !== i))} className="text-gray-600 hover:text-red-300">✕</button>
          </div>
        ))}
        <div className="flex gap-2 mt-1">
          <input
            value={draft}
            onChange={e => setDraft(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addItem(); } }}
            placeholder="Add a change…"
            className="flex-1 bg-panel border border-hair rounded-lg px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500"
          />
          <button onClick={addItem} className="px-3 py-1.5 text-xs font-semibold bg-gray-700 hover:bg-gray-600 rounded-lg text-gray-300">Add</button>
        </div>
      </div>

      {/* Above the short loop because it is the bigger fork: whether this is a correction at all,
          or new scope that wants planning — and an owner's yes — before anything is built. */}
      {canReplan && (
        <label className="flex items-start gap-2 mt-3 cursor-pointer">
          <input
            type="checkbox"
            checked={replan}
            onChange={e => { setReplan(e.target.checked); if (e.target.checked) setShortLoop(false); }}
            className="mt-[3px]"
          />
          <span className="text-[12px] leading-snug text-gray-400">
            <b className="text-gray-300">Replan first</b> — send it to speculation instead of straight
            to implementation, and hold the new plan for your approval. For new scope or a decision
            still open, rather than a correction. The plan covers this note only; finished work items
            stay done.
          </span>
        </label>
      )}

      <label className={`flex items-start gap-2 mt-3 ${replan ? 'opacity-40 cursor-not-allowed' : 'cursor-pointer'}`}>
        <input type="checkbox" checked={shortLoop} disabled={replan} onChange={e => setShortLoop(e.target.checked)} className="mt-[3px]" />
        <span className="text-[12px] leading-snug text-gray-400">
          <b className="text-gray-300">Bring it straight back to me</b> — skip QA and refinement and
          return here as soon as the change is made. For a tweak small enough that a full cycle costs
          more than it proves. Off means the full cycle: implement, QA, refine, then back here.
        </span>
      </label>

      {/* Sits under the short loop because the two answer the same question from opposite ends: that
          one shortens the trip, this one decides how long the ticket waits before the trip starts. */}
      <div className="mt-3">
        <div className="text-[11px] text-gray-500 mb-1.5">
          Priority on the way back
          {band !== priority && (
            <span className="text-amber-400/80"> — changing from {priority}</span>
          )}
        </div>
        <PriorityPicker value={band} disabled={busy} onChange={setBand} />
      </div>

      <div className="flex items-center gap-2 mt-3">
        <input ref={pick} type="file" multiple className="hidden" onChange={e => { addFiles(e.target.files); e.target.value = ''; }} />
        <button
          onClick={() => pick.current?.click()}
          className="px-2 py-1 text-[11px] font-semibold text-gray-400 hover:text-gray-200 border border-hair rounded-md"
        >
          Attach
        </button>
        <button
          // Only sent when the reviewer actually moved it — see requestChanges in the api client.
          onClick={() => { onSubmit(body.trim(), items, shortLoop, replan, files, band === priority ? undefined : band); reset(); }}
          disabled={busy || !body.trim()}
          className="ml-auto px-3 py-1.5 text-xs font-semibold bg-amber-700 hover:bg-amber-600 disabled:opacity-40 disabled:hover:bg-amber-700 rounded-lg text-amber-50"
        >
          {replan ? 'Send back for a replan' : 'Send back for changes'}
        </button>
      </div>
    </div>
  );
}

/**
 * Human-readable size for an attachment chip. Sub-kilobyte files are reported in bytes rather than
 * rounded up to "1KB": a 130-byte log dump is a real thing to attach, and rounding it makes every
 * small file look like the same file.
 */
function sizeLabel(bytes: number): string {
  if (bytes >= 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)}MB`;
  return bytes >= 1024 ? `${(bytes / 1024).toFixed(bytes >= 10 * 1024 ? 0 : 1)}KB` : `${bytes} B`;
}

/**
 * The files staged against a note that has not been posted yet.
 *
 * Paste is the affordance that matters here and the reason this is not a plain file input: the case
 * this whole channel was built for is "here is a screenshot of what is wrong", and that screenshot
 * is already on the clipboard. Making the reviewer save it to disk first is how they end up back in
 * a terminal instead.
 */
function PendingFiles({ files, onRemove }: { files: File[]; onRemove: (i: number) => void }) {
  if (files.length === 0) return null;
  return (
    <div className="flex flex-wrap gap-1.5 mt-2">
      {files.map((f, i) => (
        <span
          key={`${f.name}-${i}`}
          className="inline-flex items-center gap-1.5 rounded-md border border-hair bg-panel px-2 py-1 text-[11px] text-gray-300"
        >
          <span className="text-gray-500">{f.type.startsWith('image/') ? '🖼' : '📄'}</span>
          <span className="max-w-[160px] truncate" title={f.name}>{f.name}</span>
          <span className="text-gray-600">{sizeLabel(f.size)}</span>
          <button
            onClick={() => onRemove(i)}
            className="text-gray-600 hover:text-red-300"
            title="Remove"
          >
            ✕
          </button>
        </span>
      ))}
    </div>
  );
}

/**
 * A multi-line note with files attached — the composer behind both the progress-note box and the
 * review gate's change request.
 *
 * Deliberately not the one-line input the rest of this drawer uses for adding list items. Those add
 * a row to a checklist; this one writes an instruction that a worker is going to be held to, and a
 * box that cannot show two sentences at once is a box that gets two-word instructions written into
 * it. Enter inserts a newline here for the same reason — submit is the button, and Ctrl/Cmd+Enter.
 */
function NoteComposer({ placeholder, submitLabel, busy, onSend, autoFocus }: {
  placeholder: string;
  submitLabel: string;
  busy?: boolean;
  autoFocus?: boolean;
  onSend: (body: string, files: File[]) => void;
}) {
  const [val, setVal] = useState('');
  const [files, setFiles] = useState<File[]>([]);
  const [dragging, setDragging] = useState(false);
  const pick = useRef<HTMLInputElement>(null);

  const add = (incoming: FileList | File[] | null) => {
    if (!incoming) return;
    const next = Array.from(incoming);
    if (next.length > 0) setFiles(f => [...f, ...next]);
  };

  const submit = () => {
    const v = val.trim();
    // Files with no words are ambiguous in the one way that matters: a screenshot on its own does
    // not say whether it is the bug or the fix. The note is the instruction; the file illustrates it.
    if (!v) return;
    onSend(v, files);
    setVal('');
    setFiles([]);
  };

  return (
    <div
      className={`mt-2.5 rounded-lg border ${dragging ? 'border-sky-500 bg-sky-950/20' : 'border-hair bg-panel'}`}
      onDragOver={e => { e.preventDefault(); setDragging(true); }}
      onDragLeave={() => setDragging(false)}
      onDrop={e => { e.preventDefault(); setDragging(false); add(e.dataTransfer.files); }}
    >
      <textarea
        value={val}
        autoFocus={autoFocus}
        onChange={e => setVal(e.target.value)}
        // The clipboard is the primary route in. Anything non-text on it is an attachment.
        onPaste={e => { if (e.clipboardData.files.length > 0) { e.preventDefault(); add(e.clipboardData.files); } }}
        onKeyDown={e => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); submit(); } }}
        placeholder={placeholder}
        rows={3}
        className="w-full resize-y bg-transparent px-2.5 py-2 text-[12.5px] leading-snug text-gray-200 placeholder-gray-600 focus:outline-none"
      />
      <div className="px-2.5 pb-2">
        <PendingFiles files={files} onRemove={i => setFiles(f => f.filter((_, j) => j !== i))} />
        <div className="mt-2 flex items-center gap-2">
          <input
            ref={pick}
            type="file"
            multiple
            className="hidden"
            onChange={e => { add(e.target.files); e.target.value = ''; }}
          />
          <button
            onClick={() => pick.current?.click()}
            className="px-2 py-1 text-[11px] font-semibold text-gray-400 hover:text-gray-200 border border-hair rounded-md"
            title="Attach a screenshot or a log file — or just paste one into the box"
          >
            Attach
          </button>
          <span className="text-[10.5px] text-gray-600">paste or drop a screenshot</span>
          <button
            onClick={submit}
            disabled={busy || !val.trim()}
            className="ml-auto px-3 py-1.5 text-xs font-semibold bg-gray-700 hover:bg-gray-600 disabled:opacity-40 disabled:hover:bg-gray-700 rounded-lg text-gray-200"
          >
            {submitLabel}
          </button>
        </div>
      </div>
    </div>
  );
}

/** The files on a posted note: images as thumbnails that open, everything else as a chip that downloads. */
function NoteFiles({ attachments }: { attachments: NoteAttachment[] }) {
  const images = attachments.filter(a => a.isImage);
  const lightbox = useShotLightbox(images.map((a, i) => ({ order: i, caption: a.fileName, imageUrl: a.url })));
  const others = attachments.filter(a => !a.isImage);

  return (
    <div className="mt-1.5">
      {images.length > 0 && (
        <div className="flex flex-wrap gap-1.5">
          {images.map((a, i) => (
            <button key={a.id} onClick={() => lightbox.open(i)} title={a.fileName}>
              <img
                src={a.url}
                alt={a.fileName}
                className="h-16 w-auto max-w-[140px] rounded border border-hair object-cover hover:border-gray-500"
              />
            </button>
          ))}
        </div>
      )}
      {others.length > 0 && (
        <div className="flex flex-wrap gap-1.5 mt-1.5">
          {others.map(a => (
            <a
              key={a.id}
              href={a.url}
              target="_blank"
              rel="noreferrer"
              className="inline-flex items-center gap-1.5 rounded-md border border-hair bg-panel px-2 py-1 text-[11px] text-gray-300 hover:border-gray-500"
            >
              <span className="text-gray-500">📄</span>
              <span className="max-w-[180px] truncate">{a.fileName}</span>
              <span className="text-gray-600">{sizeLabel(a.bytes)}</span>
            </a>
          ))}
        </div>
      )}
      {lightbox.lightbox}
    </div>
  );
}

function AddInput({ placeholder, onAdd }: { placeholder: string; onAdd: (v: string) => void }) {
  const [val, setVal] = useState('');
  const submit = () => { const v = val.trim(); if (v) { onAdd(v); setVal(''); } };
  return (
    <div className="flex gap-2 mt-2.5">
      <input
        value={val} onChange={e => setVal(e.target.value)} placeholder={placeholder}
        onKeyDown={e => { if (e.key === 'Enter') submit(); }}
        className="flex-1 bg-panel border border-hair rounded-lg px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500"
      />
      <button onClick={submit} className="px-3 py-1.5 text-xs font-semibold bg-gray-700 hover:bg-gray-700 rounded-lg text-gray-300">Add</button>
    </div>
  );
}

function RenameControl({ title, onSave }: { title: string; onSave: (v: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [val, setVal] = useState(title);
  useEffect(() => setVal(title), [title]);
  if (!editing) {
    return (
      <button
        onClick={() => setEditing(true)}
        title="Rename ticket"
        className="text-[11px] text-gray-500 hover:text-gray-300"
      >
        Rename
      </button>
    );
  }
  const save = () => { const v = val.trim(); if (v) onSave(v); setEditing(false); };
  return (
    <span className="flex items-center gap-1.5">
      <input
        autoFocus value={val} onChange={e => setVal(e.target.value)} placeholder="Ticket title"
        onKeyDown={e => {
          if (e.key === 'Enter') save();
          if (e.key === 'Escape') { setVal(title); setEditing(false); }
        }}
        className="w-56 bg-panel border border-hair rounded px-2 py-1 text-[11px] text-gray-200 focus:outline-none focus:border-gray-500"
      />
      <button onClick={save} className="text-[10px] text-blue-400 hover:text-blue-300">Save</button>
      <button onClick={() => { setVal(title); setEditing(false); }} className="text-[10px] text-gray-500">Cancel</button>
    </span>
  );
}

function BranchChip({ branch, onSave }: { branch: string; onSave: (v: string) => void }) {
  const [editing, setEditing] = useState(false);
  const [val, setVal] = useState(branch);
  useEffect(() => setVal(branch), [branch]);
  if (!editing) {
    return (
      <button
        onClick={() => setEditing(true)}
        title="Edit branch"
        className="text-[10px] font-mono font-semibold px-2 py-1 rounded bg-panel border border-hair text-gray-400 hover:text-gray-200 hover:border-gray-600 transition-colors"
      >
        ⎇ {branch || 'set branch'}
      </button>
    );
  }
  return (
    <span className="flex items-center gap-1.5">
      <input
        autoFocus value={val} onChange={e => setVal(e.target.value)} placeholder="feature branch"
        onKeyDown={e => {
          if (e.key === 'Enter') { onSave(val.trim()); setEditing(false); }
          if (e.key === 'Escape') { setVal(branch); setEditing(false); }
        }}
        className="w-44 bg-panel border border-hair rounded px-2 py-1 text-[11px] font-mono text-gray-200 focus:outline-none focus:border-gray-500"
      />
      <button onClick={() => { onSave(val.trim()); setEditing(false); }} className="text-[10px] text-blue-400 hover:text-blue-300">Save</button>
      <button onClick={() => { setVal(branch); setEditing(false); }} className="text-[10px] text-gray-500">Cancel</button>
    </span>
  );
}

// Where this ticket's branch is actually checked out for the agent. Read-only: the heartbeat owns
// creating worktrees, so an editable field here could only ever disagree with the filesystem. It is
// shown so the path is one click away when you want to pick the ticket up and finish it by hand.
function WorktreeChip({ path }: { path: string }) {
  const [copied, setCopied] = useState(false);
  if (!path) return null;
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(path);
      setCopied(true);
      setTimeout(() => setCopied(false), 1200);
    } catch { /* clipboard blocked — the full path is in the tooltip either way */ }
  };
  return (
    <button
      onClick={copy}
      title={`Worktree: ${path}\nClick to copy`}
      className="text-[10px] font-mono px-2 py-1 rounded bg-panel border border-hair text-gray-500 hover:text-gray-200 hover:border-gray-600 transition-colors max-w-[16rem] truncate"
    >
      {copied ? 'copied' : `\u{1F4C1} ${path.split(/[\\/]/).filter(Boolean).slice(-1)[0]}`}
    </button>
  );
}

/**
 * The linked external issue, and the controls to link/change/unlink it.
 *
 * Every label here comes from the ticket's own project rather than a constant: on an Azure Boards
 * project a button reading "+ Link Jira" is not a cosmetic slip, it is an instruction to go and
 * look in a system this project does not use.
 */
function IssueFooter({ issueKey, issueUrl, issueUnavailable, trackerKind, trackerName, onLink, onUnlink }: {
  issueKey?: string | null; issueUrl?: string | null; issueUnavailable?: boolean;
  trackerKind: TrackerKind; trackerName: string;
  onLink: (key: string) => void; onUnlink: (title: string) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [key, setKey] = useState(issueKey ?? '');
  useEffect(() => { setKey(issueKey ?? ''); }, [issueKey]);

  // Unlinking turns this back into a standalone ticket, which needs a local title.
  const unlink = () => {
    const title = window.prompt(`This ticket’s title comes from ${trackerName}. Enter a title to keep after unlinking:`);
    if (title && title.trim()) onUnlink(title.trim());
  };

  // A project with no tracker has nothing to link to, and offering the control would only produce
  // a key the server refuses.
  if (trackerKind === 'None' && !issueKey) return null;

  const placeholder = trackerKind === 'AzureDevOps' ? '1234' : 'PROJ-123';

  if (editing) {
    return (
      <span className="flex items-center gap-1.5">
        <input
          autoFocus value={key} onChange={e => setKey(e.target.value)} placeholder={placeholder}
          title={`Title, description and URL come from ${trackerName} automatically — just the key.`}
          onKeyDown={e => {
            if (e.key === 'Enter' && key.trim()) { onLink(key.trim()); setEditing(false); }
            if (e.key === 'Escape') { setKey(issueKey ?? ''); setEditing(false); }
          }}
          className="w-28 bg-panel border border-hair rounded px-2 py-1 text-[11px] font-mono text-gray-200 focus:outline-none focus:border-gray-500"
        />
        <button onClick={() => { if (key.trim()) { onLink(key.trim()); setEditing(false); } }} className="text-[10px] text-blue-400 hover:text-blue-300">Save</button>
        <button onClick={() => { setKey(issueKey ?? ''); setEditing(false); }} className="text-[10px] text-gray-500">Cancel</button>
      </span>
    );
  }

  if (issueKey) {
    return (
      <span className="flex items-center gap-2">
        <a href={issueUrl ?? '#'} target="_blank" rel="noreferrer" className="text-[11px] font-mono text-blue-400 hover:underline">{issueKey}</a>
        {issueUnavailable && <span className="text-[10px] text-amber-400">unreachable</span>}
        <button onClick={() => setEditing(true)} className="text-[10px] text-gray-500 hover:text-gray-300">Change</button>
        <button onClick={unlink} className="text-[10px] text-gray-500 hover:text-red-300">Unlink</button>
      </span>
    );
  }

  return (
    <button onClick={() => setEditing(true)} className="text-[11px] text-gray-500 hover:text-gray-300">+ Link {trackerName}</button>
  );
}
