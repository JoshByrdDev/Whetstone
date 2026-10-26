import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';
import { getPrPreflight, getPrBaseCheck, createPr, rebaseTicket, forcePushTicket } from '../api/git';
import type { PrPreflight, PrCreateResult, RepoPreflight } from '../api/git';
import { DiffReviewList } from './DiffReviewList';
import { Action, Banner, Cmd, Tab, providerLabel, withBase } from './PrModal';

/**
 * Raise the pull requests for a ticket on a project of several repos (docs/multi-repo-plan.md).
 *
 * One PR per repo the ticket touched, all from the same branch name, and each judged on its own:
 * every repo has its own base, its own fetch, its own "behind", and a rebase or force push in one
 * repo says nothing about another. So this is PrModal's sections, once per touched repo, under one
 * shared title - each with its own description, because each names the other repos the change is
 * in. The repos the ticket left alone are listed, not drawn: there is nothing in them to raise.
 *
 * Everything PrModal refuses to act on before origin has answered, this refuses per repo, and
 * "Create all" raises only the repos that would each have let their own button through.
 *
 * Kept apart from PrModal rather than folded into it so that the dialog every project of one repo
 * uses is the same code it was. A project that merges locally or checks in to TFVC is never one of
 * several - the server refuses both - so this dialog only ever ends in pull requests.
 */
