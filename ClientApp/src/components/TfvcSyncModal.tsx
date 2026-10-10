import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';
import { getTfvcSync, runTfvcSync } from '../api/git';
import type { SyncFile, SyncSource, TfvcSyncPreview, TfvcSyncResult } from '../api/git';
import { StatusMark } from './DiffReviewList';
import { PrimaryButton, GhostButton } from './ui';
import { Banner, Cmd, PathText } from './checkinParts';

const GROUPS: { source: SyncSource; title: string; hint: string; accent: string }[] = [
  {
    source: 'tfvc', title: 'From TFVC', accent: 'text-emerald-300',
    hint: 'nothing pending or detected, so the disk is the server\'s - committed by this sync',
  },
  {
    source: 'pending', title: 'Your pending changes', accent: 'text-amber-300',
    hint: 'left alone - they reach git with your check-in, along with anything a Get Latest merged into them',
  },
  {
    source: 'detected', title: 'Local only', accent: 'text-sky-300',
    hint: 'left alone - TFVC sees these on disk and the server does not have them',
  },
  { source: 'git-only', title: 'Git-only', accent: 'text-gray-400', hint: 'left alone - outside the mapped folders' },
  { source: 'unknown', title: 'Could not tell', accent: 'text-red-300', hint: 'TFVC\'s answer was not readable - the sync will refuse' },
];

/**
 * Sync with TFVC: commit what a Get Latest brought into the hub, so git main matches the server.
 *
 * The list is the whole point of the dialog. TFVC can tell the server's changes from the human's
 * own - a file a Get Latest wrote has nothing pending - so the server's are committed and the rest
 * are shown as left alone, each with the reason. The commit names exactly the files listed here, and
 * the server refuses if the hub has moved since, so what is committed is always what was read.
 */
