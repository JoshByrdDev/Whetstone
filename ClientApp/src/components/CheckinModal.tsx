import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';
import { getPrPreflight, getCheckin, stageCheckin, abortCheckin, recordCheckin } from '../api/git';
import type {
  PrPreflight, CheckinStatus, CheckinFile, CheckinRecordResult, GitChange, TfvcState, DiskMismatch,
} from '../api/git';
import { DiffReviewList, StatusMark } from './DiffReviewList';
import { PrimaryButton, GhostButton } from './ui';
import { TfvcSyncModal } from './TfvcSyncModal';
import { Banner, Cmd, PathText, type Tone } from './checkinParts';

/**
 * Deliver a ticket on a project whose real source control is TFVC: the PR dialog's counterpart.
 *
 * There is no remote and no pull request. The ticket's branch is merged into the hub WITHOUT
 * committing, the human checks the result in from Team Explorer, and only then is the merge
 * committed to git with the changeset number. So the dialog is three steps with a person in the
 * middle - Stage, check in (outside this app), Record - and the checklist between them.
 *
 * The checklist exists because of the one step that loses work: TFVC picks up an edited file by
 * itself, but a new file, a deleted one and both halves of a rename sit under Detected Changes
 * until someone promotes them. It is a reminder, though, not the check. The check is Record, which
 * asks TFVC about exactly these files and refuses - naming each one - while anything is still
 * pending or detected. That refusal is what this dialog is for.
 */