export function MultiRepoPrModal({ ticketId, onClose }: { ticketId: number; onClose: () => void }) {
  const { fx, dismiss } = useVeil(onClose);
  const [repos, setRepos] = useState<RepoPreflight[]>([]);
  const [sections, setSections] = useState<Record<string, Section>>({});
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [showLeft, setShowLeft] = useState(false);
  const [creatingAll, setCreatingAll] = useState(false);
  const run = useRef(0);

  const patch = useCallback((dir: string, change: Partial<Section> | ((s: Section) => Partial<Section>)) => {
    setSections(prev => {
      const s = prev[dir];
      if (!s) return prev;
      return { ...prev, [dir]: { ...s, ...(typeof change === 'function' ? change(s) : change) } };
    });
  }, []);

  /** One repo's base check, landing on whatever branch read that repo has by then. */
  async function checkBase(dir: string, mine: number, known: PrPreflight) {
    try {
      const b = await getPrBaseCheck(ticketId, dir);
      if (mine !== run.current) return;
      if (!b.ok) throw new Error(b.error ?? 'Could not check the base.');
      // The fetch moved this repo's fork point, so its diff was measured from the wrong place.
      if (known.ok && b.ahead !== known.ahead) {
        const again = await getPrPreflight(ticketId);
        if (mine !== run.current) return;
        const fresh = again.repos?.find(r => r.dir === dir)?.preflight;
        patch(dir, s => ({ pre: withBase(fresh?.ok ? fresh : s.pre, b) }));
      }
      else patch(dir, s => ({ pre: withBase(s.pre, b) }));
    } catch (e) {
      if (mine !== run.current) return;
      patch(dir, { baseError: e instanceof Error ? e.message : 'Could not check the base.' });
    } finally {
      if (mine === run.current) patch(dir, { checking: false });
    }
  }

  async function load(keepText = false) {
    const mine = ++run.current;
    setLoading(true); setError(null);
    try {
      const r = await getPrPreflight(ticketId);
      if (mine !== run.current) return;
      const list = r.repos ?? [];
      setRepos(list);
      if (!keepText) setTitle(r.suggestedTitle ?? '');
      setSections(prev => {
        const next: Record<string, Section> = {};
        list.forEach((repo, i) => {
          if (!repo.touched || !repo.preflight) return;
          const old = prev[repo.dir];
          next[repo.dir] = {
            pre: repo.preflight,
            description: keepText && old ? old.description : repo.suggestedDescription ?? '',
            checking: repo.preflight.ok,
            baseError: repo.preflight.ok ? null : repo.preflight.error ?? 'Preflight failed.',
            // The first touched repo opens on its files; the others wait to be asked.
            tab: old?.tab ?? (list.findIndex(x => x.touched && x.preflight) === i ? 'diff' : null),
            pinned: old?.pinned ?? new Set(),
            gitBusy: null, gitMsg: null, confirmForce: false, override: false,
            busy: false, result: keepText && old ? old.result : null, error: null,
          };
        });
        return next;
      });
      for (const repo of list)
        if (repo.touched && repo.preflight?.ok) void checkBase(repo.dir, mine, repo.preflight);
    } catch (e) {
      if (mine !== run.current) return;
      setError(e instanceof Error ? e.message : 'Preflight failed.');
    } finally {
      if (mine === run.current) setLoading(false);
    }
  }

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [ticketId]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss]);

  async function create(dir: string): Promise<PrCreateResult | null> {
    const s = sections[dir];
    if (!s) return null;
    patch(dir, { busy: true, error: null });
    try {
      const r = await createPr(ticketId, title.trim(), s.description, dir);
      // A refusal is not always a create result: a QA copy's 403 and a 400 for a repo that is not
      // the ticket's carry only an error. Keep the result only when it is one, so nothing below
      // reads a transcript that was never sent.
      patch(dir, r.ok === true
        ? { result: r, error: null }
        : { result: Array.isArray(r.output) ? r : null, error: r.error ?? 'Could not raise the PR.' });
      return r;
    } catch (e) {
      patch(dir, { error: e instanceof Error ? e.message : 'Could not raise the PR.' });
      return null;
    } finally {
      patch(dir, { busy: false });
    }
  }

  async function createOne(dir: string) {
    const r = await create(dir);
    if (r?.ok && r.url && r.mode === 'web') window.open(r.url, '_blank', 'noopener');
  }

  /**
   * Every repo that is ready, in config order. The host's forms are opened only for the first: a
   * browser lets one window through per click and quietly blocks the rest, so each repo's link
   * stays in its own section for the others.
   */
  async function createAll() {
    setCreatingAll(true);
    let opened = false;
    for (const repo of repos) {
      const s = sections[repo.dir];
      if (!s || !canCreate(s)) continue;
      const r = await create(repo.dir);
      if (!opened && r?.ok && r.url && r.mode === 'web') { window.open(r.url, '_blank', 'noopener'); opened = true; }
    }
    setCreatingAll(false);
  }

  async function rebase(dir: string) {
    patch(dir, { gitBusy: 'rebase', gitMsg: null });
    try {
      const r = await rebaseTicket(ticketId, dir);
      if (!r.ok) {
        patch(dir, { gitMsg: { tone: 'bad', text: r.error ?? 'The rebase failed.', files: r.conflictedFiles } });
        return;
      }
      await load(true);
      patch(dir, {
        gitMsg: {
          tone: 'good',
          text: `Rebased onto ${r.baseRef}. It was at ${r.previousSha?.slice(0, 7)} - ` +
            `git -C "${r.ranIn}" reset --hard ${r.previousSha} puts it back.`,
        },
      });
    } catch (e) {
      patch(dir, { gitMsg: { tone: 'bad', text: e instanceof Error ? e.message : 'The rebase failed.' } });
    } finally {
      patch(dir, { gitBusy: null });
    }
  }

  async function forcePush(dir: string) {
    patch(dir, { gitBusy: 'force', gitMsg: null });
    try {
      const r = await forcePushTicket(ticketId, dir);
      if (!r.ok) {
        patch(dir, { gitMsg: { tone: 'bad', text: r.error ?? 'The force push failed.' } });
        return;
      }
      await load(true);
      patch(dir, { gitMsg: { tone: 'good', text: `origin/${r.branch} now matches this branch, at ${r.sha?.slice(0, 7)}.` } });
    } catch (e) {
      patch(dir, { gitMsg: { tone: 'bad', text: e instanceof Error ? e.message : 'The force push failed.' } });
    } finally {
      patch(dir, { gitBusy: null });
    }
  }

  const touched = repos.filter(r => r.touched && sections[r.dir]);
  const left = repos.filter(r => !r.touched || !sections[r.dir]);
  const ready = touched.filter(r => canCreate(sections[r.dir]));
  const anyChecking = touched.some(r => sections[r.dir].checking);
  const allDone = touched.length > 0 && touched.every(r => sections[r.dir].result?.ok);
  const branch = touched[0] ? sections[touched[0].dir].pre.branch : null;

  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4 max-sm:p-2" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div className="orbit-modal relative w-full max-w-4xl max-h-[88vh] flex flex-col overflow-hidden">
        {/* Header */}
        <div className="flex items-center justify-between gap-3 px-5 max-sm:px-3 py-3 border-b border-hair bg-panel">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-gray-200 uppercase tracking-wider">Pull requests</h2>
            <div className="mt-0.5 text-[11px] text-gray-500 truncate">
              {branch && <span className="font-mono text-sky-300">{branch}</span>}
              {repos.length > 0 && (
                <span className="ml-2">
                  changed in {touched.length} of {repos.length} repos
                </span>
              )}
            </div>
          </div>
          <div className="flex items-center gap-2 flex-none">
            <button
              onClick={() => load(true)}
              disabled={loading || anyChecking || creatingAll}
              className="orbit-tap text-[11px] font-semibold text-gray-400 hover:text-gray-200 px-2.5 py-1 rounded border border-hair disabled:opacity-40"
              title="Fetch from origin and re-check every repo"
            >
              {loading || anyChecking ? 'Checking…' : 'Re-check'}
            </button>
            <button onClick={dismiss} className="text-gray-500 hover:text-gray-200 transition-colors text-sm px-1">✕</button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto px-5 max-sm:px-3 py-4 space-y-4">
          {loading && repos.length === 0 && <div className="text-xs text-gray-500 py-8 text-center">Reading the branch in each repo…</div>}
          {error && <div className="text-xs text-red-300 bg-red-950/40 border border-red-900 rounded px-3 py-2">{error}</div>}
          {!loading && !error && repos.length > 0 && touched.length === 0 && (
            <Banner tone="warn">This ticket has not changed any of its repos, so there is nothing to raise.</Banner>
          )}

          {touched.length > 0 && (
            <label className="block">
              <span className="block text-[11px] text-gray-500 mb-1">Title, for every pull request</span>
              <input
                value={title}
                onChange={e => setTitle(e.target.value)}
                className="w-full min-w-0 bg-gray-900 border border-hair rounded px-3 py-2 text-sm text-gray-200 focus:outline-none focus:border-gray-500"
              />
            </label>
          )}

          {touched.map(repo => (
            <RepoSection
              key={repo.dir}
              dir={repo.dir}
              s={sections[repo.dir]}
              lockAll={loading || creatingAll}
              onDescription={text => patch(repo.dir, { description: text })}
              onTab={tab => patch(repo.dir, s => ({ tab: s.tab === tab ? null : tab }))}
              onTogglePin={path => patch(repo.dir, s => {
                const next = new Set(s.pinned);
                if (!next.delete(path)) next.add(path);
                return { pinned: next };
              })}
              onRebase={() => rebase(repo.dir)}
              onForce={() => forcePush(repo.dir)}
              onConfirmForce={v => patch(repo.dir, { confirmForce: v })}
              onOverride={() => patch(repo.dir, { override: true })}
              onCreate={() => createOne(repo.dir)}
            />
          ))}

          {left.length > 0 && (
            <div className="border-t border-hair pt-3">
              <button
                onClick={() => setShowLeft(v => !v)}
                className="orbit-tap text-[11px] font-semibold text-gray-500 hover:text-gray-300 px-2 py-1 rounded border border-hair"
                aria-expanded={showLeft}
              >
                Not changed <span className="text-gray-600">{left.length}</span> {showLeft ? '▾' : '▸'}
              </button>
              <div key={showLeft ? 'open' : 'shut'} className="orbit-swap">
                {showLeft && (
                  <div className="mt-2 space-y-1">
                    {left.map(r => (
                      <div key={r.dir} className="flex items-baseline gap-2 text-xs min-w-0">
                        <span className="font-mono text-gray-400 truncate">{r.dir}</span>
                        <span className="text-[10px] text-gray-600 flex-none">
                          {r.readOnly ? 'read-only - never delivered' : 'nothing on the branch - no pull request'}
                        </span>
                      </div>
                    ))}
                  </div>
                )}
              </div>
            </div>
          )}
        </div>

        {/* Footer */}
        <div className="flex flex-wrap items-center justify-between gap-3 px-5 max-sm:px-3 py-3 border-t border-hair bg-panel">
          <div className="text-[11px] text-gray-500 min-w-0 flex-1">
            {anyChecking && <span>Waiting on origin in every repo before any of them can be raised…</span>}
            {!anyChecking && touched.length > 0 && !allDone && (
              <span>
                {ready.length} of {touched.length} ready. Each is pushed, then opens its prefilled PR form.
              </span>
            )}
            {allDone && <span className="text-emerald-400">Every repo the ticket changed has its pull request.</span>}
          </div>
          <div className="flex items-center gap-2 flex-none">
            <button onClick={dismiss} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200 transition-colors">
              {allDone ? 'Close' : 'Cancel'}
            </button>
            <button
              onClick={createAll}
              disabled={ready.length === 0 || creatingAll || anyChecking}
              className="btn-act px-4 py-2 bg-emerald-500 text-void disabled:bg-gray-800 disabled:text-gray-600"
            >
              {creatingAll ? 'Pushing…' : allDone ? 'Done' : ready.length === touched.length ? 'Create all' : `Create ${ready.length} ready`}
            </button>
          </div>
        </div>
      </div>
    </div>,
    document.body,
  );
}

