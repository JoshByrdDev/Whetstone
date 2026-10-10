import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';
import { getPrPreflight, getPrBaseCheck, createPr, mergeTicket, rebaseTicket, forcePushTicket } from '../api/git';
import type { PrPreflight, PrBaseCheck, PrCreateResult, MergeResult } from '../api/git';
import { DiffReviewList } from './DiffReviewList';

/**
 * Raise the PR for a ticket's branch.
 *
 * Three things in order, because that is the order they matter in: is this branch fit to raise,
 * what is actually on it, and only then the button that pushes.
 *
 * The base check is the reason this is a dialog rather than a button. A branch that forked a week
 * ago and has not seen main since produces a PR whose diff is half other people's reverts, and the
 * only moment anyone will look is now — so being behind disables the primary action and makes the
 * user opt past it deliberately rather than discovering it in review.
 *
 * Each of those two complaints carries the button that answers it, because being told a branch is
 * stale and then having to go and fix it elsewhere is how it ends up raised stale instead. Both are
 * fenced server-side: the rebase rolls itself back on the first conflict, and the force push goes
 * out with a lease. Neither can take a decision this dialog has not shown the user first.
 *
 * It loads in two halves, because they cost wildly different amounts. What is on the branch is a
 * handful of local git reads; whether it is fit to raise means fetching from origin, and that one
 * round trip was most of the wait. So the branch is drawn as soon as it is read and the base check
 * lands into it after — but nothing may ACT on the half that has not arrived. Until it does, the
 * banner says it is still asking and the create button stays shut: the whole reason this is a
 * dialog is that it refuses to raise a PR off a stale answer, and "no answer yet" is stale.
 *
 * On a project that does not use pull requests (`mergeInsteadOfPr`) the same dialog ends in a merge
 * instead: the branch is merged into the base in the hub with a merge commit, and nothing is pushed.
 * Being behind the base stops being a reason to refuse - a merge commit is exactly how that is
 * reconciled - and origin's copy of the branch stops mattering, because nothing is sent there.
 */
