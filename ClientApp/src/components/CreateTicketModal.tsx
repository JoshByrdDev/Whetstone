import { useEffect, useMemo, useRef, useState } from 'react';
import type { Phase, Priority, ProjectInfo, Track } from '../types/tickets';
import { PHASE_LABEL, DEFAULT_PRIORITY } from '../types/tickets';
import { createTicket, getUnlinkedIssues } from '../api/tickets';
import type { UnlinkedIssue } from '../api/tickets';
import { TicketOptions } from './TicketOptions';
import { useVeil } from '../hooks/useVeil';
import { usePop, useHeld } from '../hooks/usePop';
import { PriorityPicker } from './PriorityPicker';

// Phases a human would reasonably start a ticket in, per track. A review ticket usually wants to be
// worked now - you filed it because somebody is waiting on you - so its queue phase leads.
const START_PHASES: Record<Track, Phase[]> = {
  Delivery: ['Backlog', 'NeedsSpeculation', 'NeedsImplementation'],
  Review: ['NeedsPeerReview', 'Backlog'],
};

export function CreateTicketModal({
  projects, onClose, onCreated,
}: {
  projects: ProjectInfo[];
  onClose: () => void;
  onCreated: (id: number) => void;
}) {
  const [projectKey, setProjectKey] = useState(projects[0]?.key ?? '');
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [issueKey, setIssueKey] = useState('');
  const [phase, setPhase] = useState<Phase>('Backlog');
  const [priority, setPriority] = useState<Priority>(DEFAULT_PRIORITY);
  // All three off by default — Claude runs the pipeline end to end unless asked to stop, and QA
  // runs in full unless the ticket is small enough to be worth only a code review.
  const [specReview, setSpecReview] = useState(false);
  const [implReview, setImplReview] = useState(false);
  const [skipQa, setSkipQa] = useState(false);
  // Starts from the chosen project's default, and follows it when the project changes: the default
  // is a statement about that project's work, so carrying one project's answer into another's
  // ticket would be answering for the wrong project. Once set, the box is the ticket's own.
  const projectSkipsAnalysis = (key: string) =>
    projects.find(p => p.key === key)?.skipClassroomAnalysisByDefault ?? false;
  const [skipAnalysis, setSkipAnalysis] = useState(() => projectSkipsAnalysis(projectKey));
  useEffect(() => { setSkipAnalysis(projectSkipsAnalysis(projectKey)); },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [projectKey]);
  const [masterNotes, setMasterNotes] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Which pipeline this ticket runs on. Chosen HERE and only here: nothing moves a ticket between
  // tracks afterwards, because one that switched mid-flight would be sitting in a phase its new
  // track does not have.
  const [track, setTrack] = useState<Track>('Delivery');
  const [sourceBranch, setSourceBranch] = useState('');
  const [targetBranch, setTargetBranch] = useState('');
  const [prUrl, setPrUrl] = useState('');
  const [prAuthor, setPrAuthor] = useState('');
  const reviewing = track === 'Review';

  // On a project of several repos a pull request is in one of them, so the review names it. Only
  // the writable ones are offered: nothing is delivered from a read-only repo, so no PR is in one.
  const reviewRepos = useMemo(
    () => (projects.find(p => p.key === projectKey)?.repos ?? []).filter(r => !r.readOnly).map(r => r.dir),
    [projects, projectKey]);
  const [reviewRepo, setReviewRepo] = useState('');
  useEffect(() => {
    if (!reviewRepos.includes(reviewRepo)) setReviewRepo(reviewRepos.length === 1 ? reviewRepos[0] : '');
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reviewRepos]);

  // The board unmounts this the moment onClose fires, so leaving is something the panel does to
  // itself. Every way out goes through it, a successful create included - a panel that fades when
  // you cancel and blinks out when you submit reads as two different panels.
  const { fx, leave, dismiss } = useVeil(onClose);

  // Only the tracks this project has switched on, in a fixed order so the toggle never reshuffles.
  // A project that does no peer review is not offered it — and with one track there is no choice to
  // make, so the toggle disappears rather than standing there as a single button.
  const offered = useMemo<Track[]>(() => {
    const on = new Set(projects.find(p => p.key === projectKey)?.tracks ?? []);
    const list = (['Delivery', 'Review'] as Track[]).filter(t => on.has(t));
    return list.length ? list : ['Delivery'];
  }, [projects, projectKey]);
  const offeredKey = offered.join(',');

  // Switching project can withdraw the track being filled in. Fall back to the first one offered,
  // which carries the start phase with it — leaving a review phase selected on a delivery-only
  // project would submit a phase that project's board has nowhere to draw.
  useEffect(() => {
    if (!offered.includes(track)) pickTrack(offered[0]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [offeredKey, track]);

  function pickTrack(next: Track) {
    setTrack(next);
    // The start phase belongs to the track, so switching has to carry it - leaving 'Backlog' on a
    // review ticket is fine, but leaving 'NeedsImplementation' would be a phase it cannot enter.
    setPhase(START_PHASES[next][0]);
  }

  // When an issue key is entered, title/description are pulled from the tracker — we do not collect them.
  const linked = issueKey.trim().length > 0;

  // Issues assigned to me that no local ticket has claimed yet — the suggestions behind the key
  // field, so the common case is a click rather than typing a number from memory. Fetched on open
  // (and again when the project changes, since the tracker is per project) with no button:
  // by the time the form is up, the answer should already be there.
  const [candidates, setCandidates] = useState<UnlinkedIssue[]>([]);
  const [loadingCandidates, setLoadingCandidates] = useState(false);
  // Which tracker this project is on, as the server named it. Defaulted to a neutral word rather
  // than to "Jira": until the fetch lands we genuinely do not know, and a label that guesses wrong
  // is worse than one that is vague.
  const [trackerName, setTrackerName] = useState('issue tracker');
  useEffect(() => {
    if (!projectKey) return;
    let active = true;
    setLoadingCandidates(true);
    getUnlinkedIssues(projectKey)
      .then(r => { if (active) { setCandidates(r.issues); if (r.tracker) setTrackerName(r.tracker); } })
      // A picker that cannot load is not an error worth reporting: the field still takes a typed key.
      .catch(() => { if (active) setCandidates([]); })
      .finally(() => { if (active) setLoadingCandidates(false); });
    return () => { active = false; };
  }, [projectKey]);

  // ---- The key field's suggestion list ------------------------------------
  // This is one field, not two: the list is a dropdown hung off the input, opens on focus, narrows
  // as you type, and floats over the form rather than pushing it down — so it reads as a way of
  // filling the field instead of a second thing to answer.
  const [pickerOpen, setPickerOpen] = useState(false);
  const [highlight, setHighlight] = useState(0);
  const fieldRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);

  const query = issueKey.trim().toUpperCase();
  const matches = useMemo(() => (
    query
      ? candidates.filter(c =>
          c.key.toUpperCase().includes(query) || (c.summary ?? '').toUpperCase().includes(query))
      : candidates
  ), [candidates, query]);
  // A key typed out in full is a decision already made — nothing left to suggest. Same when a hand-
  // typed key matches nothing: an empty dropdown is noise, so it simply does not open.
  const settled = candidates.some(c => c.key.toUpperCase() === query);
  const suggesting = matches.length > 0 && !settled;
  const showList = pickerOpen && suggesting;
  // Held while it leaves: the list closes on the very keystroke that empties it, and an empty box
  // fading out is not the list you were reading.
  const list = usePop(showList);
  const shown = useHeld(matches, showList);

  useEffect(() => { setHighlight(0); }, [query]);

  // Clicking anywhere outside the field closes the list — including the rest of the form, which is
  // where the click is going anyway once a key has been picked.
  useEffect(() => {
    if (!showList) return;
    const onDown = (e: MouseEvent) => {
      if (!fieldRef.current?.contains(e.target as Node)) setPickerOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, [showList]);

  function choose(key: string) {
    setIssueKey(key);
    setPickerOpen(false);
    inputRef.current?.blur();
  }

  function onKeyFieldKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Escape' && pickerOpen) { e.stopPropagation(); setPickerOpen(false); return; }
    if (!suggesting) return;
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      if (!pickerOpen) { setPickerOpen(true); return; }
      setHighlight(h => Math.min(h + 1, matches.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setHighlight(h => Math.max(h - 1, 0));
    } else if (e.key === 'Enter' && showList) {
      // Enter belongs to the list while it is open; the form's Create button is a click away.
      e.preventDefault();
      choose(matches[Math.min(highlight, matches.length - 1)].key);
    }
  }

  async function submit() {
    if (reviewing && !sourceBranch.trim()) { setError('Which branch are you reviewing?'); return; }
    if (reviewing && reviewRepos.length > 0 && !reviewRepo) { setError('Which repo is their branch in?'); return; }
    // A review ticket names itself after the branch when nothing better is given, so the title is
    // only ever required on a standalone delivery ticket.
    if (!linked && !reviewing && !title.trim()) { setError('Title is required (or link an issue key).'); return; }
    setBusy(true); setError(null);
    try {
      const options = {
        // The two speculation/implementation gates are delivery-only: a review ticket never enters
        // either phase, so sending them would set flags nothing will ever read.
        ...(reviewing ? {} : {
          requiresSpeculationReview: specReview, requiresImplementationReview: implReview,
          skipClassroomAnalysis: skipAnalysis,
        }),
        // This one DOES carry over. On a review ticket it means the same thing it always did -
        // read the diff, do not stand the app up.
        skipQaTesting: skipQa,
        priority,
        masterNotes: masterNotes.trim() || undefined,
        ...(reviewing ? {
          track,
          sourceBranch: sourceBranch.trim(),
          targetBranch: targetBranch.trim() || undefined,
          prUrl: prUrl.trim() || undefined,
          prAuthor: prAuthor.trim() || undefined,
          repo: reviewRepos.length > 0 ? reviewRepo : undefined,
        } : {}),
      };
      const t = await createTicket(linked
        ? { projectKey, issueKey: issueKey.trim(), phase, ...options }   // URL derived server-side; no local title/desc
        : { projectKey, title: title.trim() || undefined, description: description.trim() || undefined, phase, ...options });
      leave(() => onCreated(t.id));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Create failed');
      setBusy(false);
    }
  }

  return (
    <div className="orbit-veil fixed inset-0 z-40 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/60" onClick={dismiss} />
      {/* Capped and scrollable. The form was already close to a short viewport's height, and adding
          the review fields tipped it over - at which point the panel simply grew past the bottom of
          the screen and took the Create button with it, because nothing here ever constrained it. */}
      <div className="orbit-modal relative w-full max-w-lg p-5 space-y-4 max-h-[calc(100vh-2rem)] overflow-y-auto">
        <div className="flex items-center justify-between">
          <h2 className="text-sm font-semibold text-gray-200 uppercase tracking-wider">New Ticket</h2>
          <button onClick={dismiss} className="text-gray-500 hover:text-gray-200 text-sm">✕</button>
        </div>

        {/* The first question, because it changes what the rest of the form is asking. Two tracks
            through one board: our own work, or somebody else's branch that we read and never touch.
            Absent entirely on a project that runs only one of them — a row of one button is not a
            question, and asking it would imply the other answer is available somewhere. */}
        {offered.length > 1 && (
          <div className="flex items-center gap-1.5">
            {offered.map(k => (
              <button
                key={k}
                type="button"
                onClick={() => pickTrack(k)}
                className={`text-[11px] px-2.5 py-1 rounded border transition-colors ${
                  track === k
                    ? 'text-gray-100 border-gray-500 bg-gray-700/60'
                    : 'text-gray-500 border-hair hover:text-gray-300 hover:border-gray-600'
                }`}
              >
                {k === 'Delivery' ? 'Our work' : 'Peer review'}
              </button>
            ))}
            <span className="text-[11px] text-gray-600 pl-1">
              {reviewing
                ? "Read someone else's branch and stage feedback. Nothing is ever pushed or posted."
                : 'Plan, build, test and ship a change on our own branch.'}
            </span>
          </div>
        )}

        {error && <div className="text-xs text-red-400 bg-red-950/40 border border-red-900 rounded px-3 py-2">{error}</div>}

        {/* Paired onto two rows rather than stacked on four. Only the branch is required - the other
            three are labels on the work - so giving each a full row of its own made the review form
            longer than the delivery form it is meant to be a shorter version of. */}
        {reviewing && (
          <div className="space-y-3 rounded-lg border border-hair bg-black/20 p-3">
            {reviewRepos.length > 0 && (
              <Labeled label="In repo">
                <select value={reviewRepo} onChange={e => setReviewRepo(e.target.value)} className={inputCls}>
                  {reviewRepos.length > 1 && <option value="">the repo their pull request is in…</option>}
                  {reviewRepos.map(d => <option key={d} value={d}>{d}</option>)}
                </select>
              </Labeled>
            )}
            <div className="grid grid-cols-2 gap-3">
              <Labeled label="Their branch">
                <input
                  value={sourceBranch}
                  onChange={e => setSourceBranch(e.target.value)}
                  placeholder="feature/their-work"
                  autoFocus
                  className={inputCls}
                />
              </Labeled>
              <Labeled label="Into (optional)">
                <input
                  value={targetBranch}
                  onChange={e => setTargetBranch(e.target.value)}
                  placeholder="the project's base branch"
                  className={inputCls}
                />
              </Labeled>
            </div>
            <div className="grid grid-cols-[1fr_auto] gap-3">
              <Labeled label="Pull request URL (optional)">
                <input
                  value={prUrl}
                  onChange={e => setPrUrl(e.target.value)}
                  placeholder="https://dev.azure.com/…/pullrequest/1234"
                  className={inputCls}
                />
              </Labeled>
              <Labeled label="Author (optional)">
                <input
                  value={prAuthor}
                  onChange={e => setPrAuthor(e.target.value)}
                  placeholder="who wrote it"
                  className={`${inputCls} w-32`}
                />
              </Labeled>
            </div>
          </div>
        )}

        <div className="grid grid-cols-2 gap-3">
          <Labeled label="Project">
            <select value={projectKey} onChange={e => setProjectKey(e.target.value)} className={inputCls}>
              {projects.map(p => <option key={p.key} value={p.key}>{p.displayName}</option>)}
            </select>
          </Labeled>
          <Labeled label="Start phase">
            <select value={phase} onChange={e => setPhase(e.target.value as Phase)} className={inputCls}>
              {START_PHASES[track].map(p => <option key={p} value={p}>{PHASE_LABEL[p]}</option>)}
            </select>
          </Labeled>
        </div>

        {/* Beside project and start phase rather than down with the pipeline options: those decide
            how this ticket is worked, and this one decides when it is worked at all. */}
        <Labeled label="Priority">
          <PriorityPicker value={priority} onChange={setPriority} />
        </Labeled>

        <Labeled label={`${trackerName} key (optional)`}>
          <div className="relative" ref={fieldRef}>
            <input
              ref={inputRef}
              value={issueKey}
              onChange={e => { setIssueKey(e.target.value); setPickerOpen(true); }}
              onFocus={() => setPickerOpen(true)}
              onKeyDown={onKeyFieldKeyDown}
              className={`${inputCls} font-mono ${suggesting || loadingCandidates ? 'pr-20' : ''}`}
              placeholder="PROJ-123"
              role="combobox"
              aria-expanded={showList}
              aria-autocomplete="list"
            />
            {/* Inside the field's own edge, because that is what it fills. The count is the whole
                pitch: "there are N of these waiting for you, and you can have one without typing". */}
            {(loadingCandidates || suggesting) && (
              <button
                type="button"
                onMouseDown={e => e.preventDefault()}
                onClick={() => { setPickerOpen(o => !o); inputRef.current?.focus(); }}
                disabled={loadingCandidates}
                title={`${trackerName} issues assigned to you that have no ticket here yet`}
                className="absolute right-1.5 top-1/2 -translate-y-1/2 flex items-center gap-1 px-1.5 py-0.5 rounded text-[10px] text-gray-400 hover:text-gray-200 hover:bg-gray-800 disabled:hover:bg-transparent transition-colors"
              >
                {loadingCandidates
                  ? <span className="text-gray-600">…</span>
                  : <><span>{matches.length} mine</span><span className={showList ? 'rotate-180' : ''}>▾</span></>}
              </button>
            )}

            {/* bg-panel alone is glass by theme default, and the Title field sitting under this one
                showed straight through it — a floating list has to occlude to read as floating. */}
            {list.show && (
              <div className="orbit-pop absolute z-20 left-0 right-0 mt-1 rounded-md border border-hair bg-panel/[0.98] backdrop-blur-sm shadow-lg shadow-black/60 overflow-hidden"
                   data-fx={list.fx}>
                <div className="px-2.5 py-1 text-[10px] uppercase tracking-wider text-gray-600 border-b border-hair">
                  Assigned to you · no ticket yet
                </div>
                <div className="max-h-44 overflow-y-auto">
                  {shown.map((c, i) => (
                    <button
                      key={c.key}
                      type="button"
                      // mousedown would blur the input first and close the list out from under the click.
                      onMouseDown={e => e.preventDefault()}
                      onClick={() => choose(c.key)}
                      onMouseEnter={() => setHighlight(i)}
                      className={`w-full text-left px-2.5 py-1.5 flex items-baseline gap-2 ${
                        i === Math.min(highlight, shown.length - 1) ? 'bg-gray-800/80' : ''}`}
                    >
                      <span className="font-mono text-xs text-blue-300 shrink-0">{c.key}</span>
                      <span className="text-xs text-gray-400 truncate">{c.summary}</span>
                      {c.status && <span className="ml-auto text-[10px] text-gray-600 shrink-0">{c.status}</span>}
                    </button>
                  ))}
                </div>
              </div>
            )}
          </div>
        </Labeled>

        {linked ? (
          <div className="text-xs text-gray-400 bg-gray-900/60 border border-hair rounded px-3 py-2">
            Title &amp; description will be pulled live from {trackerName} <span className="font-mono text-blue-300">{issueKey.trim()}</span> — nothing to type here. The link URL is derived automatically.
          </div>
        ) : reviewing ? (
          // A review ticket already has a name - the branch - and a description of its own would be
          // us writing the summary of somebody else's change before reading it. Rename it from the
          // drawer later if the branch name turns out to be a poor label.
          <div className="text-xs text-gray-400 bg-gray-900/60 border border-hair rounded px-3 py-2">
            Named after the branch. Link the {trackerName} issue above if there is one — its
            acceptance criteria are what the review judges the change against.
          </div>
        ) : (
          <>
            <Labeled label="Title">
              <input autoFocus value={title} onChange={e => setTitle(e.target.value)} className={inputCls} placeholder="Short summary of the work" />
            </Labeled>
            <Labeled label="Description">
              <textarea value={description} onChange={e => setDescription(e.target.value)} rows={3} className={inputCls} placeholder="Optional details / context" />
            </Labeled>
          </>
        )}

        <Labeled label="Master notes (optional) — your instructions outrank the description">
          <textarea
            value={masterNotes} onChange={e => setMasterNotes(e.target.value)} rows={3}
            className={inputCls}
            placeholder="Standing instructions for Claude on this ticket. Only you can edit these; every phase reads them and they win over the description."
          />
        </Labeled>

        <div className="border-t border-hair pt-3">
          <div className="text-xs text-gray-500 mb-2">Pipeline options</div>
          <TicketOptions
            speculation={specReview}
            track={track}
            implementation={implReview}
            skipQaTesting={skipQa}
            skipClassroomAnalysis={skipAnalysis}
            onChange={o => {
              if (o.requiresSpeculationReview !== undefined) setSpecReview(o.requiresSpeculationReview);
              if (o.requiresImplementationReview !== undefined) setImplReview(o.requiresImplementationReview);
              if (o.skipQaTesting !== undefined) setSkipQa(o.skipQaTesting);
              if (o.skipClassroomAnalysis !== undefined) setSkipAnalysis(o.skipClassroomAnalysis);
            }}
          />
        </div>

        <div className="flex justify-end gap-2 pt-2">
          <button onClick={dismiss} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200">Cancel</button>
          <button onClick={submit} disabled={busy} className="btn-act px-4 py-2 bg-blue-500 text-void disabled:bg-gray-700 disabled:text-gray-500">
            {busy ? 'Creating…' : 'Create'}
          </button>
        </div>
      </div>
    </div>
  );
}

const inputCls = 'w-full bg-gray-900 border border-hair rounded px-3 py-2 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500';

function Labeled({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-xs text-gray-500 mb-1">{label}</label>
      {children}
    </div>
  );
}
