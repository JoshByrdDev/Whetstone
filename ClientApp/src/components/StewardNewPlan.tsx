import { useState } from 'react';
import { GhostButton, Panel } from './ui';
import { BODY, MONO, TIER } from './orbit';
import { label } from './stewardParts';
import { startStewardPlan } from '../api/steward';

const FIELD = 'w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none';

/**
 * Start a plan: its charter, and the ideas it starts from - a title and a paragraph each. The
 * charter is written afterwards, in the repo; the steward expands the first idea into a draft for
 * you to agree. Ideas are yours, so they are written here, not by the steward.
 *
 * A tab of its own on the Steward page: starting a plan is a rare act, and it has no business
 * sitting under the plan being worked every time somebody reads it.
 */
export function NewPlan({ project, has, onStarted }: { project: string; has: boolean; onStarted: () => Promise<void> }) {
  const [name, setName] = useState('');
  const [key, setKey] = useState('');
  const [folder, setFolder] = useState('');
  const [ideas, setIdeas] = useState<{ title: string; paragraph: string }[]>([{ title: '', paragraph: '' }]);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const [made, setMade] = useState<{ charter: string; ideas: string[]; settings: string } | null>(null);

  // The key follows the name until somebody types one of their own.
  const [keyTouched, setKeyTouched] = useState(false);
  const slug = (v: string) => v.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 40);
  const theKey = keyTouched ? key : slug(name);
  const theFolder = folder.trim() || (theKey ? `docs/plans/${theKey}` : '');
  const written = ideas.filter(i => i.title.trim());
  const ready = name.trim().length >= 3 && !!theKey && written.length > 0 && written.every(i => i.paragraph.trim());
  const setIdea = (n: number, v: Partial<{ title: string; paragraph: string }>) =>
    setIdeas(all => all.map((x, i) => (i === n ? { ...x, ...v } : x)));

  const start = async () => {
    setBusy(true); setFailed(null);
    try {
      const r = await startStewardPlan(project, {
        key: theKey, name: name.trim(), folder: folder.trim() || undefined,
        ideas: written.map(i => ({ title: i.title.trim(), paragraph: i.paragraph.trim() })),
      });
      setMade({ charter: r.data.charter, ideas: r.data.ideas, settings: r.settings });
      await onStarted();
    } catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  return (
    <div className="mb-8" style={{ maxWidth: 980 }}>
        <Panel className="mb-2">
          {made
            ? (
              <div style={{ font: `400 12.5px/1.6 ${BODY}`, color: 'var(--w-t200)' }}>
                The plan is started. Its charter and ideas are on the base branch:
                <div style={{ font: `400 11.5px ${MONO}`, color: 'var(--w-t300)', margin: '6px 0' }}>
                  {made.charter}{made.ideas.map(i => <span key={i}><br />{i}</span>)}
                </div>
                Write the charter in the repo: why the plan exists, its words, where things are. The steward expands the
                first idea into a draft for you to agree here. The project's settings now point at this plan
                (<span style={{ font: `400 11.5px ${MONO}` }}>{made.settings}</span>): commit that file in the project's folder.
              </div>
            )
            : (
              <>
                <div style={{ font: `400 12px/1.55 ${BODY}`, color: 'var(--w-t400)', marginBottom: 10 }}>
                  {has
                    ? 'A project works one plan at a time. Starting another keeps this one to be read and moves the steward to the new one, so it waits until every ticket of this plan is closed.'
                    : 'A plan is a charter that says why, and its slices in order. Each slice starts as an idea: a title and a paragraph. The steward expands one idea at a time into a draft when its turn comes.'}
                </div>
                <div className="grid gap-2 sm:grid-cols-3">
                  <label className="min-w-0">
                    <div style={{ ...label(), marginBottom: 4 }}>What it is called</div>
                    <input value={name} onChange={e => setName(e.target.value)} placeholder="Customer accounts" className={FIELD} />
                  </label>
                  <label className="min-w-0">
                    <div style={{ ...label(), marginBottom: 4 }}>Its key</div>
                    <input value={theKey} onChange={e => { setKeyTouched(true); setKey(slug(e.target.value)); }} placeholder="customer-accounts" className={FIELD} />
                  </label>
                  <label className="min-w-0">
                    <div style={{ ...label(), marginBottom: 4 }}>Where in the repo</div>
                    <input value={folder} onChange={e => setFolder(e.target.value)} placeholder={theFolder || 'docs/plans/its-key'} className={FIELD} />
                  </label>
                </div>

                <div style={{ ...label(), margin: '14px 0 6px' }}>Its ideas, in order</div>
                <div className="flex flex-col gap-2">
                  {ideas.map((idea, n) => (
                    <div key={n} className="flex flex-col gap-1.5">
                      <div className="flex items-center gap-2 min-w-0">
                        <span style={{ font: `700 11px ${MONO}`, color: 'var(--w-t400)', flex: 'none' }}>{String(n + 1).padStart(2, '0')}</span>
                        <input value={idea.title} onChange={e => setIdea(n, { title: e.target.value })}
                          placeholder="A title: what a person can do at the end" aria-label={`Idea ${n + 1} title`} className={FIELD} />
                        {ideas.length > 1 && (
                          <GhostButton onClick={() => setIdeas(all => all.filter((_, i) => i !== n))} title="Take this idea out">Remove</GhostButton>
                        )}
                      </div>
                      <textarea value={idea.paragraph} onChange={e => setIdea(n, { paragraph: e.target.value })} rows={2}
                        placeholder="One paragraph: who, what they can do at the end, what it builds on, what it is not."
                        aria-label={`Idea ${n + 1} paragraph`} className={`${FIELD} resize-y`} />
                    </div>
                  ))}
                </div>

                <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 10 }}>
                  <GhostButton onClick={() => setIdeas(all => [...all, { title: '', paragraph: '' }])}>Another idea</GhostButton>
                  <GhostButton tone="go" disabled={busy || !ready} onClick={start}>
                    {busy ? 'Starting' : 'Start the plan'}
                  </GhostButton>
                  {theFolder && (
                    <span className="min-w-0 truncate" style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>
                      {theFolder}/charter.md, {theFolder}/slices/
                    </span>
                  )}
                </div>
                {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 8 }}>{failed}</div>}
              </>
            )}
        </Panel>
    </div>
  );
}