export function CheckinModal({ ticketId, onClose }: { ticketId: number; onClose: () => void }) {
  const { fx, dismiss } = useVeil(onClose);
  const [pre, setPre] = useState<PrPreflight | null>(null);
  const [st, setSt] = useState<CheckinStatus | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'stage' | 'abort' | 'record' | 'refresh' | null>(null);
  // What the last button did, next to the state it changed rather than in the error line.
  const [note, setNote] = useState<{ tone: Tone; text: string; lines?: string[] } | null>(null);
  const [changeset, setChangeset] = useState('');
  const [recorded, setRecorded] = useState<CheckinRecordResult | null>(null);
  const [tab, setTab] = useState<'checklist' | 'diff' | 'commits'>('checklist');
  // The project-level sync, opened from here because this is where a Get Latest's leftovers block
  // staging and show up after a record. It sits over this dialog, so Escape belongs to it while open.
  const [syncOpen, setSyncOpen] = useState(false);

  // Which load the answers on the wire belong to - a refresh can outrun the one before it.
  const run = useRef(0);

  /** The hub and TFVC's view of it. Re-read after every step; tf is asked only once staged. */
  async function loadStatus() {
    const mine = ++run.current;
    try {
      const s = await getCheckin(ticketId);
      if (mine !== run.current) return;
      setSt(s);
      setError(s.ok ? null : s.error ?? 'Could not read the check-in state.');
    } catch (e) {
      if (mine === run.current) setError(e instanceof Error ? e.message : 'Could not read the check-in state.');
    }
  }

  useEffect(() => {
    let live = true;
    setLoading(true);
    // The diff is the branch against the base and does not move while the dialog is open, so it is
    // read once. The status is what every button changes.
    Promise.allSettled([getPrPreflight(ticketId), loadStatus()]).then(([p]) => {
      if (!live) return;
      if (p.status === 'fulfilled') setPre(p.value.preflight);
      setLoading(false);
    });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ticketId]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape' && !syncOpen) dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss, syncOpen]);

  const [pinned, setPinned] = useState<Set<string>>(new Set());
  const togglePin = useCallback((path: string) => {
    setPinned(prev => {
      const next = new Set(prev);
      if (!next.delete(path)) next.add(path);
      return next;
    });
  }, []);

  async function refresh() {
    setBusy('refresh');
    try { await loadStatus(); } finally { setBusy(null); }
  }

  async function stage() {
    setBusy('stage'); setNote(null);
    try {
      const r = await stageCheckin(ticketId);
      if (!r.ok) {
        setNote({
          tone: 'bad',
          text: r.error ?? 'Could not stage the merge.',
          lines: r.conflicted ? r.conflictedFiles : r.blocking?.map(c => `${c.status || '??'}  ${c.path}`),
        });
      } else {
        setNote({
          tone: 'good',
          text: 'Staged. The ticket\'s files are in the hub, uncommitted, and TFVC can see them. Check them in from ' +
            'Team Explorer - promoting every new, deleted and renamed file below - then record the changeset.',
        });
      }
      await loadStatus();
    } catch (e) {
      setNote({ tone: 'bad', text: e instanceof Error ? e.message : 'Could not stage the merge.' });
    } finally {
      setBusy(null);
    }
  }

  async function abort() {
    setBusy('abort'); setNote(null);
    try {
      const r = await abortCheckin(ticketId);
      setNote(r.ok
        ? { tone: 'good', text: 'Aborted. The hub is back on the base as it was before staging.' }
        : { tone: 'bad', text: r.error ?? 'Could not abort the merge.' });
      await loadStatus();
    } catch (e) {
      setNote({ tone: 'bad', text: e instanceof Error ? e.message : 'Could not abort the merge.' });
    } finally {
      setBusy(null);
    }
  }

  async function record() {
    // Team Explorer shows it as "Changeset 12345"; the Azure DevOps web UI as "C12345". Take either.
    const n = Number(changeset.trim().replace(/^c/i, ''));
    if (!Number.isInteger(n) || n <= 0) {
      setNote({ tone: 'bad', text: 'Enter the changeset number Team Explorer reported for this check-in.' });
      return;
    }
    setBusy('record'); setNote(null);
    try {
      const r = await recordCheckin(ticketId, n);
      if (!r.ok) setNote({ tone: 'bad', text: r.error ?? 'Not recorded.', lines: r.notCheckedIn });
      else setRecorded(r);
      await loadStatus();
    } catch (e) {
      setNote({ tone: 'bad', text: e instanceof Error ? e.message : 'Not recorded.' });
    } finally {
      setBusy(null);
    }
  }

  const ok = st?.ok === true ? st : null;
  const staged = ok?.merge === 'this';
  const canStage = !!ok && !recorded && ok.merge === 'none' && ok.onBase && ok.dirty.length === 0 && busy === null;
  const tfvcFiles = ok?.files.filter(inTfvc) ?? [];

  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div className="orbit-modal relative w-full max-w-4xl max-h-[88vh] flex flex-col overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between gap-3 px-5 py-3 border-b border-hair bg-panel">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-gray-200 uppercase tracking-wider">Check in to TFVC</h2>
            {(ok ?? pre) && (
              <div className="mt-0.5 text-[11px] font-mono text-gray-500 truncate">
                <span className="text-sky-300">{(ok ?? pre)!.branch}</span>
                <span className="mx-1.5">→</span>
                <span className="text-gray-400">{(ok ?? pre)!.baseBranch}</span>
                <span className="ml-2 text-gray-600">{(ok ?? pre)!.repoPath}</span>
              </div>
            )}
          </div>
          <div className="flex items-center gap-2">
            <GhostButton onClick={refresh} disabled={loading || busy !== null} title="Read the hub and ask TFVC again">
              {busy === 'refresh' ? 'Asking…' : 'Refresh'}
            </GhostButton>
            <button onClick={dismiss} className="text-gray-500 hover:text-gray-200 text-sm px-1 transition-colors">✕</button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4 space-y-3">
          {loading && !st && <div className="text-xs text-gray-500 py-8 text-center">Reading the branch and the hub…</div>}
          {error && <Banner tone="bad">{error}</Banner>}

          {recorded && <Recorded r={recorded} base={ok?.baseBranch ?? 'main'} onSync={() => setSyncOpen(true)} />}

          {ok && !recorded && (
            <div className="space-y-1.5">
              <HubState st={ok} busy={busy} onAbort={abort} onSync={() => setSyncOpen(true)} />
            </div>
          )}

          {note && (
            <div key={note.text} className="orbit-swap">
              <Banner tone={note.tone}>
                {note.text}
                {note.lines && note.lines.length > 0 && (
                  <ul className="mt-1.5 space-y-0.5 list-disc pl-4">
                    {note.lines.map(l => <li key={l} className="font-mono text-[10px] opacity-90 break-all">{l}</li>)}
                  </ul>
                )}
              </Banner>
            </div>
          )}

          {ok && (
            <div className="pt-1">
              <div className="flex items-center gap-3 border-b border-hair">
                <Tab active={tab === 'checklist'} onClick={() => setTab('checklist')}>
                  Check-in list <span className="text-gray-600">{tfvcFiles.length}</span>
                </Tab>
                <Tab active={tab === 'diff'} onClick={() => setTab('diff')}>
                  Files <span className="text-gray-600">{pre?.files.length ?? '…'}</span>
                </Tab>
                <Tab active={tab === 'commits'} onClick={() => setTab('commits')}>
                  Commits <span className="text-gray-600">{pre?.commits.length ?? '…'}</span>
                </Tab>
              </div>

              <div key={tab} className="orbit-swap">
                {tab === 'checklist' ? (
                  <Checklist st={ok} />
                ) : tab === 'commits' ? (
                  <div className="py-2">
                    {!pre && <div className="text-[11px] text-gray-600 py-2">Reading the branch…</div>}
                    {pre?.commits.length === 0 && <div className="text-[11px] text-gray-600 py-2">No commits on this branch.</div>}
                    {pre?.commits.map(c => (
                      <div key={c.sha} className="flex items-baseline gap-2.5 py-1 text-xs border-b border-hairsoft last:border-0">
                        <span className="font-mono text-[10px] text-amber-500 flex-none">{c.sha}</span>
                        <span className="text-gray-300 truncate flex-1">{c.subject}</span>
                        <span className="text-[10px] text-gray-600 flex-none">{c.author} · {c.date}</span>
                      </div>
                    ))}
                  </div>
                ) : pre ? (
                  <DiffReviewList
                    files={pre.files}
                    truncated={pre.patchTruncated}
                    pinned={pinned}
                    onTogglePin={togglePin}
                    // Staging is what puts the ticket's content in the hub's working tree - which is
                    // also the TFVC workspace, so it is the copy the human is about to check in.
                    // Before that the hub is still on the base and the branch is only in the
                    // agent's worktree.
                    root={staged ? pre.repoPath : pre.worktreePath || pre.repoPath}
                    ideUrl={pre.ideUrl}
                  />
                ) : (
                  <div className="text-[11px] text-gray-600 py-3">Reading the branch…</div>
                )}
              </div>
            </div>
          )}
        </div>

        {/* Footer: one primary action, and it is whichever step comes next. */}
        <div className="flex items-center justify-between gap-3 px-5 py-3 border-t border-hair bg-panel">
          <div className="text-[11px] text-gray-500 min-w-0">
            {recorded ? (
              <span className="text-emerald-400">Recorded. git {ok?.baseBranch ?? 'main'} now matches the check-in.</span>
            ) : staged ? (
              <span>Refused while TFVC still has anything pending or detected for these files.</span>
            ) : ok?.merge === 'none' ? (
              <span>Merges without committing. Nothing is committed until TFVC confirms the check-in.</span>
            ) : null}
          </div>
          <div className="flex items-center gap-2 flex-none">
            <button onClick={dismiss} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200 transition-colors">
              {recorded ? 'Close' : 'Cancel'}
            </button>
            {!recorded && staged && (
              <>
                <input
                  value={changeset}
                  onChange={e => setChangeset(e.target.value)}
                  onKeyDown={e => { if (e.key === 'Enter' && busy === null) record(); }}
                  inputMode="numeric"
                  placeholder="Changeset"
                  aria-label="Changeset number"
                  className="w-28 bg-gray-900 border border-hair rounded px-2.5 py-1.5 text-sm font-mono text-gray-200 focus:outline-none focus:border-gray-500"
                />
                <PrimaryButton onClick={record} disabled={busy !== null || changeset.trim().length === 0}>
                  {busy === 'record' ? 'Asking TFVC…' : 'Record check-in'}
                </PrimaryButton>
              </>
            )}
            {!recorded && !staged && (
              <PrimaryButton onClick={stage} disabled={!canStage}>
                {busy === 'stage' ? 'Staging…' : 'Stage in hub'}
              </PrimaryButton>
            )}
          </div>
        </div>
      </div>
      {syncOpen && ok && (
        <TfvcSyncModal project={ok.project} onClose={() => setSyncOpen(false)} onSynced={() => { loadStatus(); }} />
      )}
    </div>,
    document.body,
  );
}