export function PrModal({ ticketId, onClose }: { ticketId: number; onClose: () => void }) {
  // The row that opened this unmounts it on close, so the dialog holds its own exit open long
  // enough to be seen. Every way out - escape, the void, ✕, Cancel - goes through `dismiss`.
  const { fx, dismiss } = useVeil(onClose);
  const [pre, setPre] = useState<PrPreflight | null>(null);
  const [title, setTitle] = useState('');
  const [description, setDescription] = useState('');
  const [loading, setLoading] = useState(true);
  // The base check runs alongside the read above and outlives it. Tracked separately because it
  // gates the create button, and the button must not open the moment the diff appears.
  const [baseChecking, setBaseChecking] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<PrCreateResult | null>(null);
  // Set from the preflight: this project merges into its base rather than raising a PR.
  const [merge, setMerge] = useState(false);
  const [merged, setMerged] = useState<MergeResult | null>(null);
  // Opting past a stale base. Reset on every re-check so a fresh answer is never pre-overridden.
  const [override, setOverride] = useState(false);
  const [tab, setTab] = useState<'diff' | 'commits'>('diff');
  // The rebase and force-push buttons. One at a time, and their outcome lives next to the banner
  // that offered them rather than in the PR's own error line — they are not the same story.
  const [gitBusy, setGitBusy] = useState<'rebase' | 'force' | null>(null);
  const [gitMsg, setGitMsg] = useState<{ tone: 'good' | 'bad'; text: string; files?: string[] } | null>(null);
  const [confirmForce, setConfirmForce] = useState(false);

  // Which load the answers on the wire belong to. Two requests per load, either of which can be
  // outrun by a re-check or a switch of ticket, and a late reply landing on a newer branch's
  // numbers would be the one kind of wrong this dialog cannot afford.
  const run = useRef(0);

  async function load(keepText = false) {
    const mine = ++run.current;
    setLoading(true); setBaseChecking(true); setError(null);
    setOverride(false); setGitMsg(null); setConfirmForce(false);

    // Both at once. The first is local reads and comes back in well under a second; the second
    // fetches from origin and is the wait this split exists to get off the critical path.
    const local = getPrPreflight(ticketId);
    const base = getPrBaseCheck(ticketId).catch(e => e instanceof Error ? e : new Error('The base check failed.'));

    let known: PrPreflight | null = null;
    try {
      const r = await local;
      if (mine !== run.current) return;
      known = r.preflight;
      setPre(r.preflight);
      setMerge(r.mergeInsteadOfPr === true);
      if (!keepText) {
        setTitle(r.suggestedTitle ?? '');
        setDescription(r.suggestedDescription ?? '');
      }
      if (!r.preflight.ok) setError(r.preflight.error ?? 'Preflight failed.');
    } catch (e) {
      if (mine !== run.current) return;
      setError(e instanceof Error ? e.message : 'Preflight failed.');
    } finally {
      if (mine === run.current) setLoading(false);
    }

    const b = await base;
    if (mine !== run.current) return;
    try {
      if (b instanceof Error) throw b;
      if (!b.ok) throw new Error(b.error ?? 'Could not check the base.');
      setPre(prev => (prev && prev.ok ? withBase(prev, b) : prev));

      // The fetch moved the fork point: commits it pulled in are no longer this branch's own, so
      // the diff on screen was measured from a base older than where the branch actually forked
      // and is showing other people's work as if it were this ticket's. Read it again.
      if (known?.ok && b.ahead !== known.ahead) {
        const again = await getPrPreflight(ticketId);
        if (mine !== run.current) return;
        if (again.preflight.ok) setPre(withBase(again.preflight, b));
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not check the base.');
    } finally {
      if (mine === run.current) setBaseChecking(false);
    }
  }

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [ticketId]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss]);

  // Files the user starred. Overrides the automatic ranking, which is the point: the scorer is a
  // good guess about this repo in general and the person reading is the authority on this ticket.
  // Deliberately not persisted — it is a bookmark for one sitting, not a property of the file.
  const [pinned, setPinned] = useState<Set<string>>(new Set());
  const togglePin = useCallback((path: string) => {
    setPinned(prev => {
      const next = new Set(prev);
      if (!next.delete(path)) next.add(path);
      return next;
    });
  }, []);

  async function submit() {
    if (merge) return submitMerge();
    setBusy(true); setError(null);
    try {
      const r = await createPr(ticketId, title.trim(), description);
      setResult(r);
      if (!r.ok) setError(r.error ?? 'Could not raise the PR.');
      // The host's own form is where the PR is actually confirmed. Opening it is the natural end of
      // the flow; the link stays on screen because a popup blocker gets a vote here.
      else if (r.url && r.mode === 'web') window.open(r.url, '_blank', 'noopener');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not raise the PR.');
    } finally {
      setBusy(false);
    }
  }

  async function submitMerge() {
    setBusy(true); setError(null);
    try {
      const r = await mergeTicket(ticketId, title.trim(), description);
      setMerged(r);
      if (!r.ok) setError(r.error ?? 'Could not merge.');
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not merge.');
    } finally {
      setBusy(false);
    }
  }

  /**
   * Rebase onto the latest base, if it is free. A conflict comes back as a refusal with the files
   * that clashed and nothing moved, which is the answer — the resolution itself is a judgement about
   * the code, and it belongs to whoever wrote it.
   */
  async function rebase() {
    setGitBusy('rebase'); setGitMsg(null); setError(null);
    try {
      const r = await rebaseTicket(ticketId);
      if (!r.ok) {
        setGitBusy(null);
        setGitMsg({ tone: 'bad', text: r.error ?? 'The rebase failed.', files: r.conflictedFiles });
        return;
      }
      // Re-read before saying so: every number on screen was about the old tip. The typed title and
      // description are the user's own and survive the refresh.
      await load(true);
      setGitMsg({
        tone: 'good',
        text: `Rebased onto ${r.baseRef}. It was at ${r.previousSha?.slice(0, 7)} — ` +
          `git -C "${r.ranIn}" reset --hard ${r.previousSha} puts it back.`,
      });
    } catch (e) {
      setGitMsg({ tone: 'bad', text: e instanceof Error ? e.message : 'The rebase failed.' });
    } finally {
      setGitBusy(null);
    }
  }

  /** Replace origin with this branch. Two presses, because it is the one button here that destroys. */
  async function forcePush() {
    setGitBusy('force'); setGitMsg(null); setError(null);
    try {
      const r = await forcePushTicket(ticketId);
      if (!r.ok) {
        setGitBusy(null);
        setGitMsg({ tone: 'bad', text: r.error ?? 'The force push failed.' });
        return;
      }
      await load(true);
      setGitMsg({ tone: 'good', text: `origin/${r.branch} now matches this branch, at ${r.sha?.slice(0, 7)}.` });
    } catch (e) {
      setGitMsg({ tone: 'bad', text: e instanceof Error ? e.message : 'The force push failed.' });
    } finally {
      setGitBusy(null);
    }
  }

  // The base answer, once there is one. Every judgement below reads it through this rather than off
  // `pre`, so a number that has not been checked yet cannot be mistaken for one that has.
  const checked = pre?.ok === true && pre.baseChecked ? pre : null;
  const nothingToRaise = checked !== null && checked.ahead === 0;
  const stale = checked !== null && checked.behind > 0;
  // origin holds commits this branch does not: the two histories parted, which is what a rebase
  // leaves behind and the only case a plain push cannot fix. A merge pushes nothing, so it is moot.
  const diverged = !merge && checked !== null && checked.remoteBranchExists && checked.remoteAhead > 0;
  const gitting = gitBusy !== null;
  const done = merge ? merged?.ok === true : result?.ok === true;
  const canSubmit = checked !== null && !nothingToRaise && !busy && !gitting && !done && (merge || !stale || override);

  // Through the body, not in place: the button that opens this lives in the row's hover panel, and
  // that panel is a clipped, opacity-driven box — a dialog rendered inside it gets cropped to the
  // card and fades out with the hover. A transformed ancestor would break `fixed` here too.
  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div className="orbit-modal relative w-full max-w-4xl max-h-[88vh] flex flex-col overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between gap-3 px-5 py-3 border-b border-hair bg-panel">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-gray-200 uppercase tracking-wider">{merge ? 'Merge' : 'Pull request'}</h2>
            {pre && (
              <div className="mt-0.5 text-[11px] font-mono text-gray-500 truncate">
                <span className="text-sky-300">{pre.branch}</span>
                <span className="mx-1.5">→</span>
                <span className="text-gray-400">{pre.baseBranch}</span>
                {pre.provider !== 'none' && <span className="ml-2 text-gray-600">{providerLabel(pre.provider)}</span>}
              </div>
            )}
          </div>
          <div className="flex items-center gap-2">
            <button
              onClick={() => load(true)}
              disabled={loading || baseChecking || busy || gitting}
              className="text-[11px] font-semibold text-gray-400 hover:text-gray-200 px-2.5 py-1 rounded border border-hair hover:border-hair disabled:opacity-40 transition-colors"
              title="Fetch from origin and re-check"
            >
              {loading || baseChecking ? 'Checking…' : 'Re-check'}
            </button>
            <button onClick={dismiss} className="text-gray-500 hover:text-gray-200 text-sm px-1">✕</button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4 space-y-3">
          {loading && !pre && <div className="text-xs text-gray-500 py-8 text-center">Reading the branch…</div>}

          {error && (
            <div className="text-xs text-red-300 bg-red-950/40 border border-red-900 rounded px-3 py-2">{error}</div>
          )}

          {pre?.ok && (
            <>
              {/* ---- Is this branch fit to raise? ---- */}
              <div className="space-y-1.5">
                {!pre.baseChecked ? (
                  <Banner tone="wait">
                    Fetching <span className="font-mono">origin/{pre.baseBranch}</span> to see whether this branch
                    is based off the latest of it. Everything below is already read from the branch itself.
                  </Banner>
                ) : stale ? (
                  <Banner tone={merge ? 'warn' : 'bad'}>
                    <b>{pre.behind} commit{pre.behind === 1 ? '' : 's'} behind {pre.baseRef}.</b>{' '}
                    {merge ? (
                      <>The merge commit will reconcile that. Rebase first if you would rather {pre.baseBranch}'s history stay a straight line.</>
                    ) : (
                      <>
                        This branch is not based off the latest {pre.baseBranch} — rebase it before raising, or the
                        PR will carry a diff that is partly other people's work.
                      </>
                    )}
                    <div className="mt-2 flex flex-wrap items-center gap-2">
                      <Action tone={merge ? 'warn' : 'bad'} onClick={rebase} disabled={loading || busy || gitting}>
                        {gitBusy === 'rebase' ? 'Rebasing…' : `Rebase onto ${pre.baseRef}`}
                      </Action>
                      <span className="text-[10px] text-red-400/80">
                        Conflict-free only — anything that clashes is rolled back untouched.
                      </span>
                    </div>
                    <Cmd>{`git -C "${pre.repoPath}" rebase ${pre.baseRef} ${pre.branch}`}</Cmd>
                  </Banner>
                ) : (
                  <Banner tone="good">
                    Based off the latest <span className="font-mono">{pre.baseRef}</span> — nothing to rebase.
                  </Banner>
                )}

                {gitMsg && (
                  <Banner tone={gitMsg.tone}>
                    {gitMsg.text}
                    {gitMsg.files && gitMsg.files.length > 0 && (
                      <div className="mt-1.5 space-y-0.5">
                        {gitMsg.files.map(f => (
                          <div key={f} className="font-mono text-[10px] opacity-90 break-all">{f}</div>
                        ))}
                      </div>
                    )}
                  </Banner>
                )}

                {pre.baseChecked && !pre.fetched && (
                  <Banner tone="warn">
                    Could not reach origin, so this was compared against the local{' '}
                    <span className="font-mono">{pre.baseRef}</span> — it may be stale.
                    {pre.fetchError && <div className="mt-1 font-mono text-[10px] opacity-80">{pre.fetchError}</div>}
                  </Banner>
                )}

                {nothingToRaise && (
                  <Banner tone="warn">This branch has no commits that {pre.baseRef} does not already have.</Banner>
                )}

                {pre.worktreeDirtyFiles > 0 && (
                  <Banner tone="warn">
                    <b>{pre.worktreeDirtyFiles} uncommitted file{pre.worktreeDirtyFiles === 1 ? '' : 's'}</b> in the
                    agent's worktree. They are not committed, so they will <b>not</b> be in this PR.
                    {pre.worktreePath && <div className="mt-1 font-mono text-[10px] opacity-80">{pre.worktreePath}</div>}
                  </Banner>
                )}

                {diverged && (
                  <Banner tone="warn">
                    <b>
                      <span className="font-mono">origin/{pre.branch}</span> has {pre.remoteAhead} commit
                      {pre.remoteAhead === 1 ? '' : 's'} this branch does not.
                    </b>{' '}
                    The two histories have parted — normally because this branch was rebased after being pushed, so
                    what is on origin is the same work at its old commits. An ordinary push is rejected from here;
                    forcing replaces origin with this branch.
                    <div className="mt-2 flex flex-wrap items-center gap-2">
                      {confirmForce ? (
                        <>
                          <Action tone="warn" onClick={forcePush} disabled={loading || busy || gitting}>
                            {gitBusy === 'force'
                              ? 'Force pushing…'
                              : `Yes — overwrite origin/${pre.branch}`}
                          </Action>
                          <button
                            onClick={() => setConfirmForce(false)}
                            disabled={gitting}
                            className="text-[11px] text-amber-400/80 hover:text-amber-300 underline disabled:opacity-40"
                          >
                            Cancel
                          </button>
                        </>
                      ) : (
                        <Action tone="warn" onClick={() => setConfirmForce(true)} disabled={loading || busy || gitting}>
                          Force push to origin
                        </Action>
                      )}
                      <span className="text-[10px] text-amber-400/80">
                        Refused if origin moved since this dialog last fetched.
                      </span>
                    </div>
                    <Cmd>{`git -C "${pre.repoPath}" push --force-with-lease origin ${pre.branch}`}</Cmd>
                  </Banner>
                )}

                {!merge && checked !== null && checked.remoteBranchExists && !checked.remoteUpToDate && !diverged && (
                  <Banner tone="warn">
                    <span className="font-mono">origin/{pre.branch}</span> exists but is behind your local branch —
                    creating the PR pushes the rest.
                  </Banner>
                )}
              </div>

              {/* ---- What the PR will say ---- */}
              <div className="grid gap-2.5 pt-1">
                <label className="block">
                  <span className="block text-[11px] text-gray-500 mb-1">{merge ? 'Merge commit subject' : 'Title'}</span>
                  <input
                    value={title}
                    onChange={e => setTitle(e.target.value)}
                    className="w-full bg-gray-900 border border-hair rounded px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-gray-500"
                  />
                </label>
                <label className="block">
                  <span className="flex items-baseline justify-between text-[11px] text-gray-500 mb-1">
                    <span>{merge ? 'Merge commit body' : 'Description'}</span>
                    <span className="text-gray-600">{merge ? 'Drafted from the ticket' : 'Markdown · drafted from the ticket'}</span>
                  </span>
                  <textarea
                    value={description}
                    onChange={e => setDescription(e.target.value)}
                    rows={12}
                    className="w-full bg-gray-900 border border-hair rounded px-3 py-2 text-xs text-gray-300 font-mono leading-relaxed focus:outline-none focus:border-gray-500"
                  />
                </label>
              </div>

              {/* ---- What is on it ---- */}
              <div className="pt-1">
                <div className="flex items-center gap-3 border-b border-hair">
                  <Tab active={tab === 'diff'} onClick={() => setTab('diff')}>
                    Files <span className="text-gray-600">{pre.files.length}</span>
                  </Tab>
                  <Tab active={tab === 'commits'} onClick={() => setTab('commits')}>
                    Commits <span className="text-gray-600">{pre.commits.length}</span>
                  </Tab>
                  <span className="flex-1" />
                  <span className="text-[10px] font-mono text-gray-600 pb-1.5">
                    <span className="text-emerald-500">+{pre.files.reduce((s, f) => s + f.added, 0)}</span>{' '}
                    <span className="text-red-500">−{pre.files.reduce((s, f) => s + f.removed, 0)}</span>
                  </span>
                </div>

                {tab === 'commits' ? (
                  <div className="py-2">
                    {pre.commits.length === 0 && <div className="text-[11px] text-gray-600 py-2">No commits on this branch.</div>}
                    {pre.commits.map(c => (
                      <div key={c.sha} className="flex items-baseline gap-2.5 py-1 text-xs border-b border-hairsoft last:border-0">
                        <span className="font-mono text-[10px] text-amber-500 flex-none">{c.sha}</span>
                        <span className="text-gray-300 truncate flex-1">{c.subject}</span>
                        <span className="text-[10px] text-gray-600 flex-none">{c.author} · {c.date}</span>
                      </div>
                    ))}
                  </div>
                ) : (
                  <DiffReviewList
                    files={pre.files}
                    truncated={pre.patchTruncated}
                    pinned={pinned}
                    onTogglePin={togglePin}
                    // The branch is checked out in the agent's worktree, so that is the copy these
                    // files are in. The hub is on some other branch entirely — it is worth opening
                    // only once the worktree has been reaped.
                    root={pre.worktreePath || pre.repoPath}
                    ideUrl={pre.ideUrl}
                  />
                )}
              </div>
            </>
          )}

          {/* ---- Outcome ---- */}
          {merged?.ok && (
            <div className="rounded border border-emerald-800 bg-emerald-950/40 px-3 py-2.5 text-xs text-emerald-200 space-y-1.5">
              <div>
                Merged <span className="font-mono">{merged.branch}</span> into{' '}
                <span className="font-mono">{merged.baseBranch}</span> at{' '}
                <span className="font-mono">{merged.mergeSha?.slice(0, 7)}</span>
                {merged.fastForwardedBase && <>, after bringing {merged.baseBranch} up to origin</>}.
                {merged.movedToDone && ' The ticket is Done.'}
              </div>
              {merged.phaseNote && <div className="text-amber-300">{merged.phaseNote}</div>}
              {merged.hasOrigin && merged.aheadOfOrigin > 0 && (
                <>
                  <div className="text-emerald-300/80">
                    Nothing was pushed — {merged.baseBranch} is {merged.aheadOfOrigin} commit
                    {merged.aheadOfOrigin === 1 ? '' : 's'} ahead of origin. Publish it when ready:
                  </div>
                  <Cmd>{`git -C "${merged.repoPath}" push origin ${merged.baseBranch}`}</Cmd>
                </>
              )}
            </div>
          )}
          {merged && !merged.ok && merged.conflictedFiles.length > 0 && (
            <div className="space-y-0.5">
              {merged.conflictedFiles.map(f => (
                <div key={f} className="font-mono text-[10px] text-red-300/90 break-all">{f}</div>
              ))}
            </div>
          )}
          {merged && merged.output.length > 0 && (
            <pre className="text-[10px] font-mono text-gray-500 bg-black/40 border border-hair rounded px-2.5 py-2 max-h-32 overflow-auto whitespace-pre-wrap">
              {merged.output.join('\n')}
            </pre>
          )}
          {result?.ok && (
            <div className="rounded border border-emerald-800 bg-emerald-950/40 px-3 py-2.5 text-xs text-emerald-200 space-y-1.5">
              <div>
                {result.mode === 'gh'
                  ? 'Pull request created.'
                  : result.mode === 'web'
                    ? 'Branch pushed. Finish creating the PR on the page that opened:'
                    : 'Branch pushed. This host has no PR flow wired up here — raise it manually.'}
              </div>
              {result.url && (
                <a href={result.url} target="_blank" rel="noreferrer" className="block font-mono text-[11px] text-emerald-300 underline break-all">
                  {result.url}
                </a>
              )}
            </div>
          )}
          {result && result.output.length > 0 && (
            <pre className="text-[10px] font-mono text-gray-500 bg-black/40 border border-hair rounded px-2.5 py-2 max-h-32 overflow-auto whitespace-pre-wrap">
              {result.output.join('\n')}
            </pre>
          )}
        </div>

        {/* Footer */}
        <div className="flex items-center justify-between gap-3 px-5 py-3 border-t border-hair bg-panel">
          <div className="text-[11px] text-gray-500 min-w-0">
            {!merge && stale && !override && (
              <button onClick={() => setOverride(true)} className="text-amber-400 hover:text-amber-300 underline">
                Raise it anyway, without rebasing
              </button>
            )}
            {!merge && stale && override && <span className="text-amber-400">Raising without rebasing.</span>}
            {/* The button is shut until origin has answered, so say that rather than leaving a
                greyed-out button with no reason beside it. */}
            {pre?.ok && !checked && !done && (
              <span>Waiting on origin before this can be {merge ? 'merged' : 'raised'}…</span>
            )}
            {merge && checked && !done && (
              <span>Merges into {pre?.baseBranch} in your repo with a merge commit. Nothing is pushed.</span>
            )}
            {!merge && !stale && checked && !result?.ok && (
              diverged ? (
                // Raising would push, and a push against a diverged remote is simply rejected. Say so
                // here rather than letting the button find out.
                <span className="text-amber-400">Force push first — an ordinary push will be rejected.</span>
              ) : (
                <span>
                  {checked.remoteUpToDate ? 'Already pushed — ' : 'Pushes the branch, then '}
                  {checked.provider === 'github' ? 'creates the PR on GitHub.' : 'opens the prefilled PR form.'}
                </span>
              )
            )}
          </div>
          <div className="flex items-center gap-2 flex-none">
            <button onClick={dismiss} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200">
              {done ? 'Close' : 'Cancel'}
            </button>
            <button
              onClick={submit}
              disabled={!canSubmit}
              className="btn-act px-4 py-2 bg-emerald-500 text-void disabled:bg-gray-800 disabled:text-gray-600"
            >
              {merge
                ? (busy ? 'Merging…' : done ? 'Done' : `Merge into ${pre?.baseBranch ?? 'base'}`)
                : (busy ? 'Pushing…' : done ? 'Done' : 'Create pull request')}
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}

