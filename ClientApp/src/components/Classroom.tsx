import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { PageHead } from './ui';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { AnalysisDoc } from './AnalysisBlocks';
import { WalkthroughCard } from './WalkthroughPlayer';
import { Markdown } from './LearningBrief';
import { JourneyChart } from './JourneyChart';
import { ago } from './JourneyTrack';
import { useShotLightbox } from './ShotLightbox';
import {
  LENSES, cancelQueued, clearQueue, deleteAnalysis, getAnalysis, getClassroomTicket,
  getRunStatus, getStudiableTickets, queueAnalysis,
} from '../api/classroom';
import type {
  Analysis, Bounce, ClassroomTicket, JourneyLeg, Lens, QueueItem, RunOutcome, RunStatus, StudiableTicket,
} from '../api/classroom';
import { PHASE_LABEL } from '../types/tickets';
import type { Comment, Phase, Track } from '../types/tickets';

// The sidebar reads furthest-along first: the pipeline order, reversed. Done is the exception — it is
// finished, not urgent, so it sinks below Backlog rather than topping the list. PHASE_LABEL's keys are
// written in pipeline order, so they double as the ranking.
const PHASE_ORDER = Object.keys(PHASE_LABEL) as Phase[];
const phaseRank = (phase: string) =>
  phase === 'Done' ? -1 : PHASE_ORDER.indexOf(phase as Phase);

/**
 * The Classroom.
 *
 * Everything else that fights cognitive drift on this board is attached to a moment in the pipeline:
 * the learning brief is written at refine, the review gate is a click at the end. Both are pushed at
 * the human whether or not they wanted them then, which is exactly why they get skimmed.
 *
 * This tab is the opposite and deliberately so. Nothing generates on its own, nothing gates on it,
 * and no phase ever reads what it produces. You pick a ticket because you want to understand it,
 * and a read-only agent goes and reads the branch and the repo and teaches you the corner of the
 * domain it landed in. Voluntary is the feature: material you chose to open is material you actually
 * read.
 *
 * One analysis runs at a time, which used to mean the second one you asked for was refused. Now it
 * queues: line up three tickets, walk away, and read them as they land. The queue panel and the
 * completion notices exist so that walking away is safe — what is running, what is next, and what
 * finished while you were looking somewhere else are all on screen without being asked for.
 */
/**
 * The one control that opens and shuts the picker.
 *
 * Same button in both states rather than a rail on one side and a link on the other: it is one
 * thing doing one thing, and a control that moves house when you press it is a control you have to
 * find again. The chevrons and the hover nudge both point the way the column is about to go.
 */
function PickerToggle({ open, running, waiting, onToggle }: {
  open: boolean; running: boolean; waiting: number; onToggle: () => void;
}) {
  const busy = running || waiting > 0;
  return (
    <button
      onClick={onToggle}
      title={!open && busy
        ? `${running ? 'An analysis is running' : 'Queued'}${waiting > 0 ? ` — ${waiting} waiting` : ''}. Open the queue.`
        : open ? 'Collapse the ticket list' : 'Show the full ticket list'}
      /* The nudge points the way the column is about to go, so one control reads as two
         directions without becoming two controls. `--w-nudge` is the motion layer's own travel
         distance, so overriding it here changes the direction and nothing else. */
      style={{ '--w-nudge': open ? '-2px' : '2px' } as CSSProperties}
      className="orbit-pick group flex items-center gap-2 self-start text-[10px] uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500 hover:text-gray-300"
    >
      <span className="font-mono group-hover:text-indigo-300 transition-colors">
        {open ? '‹‹' : '››'}
      </span>
      <span>{open ? 'collapse' : 'tickets'}</span>

      {/* The queue panel is inside the drawer and the drawer is shut by default, so without this a
          run would report its whole life to a column nobody is looking at. Not the panel in
          miniature - just enough to say there is something in there. */}
      {!open && busy && (
        <span className="flex items-center gap-1">
          <span
            className={`w-1.5 h-1.5 rounded-full ${running ? 'animate-pulse' : ''}`}
            style={{ background: running ? 'var(--w-active)' : 'var(--w-queue-400)' }}
          />
          {waiting > 0 && (
            <span className="font-mono text-[9px] tabular-nums text-gray-500">{waiting}</span>
          )}
        </span>
      )}
    </button>
  );
}