// ---- the hub ----

/** Where the hub stands, and the one thing to do about it. */
function HubState({ st, busy, onAbort, onSync }: {
  st: CheckinStatus; busy: string | null; onAbort: () => void; onSync: () => void;
}) {
  if (st.merge === 'other') {
    return (
      <Banner tone="bad">
        <b>Another merge is in progress in the hub</b> (MERGE_HEAD {st.mergeHead?.slice(0, 7)}), and it is not this
        ticket's. Finish or abort it first.
        <Cmd>{`git -C "${st.repoPath}" merge --abort`}</Cmd>
      </Banner>
    );
  }
  if (!st.onBase) {
    return (
      <Banner tone="bad">
        The hub is on <b className="font-mono">{st.hubBranch || 'a detached HEAD'}</b>, not{' '}
        <span className="font-mono">{st.baseBranch}</span>. A check-in merges onto the base, so put the hub back on it first.
        <Cmd>{`git -C "${st.repoPath}" checkout ${st.baseBranch}`}</Cmd>
      </Banner>
    );
  }

  if (st.merge === 'none') {
    return (
      <>
        <Banner tone="wait">
          <b>1 · Stage in the hub.</b> Merges <span className="font-mono">{st.branch}</span> into{' '}
          <span className="font-mono">{st.baseBranch}</span> without committing, so the TFVC workspace sees exactly this
          ticket's files as changed. <b>2 ·</b> Check them in from Team Explorer. <b>3 ·</b> Record the changeset here.
          <Cmd>{`git -C "${st.repoPath}" merge --no-ff --no-commit ${st.branch}`}</Cmd>
        </Banner>
        {st.dirty.length > 0 && (
          <Banner tone="warn">
            <b>{st.dirty.length} tracked file{st.dirty.length === 1 ? ' is' : 's are'} modified in the hub</b>, so staging
            will refuse: the merge has to go onto a clean {st.baseBranch}, or the check-in carries more than this ticket.
            What a Get Latest brought in belongs in a sync commit; your own local edits can be stashed.
            <ChangeList changes={st.dirty} />
            <div className="mt-2">
              <GhostButton onClick={onSync} disabled={busy !== null} title="Commit what a Get Latest brought in, leaving your own edits alone">
                Sync with TFVC…
              </GhostButton>
            </div>
          </Banner>
        )}
      </>
    );
  }

  // Staged: this ticket's merge is sitting in the hub, waiting on Team Explorer.
  const pending = st.files.filter(f => inTfvc(f) && worst(f) === 'pending').length;
  const detected = st.files.filter(f => inTfvc(f) && worst(f) === 'detected').length;
  const offDisk = st.files.filter(f => (f.inTfvc && f.disk) || (f.renamedFromInTfvc && f.renamedFromDisk)).length;
  return (
    <>
      <Banner tone="good">
        <b>Staged in the hub.</b> Check the files below in from Team Explorer - promoting every one TFVC lists as
        detected - then enter the changeset number and record it.
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <GhostButton tone="danger" onClick={onAbort} disabled={busy !== null}
            title="git merge --abort - only before checking in">
            {busy === 'abort' ? 'Aborting…' : 'Abort merge'}
          </GhostButton>
          <span className="text-[10px] text-emerald-400/80">
            Only before the check-in. Afterwards it would put the old files back, and TFVC would offer that as edits.
          </span>
        </div>
      </Banner>

      {offDisk > 0 && (
        <Banner tone="bad">
          <b>{offDisk} file{offDisk === 1 ? ' in this merge does' : 's in this merge do'} not match the disk</b> - a new
          file that is gone, or a deleted one that is back. TFVC checks in the disk and says nothing about a file it never
          saw, so it cannot hold these as git is about to record them. Recording refuses until they match.
        </Banner>
      )}

      {st.tfvcError ? (
        <Banner tone="bad"><b>Could not ask TFVC.</b> {st.tfvcError}</Banner>
      ) : st.tfvcProblems.length > 0 ? (
        <Banner tone="warn">
          <b>TFVC said something this could not read</b>, so recording will refuse until it can.
          <ul className="mt-1.5 space-y-0.5 list-disc pl-4">
            {st.tfvcProblems.map(p => <li key={p} className="font-mono text-[10px] break-all">{p}</li>)}
          </ul>
        </Banner>
      ) : st.tfvcChecked && pending + detected > 0 ? (
        <Banner tone="warn">
          TFVC: <b>{pending}</b> pending to check in, <b>{detected}</b> still only detected - promote those in Team Explorer.
        </Banner>
      ) : st.tfvcChecked && offDisk === 0 && (
        // Only when the disk agrees too: silence from TFVC is not "checked in" for a file it never saw.
        <Banner tone="good">
          TFVC has nothing pending or detected for this ticket's files - it looks checked in. Record the changeset.
        </Banner>
      )}

      {st.stagedBehind > 0 && (
        <Banner tone="warn">
          The branch has <b>{st.stagedBehind} commit{st.stagedBehind === 1 ? '' : 's'}</b> since this was staged. They are not
          in this check-in - abort and stage again to include them.
        </Banner>
      )}
      {st.unmerged.length > 0 && (
        <Banner tone="bad">
          <b>{st.unmerged.length} file{st.unmerged.length === 1 ? '' : 's'} still unmerged.</b>
          <ul className="mt-1.5 space-y-0.5">{st.unmerged.map(p => <li key={p} className="font-mono text-[10px]">{p}</li>)}</ul>
        </Banner>
      )}
      {st.dirty.length > 0 && (
        <Banner tone="warn">
          <b>{st.dirty.length} tracked file{st.dirty.length === 1 ? ' has' : 's have'} changes outside this merge.</b>{' '}
          TFVC will list them as pending edits too - leave them out of the check-in. They stay out of the record commit either way.
          <ChangeList changes={st.dirty} />
        </Banner>
      )}
    </>
  );
}