// ---- one repo ----

interface Section {
  pre: PrPreflight;
  description: string;
  checking: boolean;
  baseError: string | null;
  tab: 'diff' | 'commits' | null;
  pinned: Set<string>;
  gitBusy: 'rebase' | 'force' | null;
  gitMsg: { tone: 'good' | 'bad'; text: string; files?: string[] } | null;
  confirmForce: boolean;
  override: boolean;
  busy: boolean;
  result: PrCreateResult | null;
  error: string | null;
}

/** PrModal's own gate, per repo: origin has answered, there is something to raise, and nothing says stop. */
function canCreate(s: Section): boolean {
  const checked = s.pre.ok && s.pre.baseChecked;
  if (!checked || s.pre.ahead === 0 || s.busy || s.gitBusy || s.result?.ok) return false;
  if (s.pre.remoteBranchExists && s.pre.remoteAhead > 0) return false;
  return s.pre.behind === 0 || s.override;
}

function RepoSection({
  dir, s, lockAll, onDescription, onTab, onTogglePin, onRebase, onForce, onConfirmForce, onOverride, onCreate,
}: {
  dir: string;
  s: Section;
  lockAll: boolean;
  onDescription: (text: string) => void;
  onTab: (tab: 'diff' | 'commits') => void;
  onTogglePin: (path: string) => void;
  onRebase: () => void;
  onForce: () => void;
  onConfirmForce: (v: boolean) => void;
  onOverride: () => void;
  onCreate: () => void;
}) {
  const { pre } = s;
  const checked = pre.ok && pre.baseChecked;
  const stale = checked && pre.behind > 0;
  const diverged = checked && pre.remoteBranchExists && pre.remoteAhead > 0;
  const busy = lockAll || s.busy || s.gitBusy !== null;

  const status = s.result?.ok ? { tone: 'text-emerald-400', text: s.result.mode === 'gh' ? 'PR created' : 'pushed' }
    : !pre.ok ? { tone: 'text-red-400', text: 'cannot read' }
    : !checked ? { tone: 'text-gray-500', text: 'checking…' }
    : diverged ? { tone: 'text-amber-400', text: 'diverged from origin' }
    : stale ? { tone: s.override ? 'text-amber-400' : 'text-red-400', text: `${pre.behind} behind` }
    : pre.ahead === 0 ? { tone: 'text-amber-400', text: 'nothing to raise' }
    : { tone: 'text-emerald-400', text: 'ready' };

  return (
    <section className="rounded border border-hair bg-gray-900/30">
      <div className="flex flex-wrap items-baseline gap-x-3 gap-y-0.5 px-3 py-2 border-b border-hairsoft">
        <span className="font-mono text-sm text-gray-200">{dir}</span>
        <span className="text-[11px] font-mono text-gray-500 min-w-0 truncate">
          <span className="text-emerald-500">+{pre.ahead}</span>
          <span className="mx-1.5">→</span>
          <span className="text-gray-400">{pre.baseBranch}</span>
          {pre.provider !== 'none' && <span className="ml-2 text-gray-600">{providerLabel(pre.provider)}</span>}
        </span>
        <span className="flex-1" />
        <span className={`text-[11px] font-semibold ${status.tone}`}>{status.text}</span>
      </div>

      <div className="px-3 py-2.5 space-y-2">
        {s.baseError && <Banner tone="bad">{s.baseError}</Banner>}

        {pre.ok && !pre.baseChecked && !s.baseError && (
          <Banner tone="wait">
            Fetching <span className="font-mono">origin/{pre.baseBranch}</span> for this repo.
          </Banner>
        )}

        {stale && (
          <Banner tone="bad">
            <b>{pre.behind} commit{pre.behind === 1 ? '' : 's'} behind {pre.baseRef}.</b>{' '}
            Rebase it before raising, or this repo's PR will carry other people's work.
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <Action tone="bad" onClick={onRebase} disabled={busy}>
                {s.gitBusy === 'rebase' ? 'Rebasing…' : `Rebase onto ${pre.baseRef}`}
              </Action>
              {!s.override && (
                <button onClick={onOverride} className="text-[11px] text-amber-400 hover:text-amber-300 underline">
                  Raise this one anyway
                </button>
              )}
            </div>
            <Cmd>{`git -C "${pre.repoPath}" rebase ${pre.baseRef} ${pre.branch}`}</Cmd>
          </Banner>
        )}
        {checked && !stale && !diverged && pre.ahead > 0 && !s.result?.ok && (
          <Banner tone="good">Based off the latest <span className="font-mono">{pre.baseRef}</span>.</Banner>
        )}

        {s.gitMsg && (
          <Banner tone={s.gitMsg.tone}>
            {s.gitMsg.text}
            {s.gitMsg.files?.map(f => <div key={f} className="mt-0.5 font-mono text-[10px] opacity-90 break-all">{f}</div>)}
          </Banner>
        )}

        {checked && !pre.fetched && (
          <Banner tone="warn">
            Could not reach this repo's origin, so it was compared against the local{' '}
            <span className="font-mono">{pre.baseRef}</span>.
            {pre.fetchError && <div className="mt-1 font-mono text-[10px] opacity-80">{pre.fetchError}</div>}
          </Banner>
        )}

        {pre.worktreeDirtyFiles > 0 && (
          <Banner tone="warn">
            <b>{pre.worktreeDirtyFiles} uncommitted file{pre.worktreeDirtyFiles === 1 ? '' : 's'}</b> in this
            repo's worktree. They will <b>not</b> be in its PR.
          </Banner>
        )}

        {diverged && (
          <Banner tone="warn">
            <b><span className="font-mono">origin/{pre.branch}</span> has {pre.remoteAhead} commit{pre.remoteAhead === 1 ? '' : 's'} this branch does not.</b>{' '}
            An ordinary push is rejected; forcing replaces origin with this branch.
            <div className="mt-2 flex flex-wrap items-center gap-2">
              {s.confirmForce ? (
                <>
                  <Action tone="warn" onClick={onForce} disabled={busy}>
                    {s.gitBusy === 'force' ? 'Force pushing…' : `Yes - overwrite origin/${pre.branch}`}
                  </Action>
                  <button onClick={() => onConfirmForce(false)} disabled={busy}
                    className="text-[11px] text-amber-400/80 hover:text-amber-300 underline disabled:opacity-40">
                    Cancel
                  </button>
                </>
              ) : (
                <Action tone="warn" onClick={() => onConfirmForce(true)} disabled={busy}>Force push to origin</Action>
              )}
            </div>
            <Cmd>{`git -C "${pre.repoPath}" push --force-with-lease origin ${pre.branch}`}</Cmd>
          </Banner>
        )}

        <label className="block">
          <span className="flex items-baseline justify-between text-[11px] text-gray-500 mb-1">
            <span>Description</span>
            <span className="text-gray-600">names the other repos</span>
          </span>
          <textarea
            value={s.description}
            onChange={e => onDescription(e.target.value)}
            rows={6}
            className="w-full min-w-0 bg-gray-900 border border-hair rounded px-3 py-2 text-xs text-gray-300 font-mono leading-relaxed focus:outline-none focus:border-gray-500"
          />
        </label>

        <div>
          <div className="flex items-center gap-3 border-b border-hair">
            <Tab active={s.tab === 'diff'} onClick={() => onTab('diff')}>
              Files <span className="text-gray-600">{pre.files.length}</span>
            </Tab>
            <Tab active={s.tab === 'commits'} onClick={() => onTab('commits')}>
              Commits <span className="text-gray-600">{pre.commits.length}</span>
            </Tab>
            <span className="flex-1" />
            <span className="text-[10px] font-mono text-gray-600 pb-1.5">
              <span className="text-emerald-500">+{pre.files.reduce((n, f) => n + f.added, 0)}</span>{' '}
              <span className="text-red-500">−{pre.files.reduce((n, f) => n + f.removed, 0)}</span>
            </span>
          </div>
          <div key={s.tab ?? 'none'} className="orbit-swap">
            {s.tab === 'commits' && (
              <div className="py-2">
                {pre.commits.map(c => (
                  <div key={c.sha} className="flex items-baseline gap-2.5 py-1 text-xs border-b border-hairsoft last:border-0 min-w-0">
                    <span className="font-mono text-[10px] text-amber-500 flex-none">{c.sha}</span>
                    <span className="text-gray-300 truncate flex-1 min-w-0">{c.subject}</span>
                    <span className="text-[10px] text-gray-600 flex-none max-sm:hidden">{c.author} · {c.date}</span>
                  </div>
                ))}
              </div>
            )}
            {s.tab === 'diff' && (
              <DiffReviewList
                files={pre.files}
                truncated={pre.patchTruncated}
                pinned={s.pinned}
                onTogglePin={onTogglePin}
                root={pre.worktreePath || pre.repoPath}
                ideUrl={pre.ideUrl}
              />
            )}
          </div>
        </div>

        {s.error && <div className="text-xs text-red-300 bg-red-950/40 border border-red-900 rounded px-3 py-2">{s.error}</div>}
        {s.result?.ok && (
          <div className="rounded border border-emerald-800 bg-emerald-950/40 px-3 py-2 text-xs text-emerald-200 space-y-1">
            <div>
              {s.result.mode === 'gh' ? 'Pull request created.'
                : s.result.mode === 'web' ? 'Branch pushed. Finish creating the PR on its form:'
                : 'Branch pushed. This host has no PR flow wired up here - raise it manually.'}
            </div>
            {s.result.url && (
              <a href={s.result.url} target="_blank" rel="noreferrer" className="block font-mono text-[11px] text-emerald-300 underline break-all">
                {s.result.url}
              </a>
            )}
          </div>
        )}
        {s.result && (s.result.output?.length ?? 0) > 0 && (
          <pre className="text-[10px] font-mono text-gray-500 bg-black/40 border border-hair rounded px-2.5 py-2 max-h-28 overflow-auto whitespace-pre-wrap">
            {s.result.output.join('\n')}
          </pre>
        )}

        {!s.result?.ok && (
          <div className="flex justify-end">
            <Action tone="good" onClick={onCreate} disabled={!canCreate(s) || lockAll}>
              {s.busy ? 'Pushing…' : `Create pull request in ${dir}`}
            </Action>
          </div>
        )}
      </div>
    </section>
  );
}