export function Classroom({ initialTicketId }: { initialTicketId?: number | null }) {
  const [projects, project, setProject] = useProjectList('classroom');
  const [tickets, setTickets] = useState<StudiableTicket[]>([]);
  const [selectedId, setSelectedId] = useState<number | null>(initialTicketId ?? null);
  const [filter, setFilter] = useState('');

  /**
   * The picker gets out of the way once it has been used.
   *
   * Picking is a moment; reading is the rest of the visit — and for the whole of that rest a
   * 19rem column of tickets you are not reading sits beside the one you are. Collapsed it keeps
   * only what identifies a row (`#37`, its key), which is enough to switch tickets without
   * expanding and gives the analysis back the width.
   *
   * Arriving with a ticket already chosen (from "Study this ticket" on the board) starts collapsed
   * for the same reason: that choice has already been made elsewhere.
   */
  const [pickerOpen, setPickerOpen] = useState(initialTicketId == null);

  /** Choosing a ticket, from wherever — the row, the queue panel, a finished-run notice. */
  const selectTicket = useCallback((id: number) => {
    setSelectedId(id);
    setPickerOpen(false);
  }, []);

  const [ticket, setTicket] = useState<ClassroomTicket | null>(null);
  const [analysis, setAnalysis] = useState<Analysis | null>(null);
  const [analysisId, setAnalysisId] = useState<number | null>(null);
  const [lens, setLens] = useState<Lens>('Full');
  const [status, setStatus] = useState<RunStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loadingDoc, setLoadingDoc] = useState(false);

  // Runs that finished while you were reading something else, waiting to be told about.
  const [notices, setNotices] = useState<RunOutcome[]>([]);

  // Queue entries this tab has asked for but not yet seen come back, so the click lands instantly
  // rather than at the speed of the round trip. They live only until that round trip returns.
  const [optimistic, setOptimistic] = useState<QueueItem[]>([]);

  // Which finished runs have already been dealt with — jumped to, reported, or dismissed. A set
  // rather than the single id this used to keep: a queue can finish several while you read one, and
  // one slot would silently drop all but the last of them.
  const acked = useRef<Set<number>>(new Set());
  // The outcomes already on the server when this tab opened are history, not news.
  const seeded = useRef(false);

  const loadTickets = useCallback(() => {
    if (!project) return;
    getStudiableTickets(project).then(setTickets).catch(() => setTickets([]));
  }, [project]);

  useEffect(loadTickets, [loadTickets]);

  // A ticket handed in from the drawer may belong to a project other than the one selected here.
  // Load it regardless — refusing to show what the user just clicked would be the wrong call.
  const loadTicket = useCallback((id: number) => {
    getClassroomTicket(id)
      .then(t => {
        setTicket(t);
        setAnalysisId(prev => (prev && t.analyses.some(a => a.id === prev) ? prev : t.analyses[0]?.id ?? null));
      })
      .catch(e => setError(e.message));
  }, []);

  useEffect(() => {
    if (selectedId == null) { setTicket(null); setAnalysis(null); setAnalysisId(null); return; }
    setError(null);
    loadTicket(selectedId);
  }, [selectedId, loadTicket]);

  useEffect(() => {
    if (analysisId == null) { setAnalysis(null); return; }
    setLoadingDoc(true);
    getAnalysis(analysisId)
      .then(setAnalysis)
      .catch(e => setError(e.message))
      .finally(() => setLoadingDoc(false));
  }, [analysisId]);

  const running = status?.running ?? false;
  const serverPending = status?.pending ?? [];

  // Poll fast while the queue has anything in it, not just while something is running: a waiting
  // item is a moving part too — it can be stepped over for a heartbeat agent, or start.
  const busy = running || serverPending.length > 0;

  useEffect(() => {
    let cancelled = false;
    const tick = () => getRunStatus().then(s => {
      if (cancelled) return;
      setStatus(s);

      if (!seeded.current) {
        seeded.current = true;
        s.recent.forEach(r => acked.current.add(r.id));
        return;
      }

      const fresh = s.recent.filter(r => !acked.current.has(r.id));
      if (fresh.length === 0) return;
      fresh.forEach(r => acked.current.add(r.id));

      // Counts and "last studied" move for every one of them, wherever you happen to be looking.
      loadTickets();

      // The one you are actually on gets the old behaviour: jump to what it produced, or say why
      // there is nothing. The rest become notices, because a banner about a ticket you are not
      // looking at is only useful if it says which ticket.
      const here = fresh.find(r => r.ticketId === selectedId);
      if (here?.analysisId) {
        setAnalysisId(here.analysisId);
        loadTicket(here.ticketId);
      } else if (here?.error) {
        setError(here.error);
      }

      const elsewhere = fresh.filter(r => r.ticketId !== selectedId);
      if (elsewhere.length > 0) setNotices(n => [...elsewhere, ...n]);
    }).catch(() => {});

    tick();
    const h = window.setInterval(tick, busy ? 3000 : 15000);
    return () => { cancelled = true; window.clearInterval(h); };
  }, [busy, selectedId, loadTicket, loadTickets]);

  const onQueue = async () => {
    if (selectedId == null) return;
    setError(null);

    // Negative so it can never collide with a real queue id.
    const temp = -Date.now();
    setOptimistic(o => [...o, {
      id: temp, ticketId: selectedId, lens, queuedAt: new Date().toISOString(), holdReason: null,
    }]);

    try {
      await queueAnalysis(selectedId, lens);
      setStatus(await getRunStatus());
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setOptimistic(o => o.filter(x => x.id !== temp));
    }
  };

  const onCancel = async (ticketId: number, forLens: Lens) => {
    await cancelQueued(ticketId, forLens).catch(() => {});
    setStatus(await getRunStatus().catch(() => null));
  };

  const onClearQueue = async () => {
    await clearQueue().catch(() => {});
    setStatus(await getRunStatus().catch(() => null));
  };

  const onDelete = async (id: number) => {
    await deleteAnalysis(id).catch(() => {});
    setAnalysisId(null);
    if (selectedId != null) loadTicket(selectedId);
    loadTickets();
  };

  const shown = useMemo(() => {
    const q = filter.trim().toLowerCase();
    const sorted = [...tickets].sort((a, b) => phaseRank(b.phase) - phaseRank(a.phase));
    if (!q) return sorted;
    // The issue key is included because it is now on screen, and a key you can see but cannot filter by is
    // an invitation to type it and get nothing.
    return sorted.filter(t =>
      t.title.toLowerCase().includes(q)
      || String(t.id).includes(q)
      || (t.issueKey?.toLowerCase().includes(q) ?? false));
  }, [tickets, filter]);

  // What the server knows, plus what this tab has just asked for and not yet seen acknowledged.
  const pending = useMemo(() => {
    const extra = optimistic.filter(o =>
      !serverPending.some(p => p.ticketId === o.ticketId && p.lens === o.lens));
    return [...serverPending, ...extra];
  }, [serverPending, optimistic]);

  // How a ticket is referred to out loud. The issue key when it has one — that is what you would say
  // to another person — and the local id when it does not.
  const keyFor = useCallback((id: number) => {
    const t = tickets.find(x => x.id === id);
    return t?.issueKey ?? `#${id}`;
  }, [tickets]);

  const titleFor = useCallback(
    (id: number) => tickets.find(x => x.id === id)?.title ?? '',
    [tickets]);

  return (
    <div className="max-w-[100rem] mx-auto px-6 py-4">
      <PageHead
        eyebrow="Whetstone // learning"
        title="Classroom"
        sub="Pick a ticket and have it taught to you. Nothing here is generated on its own, and nothing in the pipeline ever reads it."
        left={<div style={{ paddingBottom: 5 }}><ProjectPicker projects={projects} project={project} onChange={setProject} /></div>}
      />
      {/* The column width is the thing that moves, and it lives here rather than on the picker:
          `grid-template-columns` is the only property that knows about both tracks, so animating it
          is the only way the viewer widens in step with the picker narrowing instead of snapping
          across once the picker has finished. Both templates have two tracks, which is what lets
          the browser interpolate between them at all. */}
      <div
        className={`orbit-flank grid grid-cols-1 gap-6 items-start ${
          pickerOpen ? 'lg:grid-cols-[19rem_1fr]' : 'lg:grid-cols-[7.5rem_1fr]'
        }`}
      >
        {/* ---- Picker ---- */}
        <aside className="flex flex-col gap-3 lg:sticky lg:top-[72px] min-w-0">
          <PickerToggle
            open={pickerOpen}
            running={running}
            waiting={pending.length}
            onToggle={() => setPickerOpen(o => !o)}
          />

          {/* Keyed so React remounts on the switch instead of reconciling one div into the other:
              without it the contents change in a single frame under a width that is still moving,
              which is what made the whole thing read as a cut rather than a slide. */}
          {!pickerOpen ? (
              <div key="shut" className="orbit-swap flex flex-col gap-1 min-w-0 max-h-[calc(100vh-10rem)] overflow-y-auto">
                {shown.map(t => (
                  <button
                    key={t.id}
                    onClick={() => selectTicket(t.id)}
                    /* The title is the casualty of collapsing, so it moves to the tooltip rather
                       than being dropped: the row still answers "which one is that" on a hover. */
                    title={t.title}
                    aria-current={t.id === selectedId ? 'true' : undefined}
                    className={`orbit-pick text-left rounded px-2 py-1.5 border ${
                      t.id === selectedId
                        ? 'border-indigo-800 bg-indigo-950/40'
                        : 'border-transparent hover:border-hair hover:bg-gray-900/60'
                    }`}
                  >
                    <div className="font-mono text-[10.5px] text-gray-600">#{t.id}</div>
                    {/* The key at full length. It is what you actually recognise a ticket by, and
                        truncating it to fit would leave three rows all reading PROJ-88... */}
                    <div className={`font-mono text-[10.5px] leading-tight ${
                      t.id === selectedId ? 'text-indigo-300' : 'text-blue-300/80'
                    }`}>
                      {t.issueKey ?? '—'}
                    </div>
                  </button>
                ))}
              </div>
          ) : (
          <div key="open" className="orbit-swap flex flex-col gap-3 min-w-0">
          <QueuePanel
            running={running}
            currentTicketId={status?.ticketId ?? null}
            currentLens={status?.lens ?? null}
            currentKind={status?.kind ?? null}
            elapsed={status?.elapsedSeconds ?? 0}
            steps={status?.steps ?? 0}
            activity={status?.activity ?? null}
            expectedSeconds={status?.expectedSeconds ?? null}
            expectedSteps={status?.expectedSteps ?? null}
            pending={pending}
            keyFor={keyFor}
            titleFor={titleFor}
            onOpen={selectTicket}
            onCancel={onCancel}
            onClear={onClearQueue}
          />

          <input
            value={filter}
            onChange={e => setFilter(e.target.value)}
            placeholder="Filter tickets..."
            className="w-full rounded bg-gray-900 border border-hair px-2.5 py-1.5 text-[12px] text-gray-200 placeholder:text-gray-600 focus:outline-none focus:border-hair"
          />

          <div className="flex flex-col gap-1 max-h-[calc(100vh-16rem)] overflow-y-auto pr-1">
            {shown.length === 0 && (
              <div className="text-[11.5px] text-gray-600 leading-relaxed py-2">
                No tickets to study yet. A ticket becomes available once implementation has finished a
                first pass — before that there is a plan to read, not a change.
              </div>
            )}
            {shown.map(t => (
              <button
                key={t.id}
                onClick={() => selectTicket(t.id)}
                /* `orbit-pick` is the whole hover: the nudge toward the viewer it opens, the
                   pressed state, and the reduced-motion exemption. What stays here is only what is
                   particular to this list — which colours a row wears at rest. `aria-current` is
                   what exempts the selected row from moving, and it is also what a screen reader
                   reads, so the two can never fall out of step. */
                aria-current={t.id === selectedId ? 'true' : undefined}
                className={`orbit-pick group text-left rounded px-2.5 py-2 border ${
                  t.id === selectedId
                    ? 'border-indigo-800 bg-indigo-950/40'
                    : 'border-transparent hover:border-hair hover:bg-gray-900/60'
                }`}
              >
                <div className="flex items-baseline gap-1.5">
                  <span className="font-mono text-[10.5px] text-gray-600 group-hover:text-gray-500 transition-colors">#{t.id}</span>
                  <span className="text-[12.5px] text-gray-200 group-hover:text-gray-100 leading-snug line-clamp-2 transition-colors">{t.title}</span>
                </div>
                <div className="flex items-center gap-2 mt-1">
                  {/* Same chip the board uses. On the meta line rather than beside the title: the
                      sidebar is narrow, and the title is the thing that needs the width. */}
                  {t.issueKey && (
                    <span className="shrink-0 text-[10px] font-mono px-1.5 py-0.5 rounded bg-blue-900/60 text-blue-300">
                      {t.issueKey}
                    </span>
                  )}
                  <span className="text-[10px] text-gray-500">{t.phase}</span>
                  {t.analysisCount > 0 && (
                    <span className="text-[10px] text-indigo-400/80">
                      {t.analysisCount} {t.analysisCount === 1 ? 'analysis' : 'analyses'}
                    </span>
                  )}
                  {/* Queue state on the row itself, so you can see where a ticket stands without
                      having to select it and read the bar. */}
                  <QueueChip
                    running={running && status?.ticketId === t.id}
                    position={pending.findIndex(p => p.ticketId === t.id)}
                  />
                </div>
              </button>
            ))}
          </div>
          </div>
          )}
        </aside>

        {/* ---- Viewer ---- */}
        <main className="min-w-0">
          {/* Above everything, including the "select a ticket" placeholder: a run that finished
              while you were elsewhere is news wherever you happen to be. */}
          <Notices
            notices={notices}
            keyFor={keyFor}
            onOpen={(o) => {
              selectTicket(o.ticketId);
              if (o.analysisId) setAnalysisId(o.analysisId);
              setNotices(n => n.filter(x => x.id !== o.id));
            }}
            onDismiss={(id) => setNotices(n => n.filter(x => x.id !== id))}
          />

          {!ticket && (
            <div className="rounded-lg border border-hair bg-gray-900/30 px-6 py-10 text-center">
              <div className="text-[13px] text-gray-400">Select a ticket to study.</div>
              <div className="text-[11.5px] text-gray-600 mt-1.5 max-w-[46ch] mx-auto leading-relaxed">
                You will get the area it lives in, how it worked before, the routes that were rejected,
                what QA actually proved, and a route through the code.
              </div>
            </div>
          )}

          {ticket && (
            <div className="flex flex-col gap-5">
              <TicketHeader ticket={ticket} />

              <GenerateBar
                lens={lens}
                onLens={setLens}
                onQueue={onQueue}
                onCancel={() => onCancel(ticket.id, lens)}
                hasAnalysis={ticket.analyses.length > 0}
                eligible={ticket.eligible}
                reason={ticket.reason}
                readingThis={running && status?.kind !== 'walkthrough' && status?.ticketId === ticket.id && status?.lens === lens}
                queuedAt={pending.findIndex(p => p.kind !== 'walkthrough' && p.ticketId === ticket.id && p.lens === lens)}
                elapsed={status?.elapsedSeconds ?? 0}
                steps={status?.steps ?? 0}
                activity={status?.activity ?? null}
                expectedSeconds={status?.expectedSeconds ?? null}
                expectedSteps={status?.expectedSteps ?? null}
                lastInLine={
                  pending.length > 0 ? keyFor(pending[pending.length - 1].ticketId)
                    : running && status?.ticketId != null ? keyFor(status.ticketId)
                    : null
                }
              />

              {error && (
                <div className="rounded border border-rose-900/60 bg-rose-950/25 px-3.5 py-2.5 text-[12px] text-rose-200">
                  {error}
                </div>
              )}

              <Delivered summary={ticket.implementationSummary} />

              {ticket.analyses.length > 1 && (
                <HistoryStrip
                  ticket={ticket}
                  currentId={analysisId}
                  onPick={setAnalysisId}
                />
              )}

              {analysis && (
                <AnalysisView
                  analysis={analysis}
                  ticket={ticket}
                  repoPath={ticket.repoPath}
                  ideUrl={ticket.ideUrl}
                  shots={ticket.qaShots}
                  onDelete={() => onDelete(analysis.meta.id)}
                />
              )}

              {!analysis && !loadingDoc && ticket.analyses.length === 0
                && !(running && status?.ticketId === ticket.id) && (
                <div className="rounded-lg border border-hair bg-gray-900/30 px-5 py-8 text-center">
                  <div className="text-[13px] text-gray-400">Nothing written on this ticket yet.</div>
                  <div className="text-[11.5px] text-gray-600 mt-1.5">
                    Generating takes a few minutes — it reads the branch and the surrounding code, not
                    just the ticket.
                  </div>
                </div>
              )}

              <QaEvidence shots={ticket.qaShots} waiver={ticket.qaShotsWaiver} />

              <Journey legs={ticket.journey} track={ticket.track} cost={ticket.cost} />

              <Bounces bounces={ticket.bounces ?? []} />
            </div>
          )}
        </main>
      </div>
    </div>
  );
}