// ---- the checklist ----

const GROUPS: { status: CheckinFile['status']; title: string; hint: string; accent: string }[] = [
  { status: 'added', title: 'New', hint: 'only under Detected Changes - promote each one, or the server never gets it', accent: 'text-emerald-300' },
  { status: 'deleted', title: 'Deleted', hint: 'only under Detected Changes - promote the delete, or the server keeps the file', accent: 'text-red-300' },
  { status: 'renamed', title: 'Renamed', hint: 'TFVC sees a delete and an add - promote both halves', accent: 'text-sky-300' },
  { status: 'modified', title: 'Edited', hint: 'TFVC picks these up by itself', accent: 'text-gray-300' },
];

/**
 * The ticket's files, grouped by what the human has to do about each in Team Explorer. The hand
 * work goes first: an edit rides along on its own, and the three kinds above it are the ones that
 * get missed.
 */
function Checklist({ st }: { st: CheckinStatus }) {
  const tfvc = st.files.filter(inTfvc);
  const gitOnly = st.files.filter(f => !inTfvc(f));

  if (st.files.length === 0) return <div className="text-[11px] text-gray-600 py-3">No file changes.</div>;

  return (
    <div className="py-2 space-y-3">
      {!st.tfvcChecked && tfvc.length > 0 && (
        <div className="text-[11px] text-gray-500">
          Stage the merge and TFVC's view of each file appears here - pending, or still waiting to be promoted.
        </div>
      )}
      {GROUPS.map(g => {
        const files = tfvc.filter(f => f.status === g.status);
        if (files.length === 0) return null;
        return (
          <div key={g.status}>
            <div className="flex items-baseline gap-2 pb-1">
              <span className={`text-[11px] font-bold tracking-wide ${g.accent}`}>{g.title}</span>
              <span className="text-[10px] font-mono text-gray-500">{files.length}</span>
              <span className="text-[10px] text-gray-600 truncate">— {g.hint}</span>
              <span className="flex-1 h-px bg-gray-800 self-center" />
            </div>
            <div className="border border-hairsoft rounded">
              {files.map(f => <FileRow key={f.path} f={f} checked={st.tfvcChecked} />)}
            </div>
          </div>
        );
      })}
      {gitOnly.length > 0 && (
        <div>
          <div className="flex items-baseline gap-2 pb-1">
            <span className="text-[11px] font-bold tracking-wide text-gray-500">Not in TFVC</span>
            <span className="text-[10px] font-mono text-gray-500">{gitOnly.length}</span>
            <span className="text-[10px] text-gray-600 truncate">
              — outside {st.tfvcFolders.length > 0 ? st.tfvcFolders.join(' and ') : 'the mapped folders'}; git-only, nothing to check in
            </span>
            <span className="flex-1 h-px bg-gray-800 self-center" />
          </div>
          <div className="border border-hairsoft rounded opacity-70">
            {gitOnly.map(f => <FileRow key={f.path} f={f} checked={false} />)}
          </div>
        </div>
      )}
    </div>
  );
}

