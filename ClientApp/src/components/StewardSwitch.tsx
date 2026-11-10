import { useCallback, useEffect, useState } from 'react';
import { GhostButton, Panel } from './ui';
import { BODY, MONO, TIER } from './orbit';
import { label } from './stewardParts';
import { getStewardSwitch, switchStewardToSlices } from '../api/steward';
import type { StewardSwitchView } from '../api/steward';

const FIELD = 'w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none';

/**
 * Move a project on the old shape - a plan, an integration branch, a register that only grows - to
 * slices with a branch each (docs/slice-branches.md, "Moving a project that already exists").
 *
 * One act, the owner's, done once: it refuses until the slices in flight are finished and the
 * integration branch is merged into the new base by hand, and says which of those is still open.
 * What it changes is shown before it changes it: the slices recorded released, the whole project's
 * decisions kept as conventions or retired, and the conventions file the owner edits here.
 */
export function StewardSwitch({ project, onSwitched }: { project: string; onSwitched: () => Promise<void> }) {
  const [view, setView] = useState<StewardSwitchView | null>(null);
  const [base, setBase] = useState('');
  const [path, setPath] = useState('');
  const [text, setText] = useState('');
  const [keep, setKeep] = useState<Set<number>>(new Set());
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);

  const read = useCallback(async (b?: string) => {
    try {
      const r = await getStewardSwitch(project, b);
      setView(r.data); setFailed(null);
      // The base and the draft are filled in once; what the owner typed is theirs.
      setBase(x => x || r.data.base);
      setPath(x => x || r.data.conventions);
      setText(x => x || r.data.draft);
      setKeep(k => (k.size > 0 ? k : new Set(r.data.projectWide.map(d => d.number))));
    } catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
  }, [project]);
  useEffect(() => { void read(); }, [read]);

  const go = async () => {
    setBusy(true); setFailed(null);
    try {
      const r = await switchStewardToSlices(project, { base: base.trim(), conventions: path.trim(), text, keep: [...keep] });
      const d = r.data;
      setDone(`${d.from} is done with: the hub is on ${d.base}${d.conventions ? `, with ${d.conventions} committed` : ''}. `
        + `${d.released} slice${d.released === 1 ? '' : 's'} recorded released, ${d.conventionsKept} convention${d.conventionsKept === 1 ? '' : 's'} kept, `
        + `${d.retired} decision${d.retired === 1 ? '' : 's'} retired. Commit the project's settings in its folder.`);
      await onSwitched();
    } catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(false); }
  };

  const toggle = (n: number) => setKeep(k => { const x = new Set(k); if (x.has(n)) x.delete(n); else x.add(n); return x; });
  const note = { font: `400 12px/1.55 ${BODY}`, color: 'var(--w-t400)' } as const;

  if (done) return (
    <Panel className="mb-8" style={{ maxWidth: 980 }}>
      <div style={{ font: `400 12.5px/1.6 ${BODY}`, color: 'var(--w-t200)' }}>{done}</div>
    </Panel>
  );
  if (!view) return failed
    ? <Panel className="mb-8"><span style={{ ...note, color: TIER.blocked.c }}>{failed}</span></Panel>
    : null;

  const ready = view.refusals.length === 0 && base.trim().length > 0 && path.trim().length > 0;
  return (
    <div className="mb-8 flex flex-col gap-3" style={{ maxWidth: 980 }}>
      <Panel>
        <div style={{ ...note, marginBottom: 10 }}>
          Each slice gets a branch of its own, cut from the base and released into it, and its decisions retire with it.
          What every slice keeps is a short conventions file. This is done once: finish the slices in flight
          on <span style={{ font: `400 11.5px ${MONO}` }}>{view.from || 'the base'}</span>, merge it into the new base by hand, then switch.
        </div>
        <div className="grid gap-2 sm:grid-cols-2">
          <label className="min-w-0">
            <div style={{ ...label(), marginBottom: 4 }}>The base from now on</div>
            <input value={base} onChange={e => setBase(e.target.value)} onBlur={() => void read(base.trim() || undefined)}
              placeholder="main" className={FIELD} />
          </label>
          <label className="min-w-0">
            <div style={{ ...label(), marginBottom: 4 }}>The conventions file</div>
            <input value={path} onChange={e => setPath(e.target.value)} placeholder="docs/conventions.md" className={FIELD} />
          </label>
        </div>
        {view.refusals.length > 0 && (
          <div className="flex flex-col gap-1" style={{ marginTop: 12 }}>
            <div style={label(TIER.blocked.c)}>Not yet</div>
            {view.refusals.map(r => <div key={r} style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>{r}</div>)}
            <div><GhostButton onClick={() => void read(base.trim() || undefined)}>Check again</GhostButton></div>
          </div>
        )}
      </Panel>

      <Panel>
        <div style={{ ...label(), marginBottom: 6 }}>Recorded released, so their decisions retire</div>
        {view.released.length === 0
          ? <div style={note}>No slice has been agreed yet.</div>
          : (
            <div className="flex flex-wrap gap-x-4 gap-y-1">
              {view.released.map(r => (
                <span key={`${r.plan}-${r.unit}`} className="min-w-0" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t200)' }}>
                  <span style={{ font: `700 11px ${MONO}`, color: 'var(--w-t400)' }}>{r.plan ? `${r.plan} ` : ''}{String(r.unit).padStart(2, '0')}</span> {r.title}
                </span>
              ))}
            </div>
          )}
      </Panel>

      <Panel>
        <div style={{ ...label(), marginBottom: 6 }}>The whole project's decisions: ticked ones become conventions, the rest retire</div>
        {view.projectWide.length === 0
          ? <div style={note}>None: every decision is a slice's.</div>
          : (
            <div className="flex flex-col gap-1.5">
              {view.projectWide.map(d => (
                <label key={d.number} className="orbit-tap flex items-start gap-2 min-w-0 cursor-pointer">
                  <input type="checkbox" checked={keep.has(d.number)} onChange={() => toggle(d.number)} style={{ marginTop: 3, flex: 'none' }} />
                  <span className="min-w-0" style={{ font: `400 12px/1.5 ${BODY}`, color: keep.has(d.number) ? 'var(--w-t200)' : 'var(--w-t500)' }}>
                    <span style={{ font: `700 11px ${MONO}`, color: 'var(--w-t400)' }}>#{d.number}</span> {d.title ? `${d.title}: ` : ''}{d.rule}
                  </span>
                </label>
              ))}
            </div>
          )}
        <div style={{ ...label(), margin: '14px 0 6px' }}>{path || 'The conventions file'}, as it is committed</div>
        <textarea value={text} onChange={e => setText(e.target.value)} rows={12} aria-label="The conventions file"
          className={`${FIELD} resize-y`} style={{ font: `400 12px/1.5 ${MONO}` }} />
        <div style={{ ...note, marginTop: 6 }}>A draft of every decision above, each said once. Shorten it to what every slice has to keep.</div>
        <div className="flex items-center gap-3 flex-wrap" style={{ marginTop: 10 }}>
          <GhostButton tone="go" disabled={busy || !ready} onClick={go}
            title={ready ? `Move the hub onto ${base}, commit the conventions there, and switch the project` : 'Not yet: see above'}>
            {busy ? 'Switching' : 'Switch to slice branches'}
          </GhostButton>
        </div>
        {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c, marginTop: 8 }}>{failed}</div>}
      </Panel>
    </div>
  );
}