function TicketHeader({ ticket }: { ticket: ClassroomTicket }) {
  return (
    <header>
      <div className="flex items-baseline gap-2">
        <span className="font-mono text-[12px] text-gray-600">#{ticket.id}</span>
        <h1 className="text-[19px] font-semibold text-gray-100 leading-tight">{ticket.title}</h1>
      </div>
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 mt-1.5 text-[11.5px] text-gray-500">
        <span>{ticket.projectKey}</span>
        <span className="text-gray-400">{ticket.phase}</span>
        {ticket.branch && <span className="font-mono text-[11px] text-sky-400/80">{ticket.branch}</span>}
        {ticket.headSha && <span className="font-mono text-[11px]">{ticket.headSha.slice(0, 8)}</span>}
      </div>
    </header>
  );
}

/**
 * The waiting line: what is running, what is next, and the two ways out of it.
 *
 * Only on screen when there is something in it. An empty queue panel is a permanent reminder of a
 * feature you are not using, and the sidebar's job is the ticket list.
 *
 * It lives here rather than in the viewer because the queue outlives whatever you are reading: the
 * point of queueing is to go and look at something else, so the one panel that must stay put is
 * this one.
 */
function QueuePanel({
  running, currentTicketId, currentLens, currentKind, elapsed, steps, activity,
  expectedSeconds, expectedSteps, pending, keyFor, titleFor,
  onOpen, onCancel, onClear,
}: {
  running: boolean; currentTicketId: number | null; currentLens: Lens | null;
  /** 'walkthrough' when the slot is rewriting an existing analysis's walkthrough. */
  currentKind: 'analysis' | 'walkthrough' | null; elapsed: number;
  steps: number; activity: string | null;
  expectedSeconds: number | null; expectedSteps: number | null;
  pending: QueueItem[];
  keyFor: (id: number) => string; titleFor: (id: number) => string;
  onOpen: (id: number) => void;
  onCancel: (ticketId: number, lens: Lens) => void;
  onClear: () => void;
}) {
  // Before the early return: hooks cannot be conditional, and this panel comes and goes.
  const live = useLiveElapsed(elapsed, running);

  if (!running && pending.length === 0) return null;

  return (
    <section className="rounded-lg border border-hair bg-gray-900/50 px-2.5 py-2">
      <div className="flex items-baseline justify-between mb-1.5">
        <h3 className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500">Queue</h3>
        {pending.length > 0 && (
          <button
            onClick={onClear}
            title="Drop everything waiting. Whatever is running is left alone."
            className="text-[10px] text-gray-600 hover:text-rose-400 transition-colors"
          >
            clear {pending.length}
          </button>
        )}
      </div>

      {running && currentTicketId != null && (
        <div className="rounded bg-indigo-950/40 border border-indigo-900/60 px-2 py-1.5 mb-1">
          <div className="flex items-center gap-1.5">
            <span className="h-1.5 w-1.5 shrink-0 rounded-full bg-indigo-400 animate-pulse" />
            <button
              onClick={() => onOpen(currentTicketId)}
              className="min-w-0 flex-1 text-left text-[11.5px] text-gray-200 truncate hover:text-indigo-200 transition-colors"
            >
              {titleFor(currentTicketId) || keyFor(currentTicketId)}
            </button>
            {/* Stop addresses an analysis by ticket and lens; a walkthrough rewrite is short and
                has no lens of its own, so it is left to finish. */}
            {currentKind !== 'walkthrough' && (
              <button
                onClick={() => currentLens && onCancel(currentTicketId, currentLens)}
                title="Stop this run"
                className="shrink-0 text-[10px] text-gray-600 hover:text-rose-400 transition-colors"
              >
                stop
              </button>
            )}
          </div>
          <div className="flex items-center gap-2 mt-1.5 pl-3">
            <span className="font-mono text-[9.5px] text-gray-600">{keyFor(currentTicketId)}</span>
            <span className="text-[9.5px] text-gray-600">{currentKind === 'walkthrough' ? 'walkthrough' : currentLens}</span>
            <span className="ml-auto text-[9.5px] text-gray-500 tabular-nums">
              {clock(live)}
              {/* The estimate is shown, not just used. A bar at four fifths means nothing on its
                  own; "of about six minutes" is the sentence that lets you decide to walk away. */}
              {expectedSeconds != null && (
                <span className="text-gray-700"> / ~{clock(expectedSeconds)}</span>
              )}
            </span>
          </div>
          <RunBar elapsed={live} steps={steps}
                  expectedSeconds={expectedSeconds} expectedSteps={expectedSteps} />
          {/* What it is doing right now. The bar says how far along; only this says it is moving —
              and on a run that has genuinely wedged, this is the line that stops being new. */}
          <div className="mt-1 pl-3 font-mono text-[9px] leading-tight text-gray-600 truncate">
            {activity ?? 'starting up…'}
          </div>
        </div>
      )}

      {pending.map((p, i) => (
        /* The same hover the ticket list gives its rows, for the same reason: these open the same
           viewer, and the one that answers the pointer is the one that looks clickable. The row
           cannot be a single button — the × inside it is its own — so the lift and the hairline
           live on the wrapper and the text follows from group-hover. The running item above is
           left alone, on the ticket list's rule that what is already where it was going does not
           move. */
        <div
          key={p.id}
          className="group flex items-baseline gap-1.5 rounded border border-transparent px-2 py-1 transition-all duration-150 hover:border-hair hover:bg-gray-900/60 hover:translate-x-0.5 active:translate-x-0 motion-reduce:transition-none motion-reduce:hover:translate-x-0"
        >
          <span className="shrink-0 w-3 text-[9.5px] text-gray-600 group-hover:text-gray-500 tabular-nums transition-colors">{i + 1}</span>
          <div className="min-w-0 flex-1">
            <button
              onClick={() => onOpen(p.ticketId)}
              className="block w-full text-left text-[11.5px] text-gray-400 truncate group-hover:text-gray-200 transition-colors"
            >
              {titleFor(p.ticketId) || keyFor(p.ticketId)}
            </button>
            <div className="flex items-center gap-2">
              <span className="font-mono text-[9.5px] text-gray-700 group-hover:text-gray-500 transition-colors">{keyFor(p.ticketId)}</span>
              <span className="text-[9.5px] text-gray-700 group-hover:text-gray-500 transition-colors">{p.kind === 'walkthrough' ? 'walkthrough' : p.lens}</span>
            </div>
            {/* A held item is not stuck and not broken — a heartbeat agent is committing to that
                branch right now, and reading it would produce a document about a state that never
                existed. It keeps its place and goes as soon as the worker lets go. */}
            {p.holdReason && (
              <div className="text-[9.5px] text-amber-500/80 leading-snug mt-0.5">
                waiting — {p.holdReason}
              </div>
            )}
          </div>
          <button
            onClick={() => onCancel(p.ticketId, p.lens)}
            title="Take this out of the queue"
            /* Opacity rather than another colour, so the row lighting it up cannot fight the
               rose it turns under the pointer itself. */
            className="shrink-0 text-[10px] text-gray-700 opacity-60 group-hover:opacity-100 hover:text-rose-400 transition-all"
          >
            ×
          </button>
        </div>
      ))}
    </section>
  );
}