function FileRow({ f, checked }: { f: CheckinFile; checked: boolean }) {
  return (
    <div className="px-2 py-1 border-b border-hairsoft last:border-0">
      <div className="flex items-center gap-2 text-[11px]">
        <StatusMark status={f.status} />
        <PathText path={f.path} />
        {f.inTfvc && f.disk && <DiskChip disk={f.disk} />}
        {checked && f.inTfvc && <TfvcChip state={f.tfvc} change={f.tfvcChange} />}
      </div>
      {f.renamedFrom && (
        <div className="flex items-center gap-2 pl-6 text-[10px] text-gray-600">
          <span className="flex-none">from</span>
          <PathText path={f.renamedFrom} dim />
          {f.renamedFromInTfvc && f.renamedFromDisk && <DiskChip disk={f.renamedFromDisk} />}
          {checked && f.renamedFromInTfvc && <TfvcChip state={f.renamedFromTfvc ?? 'unknown'} change={f.renamedFromTfvcChange} />}
        </div>
      )}
    </div>
  );
}

const CHIPS: Record<TfvcState, { cls: string; label: (change?: string | null) => string; title: string }> = {
  pending: {
    cls: 'text-emerald-300 border-emerald-900 bg-emerald-950/40',
    label: c => `pending ${c ?? ''}`.trim(),
    title: 'A pending change - it goes with the next check-in.',
  },
  detected: {
    cls: 'text-amber-300 border-amber-800 bg-amber-950/50',
    label: c => `detected ${c ?? ''} - promote`.replace('  ', ' '),
    title: 'Only under Detected Changes. Promote it in Team Explorer or the check-in leaves it behind.',
  },
  clean: {
    cls: 'text-gray-500 border-hairsoft bg-panel',
    label: () => 'clean',
    title: 'Nothing pending or detected - the server matches the disk.',
  },
  unknown: {
    cls: 'text-gray-500 border-hairsoft bg-panel',
    label: () => 'unread',
    title: 'TFVC\'s answer for this file could not be read in full.',
  },
  'not-in-tfvc': {
    cls: 'text-gray-600 border-hairsoft bg-panel',
    label: () => 'not in TFVC',
    title: 'Outside the mapped folders.',
  },
};