export function TfvcSyncModal({ project, onClose, onSynced }: {
  project: string;
  onClose: () => void;
  /** Called after a commit, so whatever opened this can re-read the hub. */
  onSynced?: () => void;
}) {
  const { fx, dismiss } = useVeil(onClose);
  const [pre, setPre] = useState<TfvcSyncPreview | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [note, setNote] = useState<string | null>(null);
  const [done, setDone] = useState<TfvcSyncResult | null>(null);
  const run = useRef(0);

  async function load() {
    const mine = ++run.current;
    setLoading(true); setError(null);
    try {
      const p = await getTfvcSync(project);
      if (mine !== run.current) return;
      setPre(p);
      setError(p.ok ? null : p.error ?? 'Could not read the hub.');
      setMessage(m => m || p.suggestedMessage);
    } catch (e) {
      if (mine === run.current) setError(e instanceof Error ? e.message : 'Could not read the hub.');
    } finally {
      if (mine === run.current) setLoading(false);
    }
  }

  useEffect(() => { load(); /* eslint-disable-next-line react-hooks/exhaustive-deps */ }, [project]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss]);

  const ok = pre?.ok ? pre : null;
  const toSync = ok?.files.filter(f => f.source === 'tfvc') ?? [];
  const unreadable = !!ok && (!!ok.tfvcError || ok.tfvcProblems.length > 0 || ok.files.some(f => f.source === 'unknown'));
  const canSync = !!ok && !done && !busy && !loading && !unreadable && toSync.length > 0;

  async function sync() {
    setBusy(true); setNote(null);
    try {
      const r = await runTfvcSync(project, toSync.map(f => f.path), message);
      if (!r.ok) {
        setNote(r.error ?? 'Nothing was committed.');
        if (r.changed) await load();
        return;
      }
      setDone(r);
      onSynced?.();
    } catch (e) {
      setNote(e instanceof Error ? e.message : 'Nothing was committed.');
    } finally {
      setBusy(false);
    }
  }

  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div className="orbit-modal relative w-full max-w-3xl max-h-[85vh] flex flex-col overflow-hidden">
        <div className="flex items-center justify-between gap-3 px-5 py-3 border-b border-hair bg-panel">
          <div className="min-w-0">
            <h2 className="text-sm font-semibold text-gray-200 uppercase tracking-wider">Sync with TFVC</h2>
            {pre && (
              <div className="mt-0.5 text-[11px] font-mono text-gray-500 truncate">
                <span className="text-gray-400">{pre.baseBranch}</span>
                <span className="ml-2 text-gray-600">{pre.repoPath}</span>
              </div>
            )}
          </div>
          <div className="flex items-center gap-2">
            <GhostButton onClick={load} disabled={loading || busy} title="Read the hub and ask TFVC again">
              {loading ? 'Asking…' : 'Refresh'}
            </GhostButton>
            <button onClick={dismiss} className="text-gray-500 hover:text-gray-200 text-sm px-1 transition-colors">✕</button>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4 space-y-3">
          {loading && !pre && <div className="text-xs text-gray-500 py-8 text-center">Reading the hub and asking TFVC…</div>}

          {!done && (
            <Banner tone="wait">
              A Get Latest writes other people's check-ins into the hub, where git sees them as uncommitted - so git{' '}
              <span className="font-mono">{pre?.baseBranch ?? 'main'}</span> falls behind TFVC. This commits the changes TFVC
              reports as the server's, and leaves your own work exactly where it is.
            </Banner>
          )}

          {error && <Banner tone="bad">{error}</Banner>}
          {ok?.tfvcError && <Banner tone="bad"><b>Could not ask TFVC.</b> {ok.tfvcError}</Banner>}
          {ok && ok.tfvcProblems.length > 0 && (
            <Banner tone="warn">
              <b>TFVC said something this could not read</b>, so it cannot tell the server's changes from yours. The sync will
              refuse until it can.
              <ul className="mt-1.5 space-y-0.5 list-disc pl-4">
                {ok.tfvcProblems.map(p => <li key={p} className="font-mono text-[10px] break-all">{p}</li>)}
              </ul>
            </Banner>
          )}

          {note && <div key={note} className="orbit-swap"><Banner tone="bad">{note}</Banner></div>}

          {done && (
            <Banner tone="good">
              <b>Synced.</b> Committed {done.committed} file{done.committed === 1 ? '' : 's'} to{' '}
              <span className="font-mono">{pre?.baseBranch ?? 'main'}</span>
              {done.commitSha && <> as <span className="font-mono">{done.commitSha.slice(0, 7)}</span></>} - git matches TFVC again
              for everything the server sent.
              {done.commitSubject && <Cmd>{done.commitSubject}</Cmd>}
            </Banner>
          )}

          {ok && ok.files.length === 0 && (
            <Banner tone="good">Nothing to sync - the hub has no uncommitted changes, so git and TFVC already agree.</Banner>
          )}

          {ok && ok.files.length > 0 && (
            <div key={done ? 'done' : 'list'} className="orbit-swap space-y-3">
              {GROUPS.map(g => {
                const files = ok.files.filter(f => f.source === g.source);
                if (files.length === 0) return null;
                return (
                  <div key={g.source}>
                    <div className="flex items-baseline gap-2 pb-1">
                      <span className={`text-[11px] font-bold tracking-wide ${g.accent}`}>{g.title}</span>
                      <span className="text-[10px] font-mono text-gray-500">{files.length}</span>
                      <span className="text-[10px] text-gray-600 truncate">— {g.hint}</span>
                      <span className="flex-1 h-px bg-gray-800 self-center" />
                    </div>
                    <div className={`border border-hairsoft rounded max-h-64 overflow-y-auto ${g.source === 'tfvc' ? '' : 'opacity-75'}`}>
                      {files.map(f => <Row key={f.path} f={f} />)}
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>

        <div className="flex items-center gap-3 px-5 py-3 border-t border-hair bg-panel">
          {!done ? (
            <input
              value={message}
              onChange={e => setMessage(e.target.value)}
              aria-label="Commit message"
              placeholder="Commit message"
              className="flex-1 min-w-0 bg-gray-900 border border-hair rounded px-2.5 py-1.5 text-xs font-mono text-gray-200 focus:outline-none focus:border-gray-500"
            />
          ) : <span className="flex-1" />}
          <button onClick={dismiss} className="px-3 py-1.5 text-sm text-gray-400 hover:text-gray-200 transition-colors flex-none">
            {done ? 'Close' : 'Cancel'}
          </button>
          {!done && (
            <PrimaryButton onClick={sync} disabled={!canSync}>
              {busy ? 'Committing…' : `Commit ${toSync.length} to ${ok?.baseBranch ?? 'main'}`}
            </PrimaryButton>
          )}
        </div>
      </div>
    </div>,
    document.body,
  );
}

function Row({ f }: { f: SyncFile }) {
  return (
    <div className="flex items-center gap-2 px-2 py-1 text-[11px] border-b border-hairsoft last:border-0">
      <StatusMark status={f.git} />
      <PathText path={f.path} />
      {f.tfvcChange && (
        <span className="flex-none text-[9px] px-1.5 py-px rounded border whitespace-nowrap text-gray-400 border-hairsoft bg-panel">
          {f.source === 'detected' ? `detected ${f.tfvcChange}` : `pending ${f.tfvcChange}`}
        </span>
      )}
    </div>
  );
}