/** Where a ticket stands, on the row itself. Nothing when it is not in the queue at all. */
function QueueChip({ running, position }: { running: boolean; position: number }) {
  if (running) {
    return (
      <span className="flex items-center gap-1 text-[10px] text-indigo-300">
        <span className="h-1 w-1 rounded-full bg-indigo-400 animate-pulse" />
        reading
      </span>
    );
  }
  if (position < 0) return null;
  return <span className="text-[10px] text-gray-500">queued {ordinal(position + 1)}</span>;
}

/**
 * Runs that finished somewhere other than where you were looking.
 *
 * This is the half of the queue that makes walking away work. Without it the only report of a
 * finished run is the page you happen to have open, so queueing three tickets and going to read the
 * first one means never being told the other two landed.
 */
function Notices({ notices, keyFor, onOpen, onDismiss }: {
  notices: RunOutcome[];
  keyFor: (id: number) => string;
  onOpen: (o: RunOutcome) => void;
  onDismiss: (id: number) => void;
}) {
  if (notices.length === 0) return null;

  return (
    <div className="flex flex-col gap-2 mb-5">
      {notices.map(o => (
        <div
          key={o.id}
          className={`flex items-center gap-3 rounded border px-3.5 py-2 text-[12px] ${
            o.error
              ? 'border-rose-900/60 bg-rose-950/25 text-rose-200'
              : 'border-emerald-900/60 bg-emerald-950/25 text-emerald-200'
          }`}
        >
          <span className="min-w-0 flex-1">
            <b className="font-mono text-[11.5px]">{keyFor(o.ticketId)}</b>{' '}
            {o.error ? <>failed: {o.error}</> : 'is ready.'}
          </span>
          {!o.error && (
            <button
              onClick={() => onOpen(o)}
              className="shrink-0 underline underline-offset-2 hover:text-emerald-100 transition-colors"
            >
              Open it
            </button>
          )}
          <button
            onClick={() => onDismiss(o.id)}
            title="Dismiss"
            className="shrink-0 text-current/60 hover:text-current transition-colors"
          >
            ×
          </button>
        </div>
      ))}
    </div>
  );
}

