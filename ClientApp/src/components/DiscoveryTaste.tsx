import { useEffect, useState } from 'react';
import * as api from '../api/discovery';
import type { TasteStatus, TasteVersion } from '../api/discovery';

/**
 * A project's taste file, in a discovery's drawer (docs/discovery-track-plan.md, step 4): what the
 * owner's verdicts have taught about what they want proposed, which version this discovery's runs
 * judged by, how near the next fold is, and every version before it.
 *
 * The file is the owner's to edit like any other file in the project folder, and every save, revert
 * and "fold at the next run" here is a REST route with no tool behind it. The fold itself is a
 * worker run's: the board starts no model, so asking for a fold means the next run on the project
 * does it.
 */

const SOURCE_LABEL: Record<TasteVersion['source'], string> = {
  Fold: 'fold',
  Owner: 'your edit',
  Revert: 'put back',
  Outside: 'changed in the folder',
};

const btn = 'orbit-tap text-[11px] px-2 py-1 rounded border border-hair text-gray-300 hover:border-gray-600 disabled:opacity-40';

function when(iso: string) {
  const d = new Date(iso);
  return d.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
}

export function DiscoveryTaste({ project, reads }: {
  project: string;
  /** The taste versions this discovery's sift and rechecks read, in order. */
  reads: { round: number; phase: string; versionNumber?: number | null }[];
}) {
  const [s, setS] = useState<TasteStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [shown, setShown] = useState<{ number: number; text: string } | null>(null);

  const load = () => api.getTaste(project).then(setS).catch(e => setError(e instanceof Error ? e.message : String(e)));
  useEffect(() => { load(); }, [project]); // eslint-disable-line react-hooks/exhaustive-deps

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try { await fn(); await load(); }
    catch (e) { setError(e instanceof Error ? e.message : 'That did not work.'); }
    finally { setBusy(false); }
  }

  if (!s) return error ? <p className="text-[12px] text-rose-300">{error}</p> : null;

  const f = s.fold;
  const readLine = reads.length === 0
    ? null
    : reads.map(r => `${r.phase === 'Rechecking' ? `recheck ${r.round}` : 'the sift'} read ${r.versionNumber ? `version ${r.versionNumber}` : 'no taste file'}`)
      .filter((x, i, a) => a.indexOf(x) === i).join(', ');
  const foldLine = s.verdictsEver === 0
    ? 'No verdicts yet, so there is nothing to fold. The file starts with the first fold, or with what you write here.'
    : f.claimedAt
    ? 'A fold is under way.'
    : f.due
    ? `A fold is due (${f.verdicts} verdict${f.verdicts === 1 ? '' : 's'}): the next worker run on this project folds them in.`
    : f.verdicts === 0
    ? 'Every verdict so far is folded in.'
    : `${f.verdicts} of ${f.every} verdicts toward the next fold.`;

  const startEdit = () => { setDraft(s.text); setEditing(true); };
  const save = () => run(async () => { await api.saveTaste(project, draft); setEditing(false); });
  const view = (v: TasteVersion) => run(async () => {
    const full = await api.getTasteVersion(project, v.id);
    setShown(shown?.number === full.number ? null : { number: full.number, text: full.text });
  });

  return (
    <div className="space-y-3 text-[12px]">
      <div className="space-y-1 text-gray-400 leading-snug">
        {readLine && <div className="text-gray-300">This discovery: {readLine}{s.current ? `. Now at version ${s.current.number}.` : '.'}</div>}
        <div>{foldLine}</div>
        {f.lastNote && <div className="text-[11px] text-gray-600">Last fold{f.lastAt ? `, ${when(f.lastAt)}` : ''}: {f.lastNote}</div>}
        {f.teachNothing > 0 && (
          <div className="text-[11px] text-gray-600">
            {f.teachNothing} more verdict{f.teachNothing === 1 ? '' : 's'} (not now, snoozed, already decided) teach nothing and are not counted.
          </div>
        )}
        {f.verdicts > 0 && !f.due && !f.claimedAt && (
          <button type="button" disabled={busy} onClick={() => run(() => api.requestFold(project, true))} className={btn}>
            Fold at the next run
          </button>
        )}
        {f.requested && f.verdicts > 0 && (
          <button type="button" disabled={busy} onClick={() => run(() => api.requestFold(project, false))}
            className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors">
            take back the early fold
          </button>
        )}
      </div>

      <div key={editing ? 'edit' : 'view'} className="orbit-swap">
        {editing ? (
          <div className="space-y-1.5">
            <textarea value={draft} onChange={e => setDraft(e.target.value)} rows={14} spellCheck
              className="w-full font-mono text-[11.5px] leading-snug bg-black/40 border border-hair rounded px-2 py-1.5 text-gray-200 focus:outline-none focus:border-gray-600" />
            <div className="text-[11px] text-gray-600 leading-snug">
              Write what you want under "The owner's": a fold never changes it. "Learned" is rewritten by each fold; edit it too if a lesson is wrong.
            </div>
            <div className="flex items-center gap-2">
              <button type="button" disabled={busy} onClick={save} className={btn}>Save</button>
              <button type="button" disabled={busy} onClick={() => setEditing(false)} className="text-[11px] text-gray-500 hover:text-gray-300 transition-colors">Cancel</button>
            </div>
          </div>
        ) : s.text ? (
          <div className="rounded-lg border border-hair bg-black/20 px-3 py-2.5">
            <pre className="whitespace-pre-wrap break-words font-mono text-[11.5px] leading-snug text-gray-300">{s.text}</pre>
            <div className="mt-2"><button type="button" disabled={busy} onClick={startEdit} className={btn}>Edit</button></div>
          </div>
        ) : (
          <button type="button" disabled={busy} onClick={() => { setDraft(''); setEditing(true); }} className={btn}>Write one now</button>
        )}
      </div>

      {s.suggestions && (
        <div className="rounded-lg border border-amber-800/50 bg-amber-950/10 px-3 py-2 space-y-1">
          <div className="text-[11px] text-amber-300">The last fold suggests a change to your section - yours to make or leave:</div>
          <pre className="whitespace-pre-wrap break-words font-mono text-[11.5px] leading-snug text-gray-300">{s.suggestions}</pre>
        </div>
      )}

      {s.alreadyDecided.length > 0 && (
        <div className="space-y-1">
          <div className="text-[11px] text-gray-500">Rejected as already decided - a decision the register may be missing, not a lesson:</div>
          <ul className="space-y-0.5">
            {s.alreadyDecided.map(a => (
              <li key={a.proposalId} className="flex items-baseline gap-2 min-w-0 text-gray-400">
                <span className="font-mono text-gray-600">#{a.discovery}</span>
                <span className="flex-1 min-w-0">{a.title}{a.note ? <span className="text-gray-600"> · {a.note}</span> : null}</span>
              </li>
            ))}
          </ul>
        </div>
      )}

      {!s.versioned && s.versions.length > 0 && (
        <div className="text-[11px] text-gray-600">The project folder is not a git repo, so versions are kept here only, not committed.</div>
      )}

      {s.versions.length > 0 && (
        <details>
          <summary className="cursor-pointer text-[11px] text-gray-500 hover:text-gray-300 transition-colors">Versions ({s.versions.length})</summary>
          <ul className="mt-2 space-y-1">
            {s.versions.map(v => {
              const current = s.current?.id === v.id;
              return (
                <li key={v.id} className="flex items-baseline gap-2 min-w-0 flex-wrap">
                  <span className={`font-mono ${current ? 'text-gray-200' : 'text-gray-500'}`}>v{v.number}</span>
                  <span className="text-gray-400 flex-1 min-w-0">
                    {SOURCE_LABEL[v.source]}
                    {v.source === 'Fold' ? ` · ${v.verdicts} verdict${v.verdicts === 1 ? '' : 's'}` : ''}
                    {v.reads > 0 ? ` · read by ${v.reads} run${v.reads === 1 ? '' : 's'}` : ''}
                    <span className="text-gray-600"> · {when(v.at)}{v.commit ? ` · ${v.commit.slice(0, 7)}` : ''}</span>
                  </span>
                  <button type="button" disabled={busy} onClick={() => view(v)} className={btn}>{shown?.number === v.number ? 'Hide' : 'Show'}</button>
                  {!current && (
                    <button type="button" disabled={busy} onClick={() => run(() => api.restoreTaste(project, v.id))}
                      title="Put this version back, as a new version" className={btn}>Put back</button>
                  )}
                </li>
              );
            })}
          </ul>
          {shown && (
            <pre key={shown.number} className="orbit-swap mt-2 rounded border border-hair bg-black/20 px-2 py-1.5 whitespace-pre-wrap break-words font-mono text-[11px] leading-snug text-gray-400">
              {shown.text || '(empty)'}
            </pre>
          )}
        </details>
      )}

      {error && <p className="text-[12px] text-rose-300">{error}</p>}
    </div>
  );
}