function DiskChip({ disk }: { disk: DiskMismatch }) {
  return (
    <span
      title={disk === 'missing'
        ? 'The merge has this file and the disk does not - TFVC cannot have checked it in.'
        : 'The merge deletes this file and the disk still has it - TFVC will not have checked the delete in.'}
      className="flex-none text-[9px] px-1.5 py-px rounded border whitespace-nowrap text-red-300 border-red-800 bg-red-950/50"
    >
      {disk === 'missing' ? 'not on disk' : 'still on disk'}
    </span>
  );
}

function TfvcChip({ state, change }: { state: TfvcState; change?: string | null }) {
  const chip = CHIPS[state] ?? CHIPS.unknown;
  return (
    <span title={chip.title} className={`flex-none text-[9px] px-1.5 py-px rounded border whitespace-nowrap ${chip.cls}`}>
      {chip.label(change)}
    </span>
  );
}

// ---- the outcome ----

function Recorded({ r, base, onSync }: { r: CheckinRecordResult; base: string; onSync: () => void }) {
  const left = r.leftUnstaged ?? [];
  const differ = r.ticketFilesDiffer ?? [];
  return (
    <div className="space-y-1.5">
      <Banner tone="good">
        <b>Recorded.</b> TFVC confirmed nothing is left pending, and the merge is committed in the hub
        {r.commitSha && <> as <span className="font-mono">{r.commitSha.slice(0, 7)}</span></>}.{' '}
        {r.movedToDone ? 'The ticket is Done.' : r.phaseNote}
        {r.commitSubject && <Cmd>{r.commitSubject}</Cmd>}
      </Banner>
      {differ.length > 0 && (
        <Banner tone="warn">
          <b>{differ.length} of this ticket's files differ from what git committed.</b> TFVC checks in the disk copy, so the
          server has changes this commit does not - they belong in the sync commit below.
          <ul className="mt-1.5 space-y-0.5">{differ.map(p => <li key={p} className="font-mono text-[10px]">{p}</li>)}</ul>
        </Banner>
      )}
      {left.length > 0 && (
        <Banner tone="warn">
          <b>{left.length} tracked file{left.length === 1 ? '' : 's'} still differ from {base}</b> - changes that reached the
          working tree but not git, normally from a Get Latest. git {base} is behind TFVC until they get a "Sync with TFVC"
          commit of their own.
          <ChangeList changes={left} />
          <div className="mt-2">
            <GhostButton onClick={onSync} title="Commit what a Get Latest brought in, leaving your own edits alone">
              Sync with TFVC…
            </GhostButton>
          </div>
        </Banner>
      )}
    </div>
  );
}