/**
 * The one control, in three states, because there are three situations and they want three
 * different things from you.
 *
 * It is never disabled for the reason it used to be. "Something else is running" was the whole
 * argument for a queue, so answering it with a greyed-out button is answering the request with the
 * problem. The only thing it refuses now is asking twice for the identical read.
 */
function GenerateBar({
  lens, onLens, onQueue, onCancel, hasAnalysis, eligible, reason,
  readingThis, queuedAt, elapsed, steps, activity, expectedSeconds, expectedSteps, lastInLine,
}: {
  lens: Lens; onLens: (l: Lens) => void; onQueue: () => void; onCancel: () => void;
  hasAnalysis: boolean; eligible: boolean; reason: string | null;
  /** This ticket, under this lens, is the one running. */
  readingThis: boolean;
  /** Its place in the waiting line, or -1 when it is not in it. */
  queuedAt: number; elapsed: number;
  /** What the run has done and what runs like it have cost — the bar's two numerators. */
  steps: number; activity: string | null;
  expectedSeconds: number | null; expectedSteps: number | null;
  /** What this would go behind, if anything. */
  lastInLine: string | null;
}) {
  const live = useLiveElapsed(elapsed, readingThis);

  if (!eligible) {
    return (
      <div className="rounded-lg border border-hair bg-gray-900/40 px-4 py-3 text-[12px] text-gray-400">
        This ticket cannot be studied yet: {reason}.
      </div>
    );
  }

  const queued = queuedAt >= 0;

  return (
    <div className="rounded-lg border border-hair bg-gray-900/40 px-4 py-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex gap-1">
          {LENSES.map(l => (
            <button
              key={l.value}
              onClick={() => onLens(l.value)}
              title={l.hint}
              className={`btn-act px-3 py-1.5 border ${
                lens === l.value
                  ? 'bg-indigo-900 text-indigo-200 border-indigo-700'
                  : 'text-gray-400 hover:text-gray-200 border-transparent'
              }`}
            >
              {l.label}
            </button>
          ))}
        </div>

        {readingThis ? (
          <span className="btn-act ml-auto px-3.5 py-1.5 bg-gray-800 text-gray-300 tabular-nums">
            Reading… {clock(live)}
            {/* The estimate is shown, not just used: a bar at four fifths means nothing on its own,
                and "of about six minutes" is the sentence that lets you decide to walk away. */}
            {expectedSeconds != null && (
              <span className="text-gray-500"> / ~{clock(expectedSeconds)}</span>
            )}
          </span>
        ) : queued ? (
          <span className="ml-auto flex items-center gap-2 text-[12px]">
            <span className="px-3.5 py-1.5 rounded bg-gray-800 text-gray-300">
              Queued {ordinal(queuedAt + 1)}
            </span>
            <button onClick={onCancel} className="text-gray-500 hover:text-rose-400 transition-colors">
              cancel
            </button>
          </span>
        ) : (
          <button
            onClick={onQueue}
            className="btn-act ml-auto px-3.5 py-1.5 bg-indigo-500 text-void"
          >
            {lastInLine ? 'Add to queue' : hasAnalysis ? 'Generate again' : 'Generate'}
          </button>
        )}
      </div>

      <div className="text-[11px] text-gray-500 mt-2">
        {LENSES.find(l => l.value === lens)?.hint}
      </div>

      {/* The same bar the queue panel draws, on the same numbers — a run does not become less
          knowable because you happen to be looking at its ticket. What stood here was a third of a
          groove that pulsed and never moved, which reads as a wedged run rather than a working one:
          the one place with the most room to say how far along this is was the one saying least. */}
      {readingThis && (
        <>
          <RunBar elapsed={live} steps={steps} track="h-1 mt-2.5"
                  expectedSeconds={expectedSeconds} expectedSteps={expectedSteps} />
          <div className="mt-1.5 font-mono text-[9.5px] leading-tight text-gray-600 truncate">
            {activity ?? 'starting up…'}
          </div>
        </>
      )}

      {!readingThis && !queued && lastInLine && (
        <div className="mt-2 text-[11px] text-gray-500">
          Goes after {lastInLine}. One at a time, so the Classroom never competes with the heartbeat
          for the machine.
        </div>
      )}
    </div>
  );
}