// ---- bits ----

/**
 * The base check landing on the branch read that arrived first. Every field it owns replaces the
 * provisional one of the same name, and `baseChecked` is what turns the dialog's judgements on.
 */
function withBase(pre: PrPreflight, b: PrBaseCheck): PrPreflight {
  return {
    ...pre,
    baseChecked: true,
    fetched: b.fetched,
    fetchError: b.fetchError,
    baseRef: b.baseRef,
    behind: b.behind,
    ahead: b.ahead,
    remoteBranchExists: b.remoteBranchExists,
    remoteUpToDate: b.remoteUpToDate,
    remoteAhead: b.remoteAhead,
    upToDateWithBase: b.behind === 0,
  };
}

function providerLabel(p: PrPreflight['provider']) {
  return p === 'github' ? 'GitHub' : p === 'azure-devops' ? 'Azure DevOps' : p === 'other' ? 'origin' : 'no remote';
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

const TONES = {
  good: 'border-emerald-900 bg-emerald-950/30 text-emerald-300',
  warn: 'border-amber-900 bg-amber-950/30 text-amber-300',
  bad: 'border-red-900 bg-red-950/30 text-red-300',
  // Not a verdict yet. Deliberately the quietest of the four: it sits where the green "nothing to
  // rebase" will be, and reading as good news for the second before the answer arrives is the one
  // thing it must not do.
  wait: 'border-hair bg-gray-900/40 text-gray-400',
};

function Banner({ tone, children }: { tone: keyof typeof TONES; children: React.ReactNode }) {
  return <div className={`text-xs leading-relaxed rounded border px-3 py-2 ${TONES[tone]}`}>{children}</div>;
}

const ACTION_TONES = {
  good: 'border-emerald-700 bg-emerald-900/50 text-emerald-200 hover:bg-emerald-800/60',
  warn: 'border-amber-700 bg-amber-900/50 text-amber-200 hover:bg-amber-800/60',
  bad: 'border-red-800 bg-red-900/50 text-red-200 hover:bg-red-800/60',
};

/** The button that fixes what the banner is complaining about, wearing the banner's own colour. */
function Action({
  tone, onClick, disabled, children,
}: {
  tone: keyof typeof ACTION_TONES;
  onClick: () => void;
  disabled?: boolean;
  children: React.ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      className={`text-[11px] font-semibold px-2.5 py-1 rounded border transition-colors disabled:opacity-40 disabled:cursor-not-allowed ${ACTION_TONES[tone]}`}
    >
      {children}
    </button>
  );
}

function Cmd({ children }: { children: string }) {
  return (
    <div className="mt-1.5 font-mono text-[10px] text-gray-400 bg-black/50 rounded px-2 py-1 select-all break-all">
      {children}
    </div>
  );
}