// ---- bits ----

/** Pending or detected on either half of a file - whichever needs more doing. */
function worst(f: CheckinFile): TfvcState {
  const states = [f.inTfvc ? f.tfvc : null, f.renamedFromInTfvc ? f.renamedFromTfvc : null];
  if (states.includes('detected')) return 'detected';
  if (states.includes('pending')) return 'pending';
  return f.tfvc;
}

/** A check-in item: TFVC holds this file, or the old half of it. */
function inTfvc(f: CheckinFile) {
  return f.inTfvc || (!!f.renamedFrom && f.renamedFromInTfvc);
}

function ChangeList({ changes }: { changes: GitChange[] }) {
  return (
    <div className="mt-1.5 max-h-32 overflow-auto font-mono text-[10px] space-y-0.5 opacity-90">
      {changes.slice(0, 40).map(c => (
        <div key={c.path} className="truncate"><span className="opacity-70">{c.status || '??'}</span> {c.path}</div>
      ))}
      {changes.length > 40 && <div className="opacity-70">+{changes.length - 40} more</div>}
    </div>
  );
}

function Tab({ active, onClick, children }: { active: boolean; onClick: () => void; children: React.ReactNode }) {
  return (
    <button
      onClick={onClick}
      className={`text-[11px] font-semibold pb-1.5 -mb-px border-b-2 transition-colors ${
        active ? 'text-gray-200 border-gray-400' : 'text-gray-500 border-transparent hover:text-gray-300'
      }`}
    >
      {children}
    </button>
  );
}