/**
 * What the implement phase said it delivered — the pipeline's own words, passed straight through.
 *
 * Deliberately NOT generated and deliberately not folded into the analysis. This is the claim; the
 * analysis is the reading of it. Keeping them apart is what lets a reader notice when the two do not
 * agree, which is the single most useful thing this page can surface. It sits above the analysis
 * because it is two sentences of context and the analysis is fifteen hundred words of consequence.
 */
function Delivered({ summary }: { summary: string | null }) {
  if (!summary?.trim()) return null;
  return (
    <section className="rounded-lg border border-hair bg-gray-900/30 px-4 py-3">
      <h3 className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500 mb-1.5">
        What was delivered
        <span className="ml-2 normal-case tracking-normal font-normal text-gray-700">
          the implement phase's own summary
        </span>
      </h3>
      <div className="max-w-[70ch]">
        <Markdown text={summary.trim()} />
      </div>
    </section>
  );
}

/**
 * The screenshots QA captured, with their captions — again, shown rather than described.
 *
 * Below the analysis because they are proof and the analysis is the explanation: they land best
 * right after "how we know it works" has told you what to look for. When there are none but QA said
 * why, that answer takes their place, because the reader's question is "did anyone actually look?"
 * and silence is not an answer to it.
 */
function QaEvidence({ shots, waiver }: {
  shots: ClassroomTicket['qaShots']; waiver: string | null;
}) {
  const { open, lightbox } = useShotLightbox(shots);
  if (shots.length === 0 && !waiver?.trim()) return null;

  return (
    <section className="rounded-lg border border-hair bg-gray-900/25 px-5 py-4">
      <h3 className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500 mb-3">
        What QA saw
      </h3>

      {shots.length === 0 ? (
        <p className="text-[12.5px] leading-relaxed text-gray-500 max-w-[70ch]">
          <span className="text-gray-400">No screenshots — </span>{waiver}
        </p>
      ) : (
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          {shots.map((s, i) => (
            <figure key={s.order} className="flex flex-col gap-2 min-w-0">
              <button
                type="button"
                onClick={() => open(i)}
                title="Open the full screenshot"
                className="block rounded-lg border border-hair bg-black/40 overflow-hidden hover:border-gray-600 transition-colors cursor-zoom-in"
              >
                <img
                  src={s.imageUrl}
                  alt={s.caption || `QA shot ${s.order + 1}`}
                  loading="lazy"
                  className="w-full h-44 object-contain"
                />
              </button>
              {s.caption && (
                <figcaption className="text-[12.5px] leading-snug text-gray-400">{s.caption}</figcaption>
              )}
            </figure>
          ))}
        </div>
      )}
      {lightbox}
    </section>
  );
}

/**
 * Earlier analyses of the same ticket. Kept rather than overwritten because the comparison is the
 * point: what the system looked like at QA, next to what it looked like at review.
 */
function HistoryStrip({ ticket, currentId, onPick }: {
  ticket: ClassroomTicket; currentId: number | null; onPick: (id: number) => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <span className="text-[10px] uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-600 mr-1">Earlier reads</span>
      {ticket.analyses.map(a => (
        <button
          key={a.id}
          onClick={() => onPick(a.id)}
          className={`px-2 py-1 rounded text-[11px] border transition-colors ${
            a.id === currentId
              ? 'border-indigo-800 bg-indigo-950/50 text-indigo-200'
              : 'border-hair text-gray-500 hover:text-gray-300'
          }`}
        >
          {a.lens} · {a.phaseAtGeneration} · {agoLabel(a.generatedAt)}
        </button>
      ))}
    </div>
  );
}

function AnalysisView({ analysis, ticket, repoPath, ideUrl, shots, onDelete }: {
  analysis: Analysis; ticket: ClassroomTicket; repoPath: string; ideUrl: string;
  shots: ClassroomTicket['qaShots']; onDelete: () => void;
}) {
  // A second lightbox, not the QA panel's. Both hold an index into the same list, and sharing one
  // would mean threading a callback up through the analysis and back down — for a viewer whose
  // whole state is one number.
  const { open, lightbox } = useShotLightbox(shots);
  const m = analysis.meta;
  const tokens = m.inputTokens + m.outputTokens + m.cacheReadTokens + m.cacheCreationTokens;

  return (
    <article className="rounded-xl border border-hair bg-gray-900/20 px-6 py-6">
      {m.stale && (
        <div className="mb-5 rounded border border-amber-900/60 bg-amber-950/20 px-3.5 py-2.5 text-[12px] text-amber-200/90">
          Written when this ticket was in <b>{m.phaseAtGeneration}</b>
          {m.headSha ? <> at <code className="font-mono text-[11px]">{m.headSha.slice(0, 8)}</code></> : null}.
          The ticket has moved since — generate again for the current state.
        </div>
      )}

      <h2 className="text-[17px] leading-snug font-medium text-gray-100 max-w-[60ch]">{m.headline}</h2>

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 mt-2 mb-7 text-[11px] text-gray-600">
        <span>{m.lens} lens</span>
        <span>{agoLabel(m.generatedAt)}</span>
        <span>{(m.durationMs / 1000).toFixed(0)}s to write</span>
        <span>{compact(tokens)} tokens</span>
        {m.model && <span className="font-mono">{m.model}</span>}
        {/* The user-flow section is meant to be absent on work no user can see. Absent is honest but
            silent, and a run quietly skipping it on every ticket looks exactly like a run correctly
            skipping it on this one — so the omission says so out loud. */}
        {!m.hasUserFlow && (
          <span
            title="This run judged that nothing a person does with the product changed. Expected on backend-only work; worth a second look on anything with a UI."
            className="rounded border border-hair px-1.5 py-0.5 text-[10px] text-gray-600"
          >
            no user flow
          </span>
        )}
        <button onClick={onDelete} className="ml-auto text-gray-700 hover:text-rose-400 transition-colors">
          delete
        </button>
      </div>

      <WalkthroughCard analysis={analysis} ticket={ticket} />

      <AnalysisDoc
        doc={analysis.document}
        repoPath={repoPath}
        ideUrl={ideUrl}
        analysisId={m.id}
        shots={shots}
        onOpenShot={open}
      />
      {lightbox}
    </article>
  );
}

/**
 * Where the ticket's time actually went, drawn straight from the phase-transition table.
 *
 * Not model-written on purpose: the pipeline already recorded every move with a timestamp, so a
 * chart built from those rows cannot be wrong about them, and a paragraph describing them could.
 */
function Journey({ legs, track, cost }: { legs: JourneyLeg[]; track: Track; cost: ClassroomTicket['cost'] }) {
  if (legs.length === 0) return null;
  const total = legs.reduce((s, l) => s + (l.minutes ?? 0), 0);

  return (
    <section className="rounded-lg border border-hair bg-gray-900/25 px-5 py-4">
      <div className="flex items-baseline justify-between mb-3">
        <h3 className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500">
          The trip it took
        </h3>
        <div className="flex gap-4 text-[11px] text-gray-600 tabular-nums">
          <span>{legs.length} moves</span>
          {legs.some(l => l.backward) && (
            <span className="text-amber-500/80">{legs.filter(l => l.backward).length} sent back</span>
          )}
          <span>{minutes(total)} elapsed</span>
          {cost.runs > 0 && <span>{cost.runs} runs · {compact(
            cost.inputTokens + cost.outputTokens + cost.cacheReadTokens + cost.cacheCreationTokens,
          )} tokens</span>}
        </div>
      </div>

      <JourneyChart legs={legs} track={track} />

      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 mt-2.5 text-[10px] text-gray-600">
        <Key className="bg-indigo-400" label="forward" />
        <Key className="bg-amber-500" label="sent back" />
        <Key className="bg-sky-400" label="where it is now" />
        <span className="text-gray-700">dot size = time spent · hover for detail</span>
      </div>
    </section>
  );
}

/**
 * Why each amber tooth on the chart is there, in the pipeline's own words.
 *
 * The chart says a ticket went back; only the notes say why, and on the board those sit somewhere
 * in a timeline of thirty-odd entries. This lifts out the handful that belong to each bounce — the
 * move's reason, what was written in the phase it bounced out of, and what the rework said back —
 * so the question and its answer can be read without leaving the page.
 */
function Bounces({ bounces }: { bounces: Bounce[] }) {
  if (bounces.length === 0) return null;

  return (
    <section className="rounded-lg border border-hair bg-gray-900/25 px-5 py-4">
      <div className="flex items-baseline justify-between mb-3">
        <h3 className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500">
          Why it was sent back
        </h3>
        <span className="text-[11px] text-gray-600 tabular-nums">
          {bounces.length} {bounces.length === 1 ? 'bounce' : 'bounces'}
        </span>
      </div>

      <div className="flex flex-col gap-5">
        {bounces.map((b, i) => (
          <article key={b.leg} className="border-l-2 border-amber-600/60 pl-4">
            <div className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 text-[12.5px]">
              <span className="font-mono text-[10.5px] text-amber-500/80">{ordinal(i + 1)}</span>
              <span className="text-gray-200">{PHASE_LABEL[b.from] ?? b.from}</span>
              <span className="text-amber-500">→</span>
              <span className="text-gray-200">{PHASE_LABEL[b.to] ?? b.to}</span>
              <span className="text-[11px] text-gray-600" title={new Date(b.at).toLocaleString()}>
                by {ACTOR[b.actor] ?? b.actor} · {agoLabel(b.at)}
              </span>
            </div>

            {b.reason?.trim() && (
              <p className="mt-1.5 text-[12.5px] leading-relaxed text-amber-200/80 max-w-[80ch] whitespace-pre-wrap">
                {b.reason.trim()}
              </p>
            )}

            {b.why.length > 0 && (
              <div className="mt-3 flex flex-col gap-3">
                {b.why.map(c => <BounceNote key={c.id} note={c} />)}
              </div>
            )}

            {b.reply && (
              <div className="mt-3 border-l-2 border-violet-900 pl-3">
                <div className="text-[10px] uppercase tracking-[calc(var(--w-track)*0.1em)] text-violet-400/80 mb-1">
                  What the rework said
                </div>
                <BounceNote note={b.reply} />
              </div>
            )}

            {b.why.length === 0 && !b.reply && !b.reason?.trim() && (
              <p className="mt-1.5 text-[12px] text-gray-600">Nothing was written about this one.</p>
            )}
          </article>
        ))}
      </div>
    </section>
  );
}

const ACTOR: Record<string, string> = { Worker: 'a run', Human: 'you', System: 'the system' };

/** One progress note, clamped: a QA write-up is a page long, and the first screenful is the verdict. */
function BounceNote({ note }: { note: Comment }) {
  const [open, setOpen] = useState(false);
  const [overflows, setOverflows] = useState(false);
  const body = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (open) return;
    const el = body.current;
    if (el) setOverflows(el.scrollHeight > el.clientHeight + 1);
  }, [note.body, open]);

  const who = note.author === 'Human' ? 'You' : note.author;
  const tone = note.author === 'Human' ? 'text-sky-400' : note.author === 'Claude' ? 'text-violet-400' : 'text-gray-500';

  return (
    <div className="min-w-0">
      <div className={`text-[11px] mb-0.5 ${tone}`} title={new Date(note.createdAt).toLocaleString()}>
        <b>{who}</b>{note.phase && <> · {PHASE_LABEL[note.phase]}</>} · {agoLabel(note.createdAt)}
      </div>
      <div
        ref={body}
        className={`max-w-[80ch] overflow-hidden ${note.author === 'System' ? 'text-gray-400' : ''}`}
        style={open ? undefined : { maxHeight: '11rem' }}
      >
        <Markdown text={note.body} />
      </div>
      {note.attachments && note.attachments.some(a => a.isImage) && (
        <div className="flex flex-wrap gap-1.5 mt-1.5">
          {note.attachments.filter(a => a.isImage).map(a => (
            <a key={a.id} href={a.url} target="_blank" rel="noreferrer" title={a.fileName}>
              <img src={a.url} alt={a.fileName} className="h-16 w-auto max-w-[140px] rounded border border-hair object-cover hover:border-gray-500 transition-colors" />
            </a>
          ))}
        </div>
      )}
      {(overflows || open) && (
        <button
          onClick={() => setOpen(o => !o)}
          className="text-[11px] font-semibold text-sky-400 hover:text-sky-300 mt-1 transition-colors"
        >
          {open ? 'less' : 'more…'}
        </button>
      )}
    </div>
  );
}

function Key({ className, label }: { className: string; label: string }) {
  return (
    <span className="flex items-center gap-1.5">
      <span className={`h-1.5 w-1.5 rounded-full ${className}`} />
      {label}
    </span>
  );
}

/**
 * `ago` answers "just now" under a minute, which does not take the " ago" every other value wants.
 * A freshly generated analysis is exactly the case that hits it, so it would be the first thing you
 * ever saw this render.
 */
function agoLabel(iso: string): string {
  const a = ago(iso);
  return a === 'just now' ? a : `${a} ago`;
}

/**
 * The server's elapsed time, moving between the polls that carry it.
 *
 * The status endpoint is asked every three seconds, which is often enough to know what a run is
 * doing and far too rare to watch a clock by: the seconds would sit still and then jump three. So
 * the poll sets an anchor and the browser counts forward from it. Every poll re-anchors, so the
 * count can drift for at most one interval and can never disagree with the server about the total.
 *
 * Anchored on `performance.now()` rather than the wall clock: a laptop coming back from sleep, or an
 * NTP correction, would otherwise show a run that had been going for minus four seconds.
 */
function useLiveElapsed(serverSeconds: number, running: boolean): number {
  const anchor = useRef({ base: serverSeconds, at: performance.now() });
  const [live, setLive] = useState(serverSeconds);

  useEffect(() => {
    anchor.current = { base: serverSeconds, at: performance.now() };
    setLive(serverSeconds);
  }, [serverSeconds]);

  useEffect(() => {
    if (!running) return;
    const h = window.setInterval(
      () => setLive(anchor.current.base + (performance.now() - anchor.current.at) / 1000),
      500);
    return () => window.clearInterval(h);
  }, [running]);

  return running ? live : serverSeconds;
}

/**
 * How far along a run is, drawn from the two things that are actually known about it.
 *
 * There is no true percentage here to report — the run does not know how much reading is left
 * either — so the honest thing is to show it against what runs like it have cost before, and to
 * make sure it moves for a real reason. Two numerators:
 *
 * - **the clock**, which starts immediately and keeps the bar alive through the CLI's slow boot and
 *   the model's long thinking pauses, when nothing else is happening;
 * - **the steps**, which are real evidence of work rather than of time passing — the half that
 *   makes this a progress bar rather than a countdown.
 *
 * Averaged rather than raced, when both are known. Both are noisy estimates of the same thing and
 * the mean of two is steadier than the leader of two; taking whichever was further along let one
 * busy run — 18 tool calls against a typical 14 — push the bar to nine tenths while barely four
 * fifths of the expected time had gone.
 *
 * Past the estimate it decelerates instead of stopping, because an overrunning run is still running
 * and a bar frozen at its cap looks exactly like the thing this replaced. It reaches 100% only by
 * being replaced: the item leaves the panel when the run lands.
 */
function progressFraction(
  elapsed: number, steps: number, expectedSeconds: number | null, expectedSteps: number | null,
): number | null {
  const byTime = expectedSeconds && expectedSeconds > 0 ? elapsed / expectedSeconds : null;
  const bySteps = expectedSteps && expectedSteps > 0 ? steps / expectedSteps : null;

  const known = [byTime, bySteps].filter((v): v is number => v != null);
  if (known.length === 0) return null;                  // nothing comparable has ever finished
  const raw = known.reduce((a, b) => a + b, 0) / known.length;

  // 0.88 at exactly the expected cost, then an asymptote: the last tenth is reserved for finishing.
  const shaped = raw <= 1 ? 0.88 * raw : 0.88 + 0.11 * (1 - Math.exp(1 - raw));
  return Math.min(0.99, Math.max(0.02, shaped));
}

function RunBar({ elapsed, steps, expectedSeconds, expectedSteps, track = 'h-0.5 mt-1.5' }: {
  elapsed: number; steps: number; expectedSeconds: number | null; expectedSteps: number | null;
  /** Groove geometry, so the same bar can be a hairline in the queue and a rule under the control. */
  track?: string;
}) {
  const fraction = progressFraction(elapsed, steps, expectedSeconds, expectedSteps);

  // A run is reported as running a beat before it reports what it is expected to cost: the
  // expectation is read from history a little way into the run, behind a token check that can take
  // its time. So the opening seconds of every run look exactly like a lens with no history at all.
  // Holding still through that window costs nothing — the activity line underneath is already
  // saying "starting up…" — and it is the whole difference between a bar that begins at zero and
  // one that begins somewhere and falls back to zero when the denominator lands.
  const settling = fraction == null && elapsed < SETTLE_SECONDS;

  return (
    <div className={`${track} overflow-hidden rounded bg-gray-800`}>
      {settling ? (
        // An empty groove, deliberately: nothing is known yet, so nothing is claimed.
        null
      ) : fraction == null ? (
        // Past the settling window and still no denominator: this lens has never finished a run,
        // and nothing about how far along this one is will ever be known. Breathing out from the
        // left edge says "working" and claims nothing else, which is the whole of what is known —
        // and it never sits far enough right for a late estimate to look like ground being lost.
        <div className="h-full rounded bg-indigo-600 anim-runbar-wait" />
      ) : (
        // Slower than the 500ms tick that feeds it, so the fill glides rather than stepping.
        <div
          className="h-full rounded bg-indigo-500 transition-[width] duration-700 ease-out"
          style={{ width: `${(fraction * 100).toFixed(1)}%` }}
        />
      )}
    </div>
  );
}

/**
 * How long a run gets to produce an estimate before the bar stops waiting for one.
 *
 * Wider than the 3s status poll, so there has been at least one read after the run went in; short
 * enough that a lens genuinely running for the first time is not left staring at an empty groove.
 */
const SETTLE_SECONDS = 4;

/** Elapsed time for something you are watching happen — seconds matter, hours do not exist. */
function clock(seconds: number): string {
  const s = Math.max(0, Math.round(seconds));
  if (s < 60) return `${s}s`;
  return `${Math.floor(s / 60)}m ${String(s % 60).padStart(2, '0')}s`;
}

function ordinal(n: number): string {
  // 11th-13th are the exceptions the last-digit rule gets wrong.
  const rule = n % 100 >= 11 && n % 100 <= 13 ? 0 : n % 10;
  return `${n}${rule === 1 ? 'st' : rule === 2 ? 'nd' : rule === 3 ? 'rd' : 'th'}`;
}

function compact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n < 10_000_000 ? 1 : 0)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(n < 10_000 ? 1 : 0)}k`;
  return `${Math.round(n)}`;
}

function minutes(m: number): string {
  if (m < 1) return '<1m';
  if (m < 60) return `${Math.round(m)}m`;
  if (m < 1440) return `${(m / 60).toFixed(1)}h`;
  return `${(m / 1440).toFixed(1)}d`;
}
