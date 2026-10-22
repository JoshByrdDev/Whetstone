import { useState, useEffect, useCallback } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { PageHead, SectionRule, Panel, GhostButton, Chip } from './ui';
import { CAPS, chamfer, DISPLAY, MONO } from './orbit';
import { track } from '../theme';
import { useVeil } from '../hooks/useVeil';
import { PatchView } from './PatchView';
import { useSkillDrift, refreshSkillDrift } from '../hooks/useSkillDrift';

interface Skill {
  name: string;
  description: string;
}

/** One of this project's copies of a template, and where it stands against it. See SkillDriftService. */
interface DriftItem {
  file: string;
  status: 'current' | 'behind' | 'untracked';
  syncedAt: string | null;
  how: string | null;
  baseCommit: string | null;
  commits: { sha: string; subject: string; date: string }[];
  templateDiff: string | null;
  copyDiff: string | null;
  conflicts: number | null;
}

export function SkillsPanel() {
  const [projects, project, setProject] = useProjectList();
  const [skills, setSkills] = useState<Skill[]>([]);
  const [selectedSkill, setSelectedSkill] = useState<string | null>(null);
  const [skillContent, setSkillContent] = useState<string>('');
  const [loading, setLoading] = useState(false);
  const [promptContent, setPromptContent] = useState<string | null>(null);
  const [showPrompt, setShowPrompt] = useState(false);

  useEffect(() => {
    if (!project) return;
    fetch(`/api/skills?project=${encodeURIComponent(project)}`)
      .then(r => r.json())
      .then(setSkills)
      .catch(() => {});
  }, [project]);

  // null until this project's answer arrives: it takes a second or so (a git diff and merge per
  // card), and an empty list in the meantime would announce "up to date" about a project that is not.
  const [drift, setDrift] = useState<DriftItem[] | null>(null);
  const summary = useSkillDrift();
  const loadDrift = useCallback(() => {
    if (!project) return;
    fetch(`/api/skill-drift/${encodeURIComponent(project)}`)
      .then(r => (r.ok ? r.json() : []))
      .then((items: DriftItem[]) => setDrift(Array.isArray(items) ? items : []))
      .catch(() => setDrift([]));
  }, [project]);
  useEffect(() => { setDrift(null); loadDrift(); }, [loadDrift]);

  // A card resolved here has to take the badge in the header down with it.
  const resolved = useCallback(() => { loadDrift(); refreshSkillDrift(); }, [loadDrift]);

  // Behind first: those have a change to take. A copy with no baseline is a one-off chore.
  const pending = (drift ?? [])
    .filter(d => d.status !== 'current')
    .sort((a, b) => (a.status === b.status ? 0 : a.status === 'behind' ? -1 : 1));
  const checking = drift === null && summary.some(s => s.project === project && s.behind + s.untracked > 0);
  const behindSkill = (name: string) =>
    (drift ?? []).some(d => d.status !== 'current' && d.file === `skills/${name}/skill.md`);
  // Other projects with news, so the badge's count can be traced from whichever project is open.
  const elsewhere = summary.filter(s => s.project !== project && s.behind + s.untracked > 0);
  const nameOf = (key: string) => projects.find(p => p.key === key)?.displayName ?? key;

  const openSkill = async (name: string) => {
    if (!project) return;   // the list this is clicked from only exists once a project is picked
    setSelectedSkill(name);
    setLoading(true);
    try {
      const res = await fetch(`/api/skills/${name}?project=${encodeURIComponent(project)}`);
      const data = await res.json();
      setSkillContent(data.content);
    } catch {
      setSkillContent('Failed to load skill file.');
    } finally {
      setLoading(false);
    }
  };

  const openPrompt = async () => {
    setShowPrompt(true);
    if (promptContent !== null) return; // already loaded
    setLoading(true);
    try {
      const res = await fetch('/api/heartbeat-prompt');
      const data = await res.json();
      setPromptContent(data.content);
    } catch {
      setPromptContent('Failed to load heartbeat prompt.');
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="max-w-7xl mx-auto px-3 sm:px-6 py-4">
      <PageHead
        eyebrow="Whetstone // instructions"
        title="Skills"
        sub={`${skills.length} skill${skills.length === 1 ? '' : 's'} in this project's library`}
        left={<div style={{ paddingBottom: 5 }}><ProjectPicker projects={projects} project={project} onChange={setProject} /></div>}
      />

      {/* The dispatcher sits above the library and outside it: every skill below is something the
          dispatcher can reach for, so it is the one card that is about the others. */}
      <SectionRule title="Dispatcher" colour="var(--w-accent)" />
      <button
        onClick={openPrompt}
        className="orbit-btn w-full mb-8 text-left"
        style={{
          clipPath: chamfer(13),
          ['--rbg' as string]: 'linear-gradient(120deg,rgb(var(--w-accent-rgb) / .08),rgb(var(--w-panel-deep-rgb) / .44))',
          ['--rbw' as string]: '1px dashed rgb(var(--w-accent-rgb) / .32)',
          padding: '18px 20px',
          ['--hbg' as string]: 'linear-gradient(120deg,rgb(var(--w-accent-rgb) / .14),rgb(var(--w-panel-deep-rgb) / .5))',
          ['--hbc' as string]: 'rgb(var(--w-accent-rgb) / .6)',
          ['--hsh' as string]: 'inset 0 0 30px -12px var(--w-accent)',
        } as CSSProperties}
      >
        <div className="flex items-center gap-3 mb-2">
          <span
            style={{
              font: `700 8.5px ${DISPLAY}`, letterSpacing: track(.16), textTransform: CAPS,
              color: 'var(--w-accent-text)', background: 'rgb(var(--w-accent-rgb) / .12)',
              border: '1px solid rgb(var(--w-accent-rgb) / .4)', padding: '2px 7px',
            }}
          >
            Read-only
          </span>
          <span style={{ font: `700 13px ${MONO}`, color: 'var(--w-t100)' }}>heartbeat-prompt.md</span>
        </div>
        <div className="text-sm text-gray-400 leading-relaxed">
          The dispatcher prompt that orchestrates heartbeat cycles — detects ticket phases and delegates to skills.
        </div>
      </button>

      {(pending.length > 0 || elsewhere.length > 0 || checking) && (
        <>
          <SectionRule title="Template updates" colour="var(--w-gate)" count={pending.length || undefined}
                       meta={pending.length ? 'the templates moved on since these copies were written' : undefined} />
          {checking && <div className="text-sm text-gray-500 mb-3">Comparing this project&apos;s copies with the templates…</div>}
          {elsewhere.length > 0 && (
            <div className="flex flex-wrap items-center gap-2 mb-3 text-sm text-gray-400">
              <span>{pending.length || checking ? 'Other projects behind:' : 'This project is up to date. Behind:'}</span>
              {elsewhere.map(s => (
                <Chip key={s.project} on={false} onClick={() => setProject(s.project)}>
                  {nameOf(s.project)} · {s.behind + s.untracked}
                </Chip>
              ))}
            </div>
          )}
          <div className="space-y-3 mb-8">
            {pending.map(item => (
              <DriftCard key={`${project}:${item.file}`} project={project!} item={item} onResolved={resolved} />
            ))}
          </div>
        </>
      )}

      <SectionRule title="Library" colour="var(--w-queue)" count={skills.length} />
      <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-3">
        {skills.map(skill => (
          <button
            key={skill.name}
            onClick={() => openSkill(skill.name)}
            className="orbit-btn text-left"
            style={{
              clipPath: chamfer(13),
              ['--rbg' as string]: 'linear-gradient(120deg,rgb(var(--w-panel-lit-rgb) / .62),rgb(var(--w-panel-deep-rgb) / .44))',
              ['--rbw' as string]: '1px solid rgb(var(--w-line-rgb) / .12)',
              padding: '18px 20px',
              ['--hbg' as string]: 'linear-gradient(120deg,rgb(var(--w-queue-rgb) / .14),rgb(var(--w-panel-deep-rgb) / .5))',
              ['--hbc' as string]: 'rgb(var(--w-queue-rgb) / .42)',
              ['--hsh' as string]: 'inset 0 0 30px -12px var(--w-queue)',
            } as CSSProperties}
          >
            <div className="flex items-center gap-2 flex-wrap" style={{ marginBottom: 8 }}>
              <span style={{ font: `700 13px ${MONO}`, color: 'var(--w-t100)' }}>/{skill.name}</span>
              {behindSkill(skill.name) && <Tag colour="var(--w-gate)">Template updated</Tag>}
            </div>
            <div className="text-sm text-gray-400 leading-relaxed">
              {skill.description}
            </div>
          </button>
        ))}
      </div>

      {skills.length === 0 && (
        <div className="text-gray-500 text-center py-12">
          No skills configured for this project yet.
        </div>
      )}

      {/* Both documents this page opens are the same overlay: a filename, a body, one way out. */}
      {selectedSkill && (
        <DocModal name={`/${selectedSkill}/skill.md`} loading={loading} body={skillContent}
                  onClose={() => setSelectedSkill(null)} />
      )}

      {showPrompt && (
        <DocModal name="heartbeat-prompt.md" badge="Read-only"
                  loading={loading && promptContent === null} body={promptContent}
                  onClose={() => setShowPrompt(false)} />
      )}
    </div>
  );
}

/**
 * A read-only file, opened over the page that lists it.
 *
 * Written once rather than twice: the two documents this page shows differ only by a filename, a
 * badge and where their text came from, and the copy that was not being edited is how the second
 * one ended up without the first one's dismissal.
 *
 * Rendered only while open, which is what lets it see itself out — `useVeil` holds the unmount off
 * for the length of the fade, and a panel kept mounted while hidden would re-open already faded.
 */
function DocModal({ name, badge, loading, body, onClose }: {
  name: string;
  badge?: string;
  loading: boolean;
  body: string | null;
  onClose: () => void;
}) {
  const { fx, dismiss } = useVeil(onClose);
  return (
    <div
      className="orbit-veil fixed inset-0 bg-black/70 flex items-center justify-center z-50 p-6"
      data-fx={fx}
      onClick={dismiss}
    >
      {/* The dim is the container's own background here, so there is no `.orbit-dim` child to
          exempt — the sheet fades with the veil and the panel below is the only thing that rises. */}
      <div
        className="orbit-modal w-full max-w-3xl max-h-[80vh] flex flex-col"
        onClick={e => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-5 py-3 border-b border-hair">
          <div className="flex items-center gap-3">
            <span className="font-mono font-bold text-sm">{name}</span>
            {badge && (
              <span className="text-xs font-semibold uppercase tracking-wider text-amber-400/80 bg-amber-400/10 px-2 py-0.5 rounded">
                {badge}
              </span>
            )}
          </div>
          <button onClick={dismiss} className="text-gray-400 hover:text-gray-100 text-lg px-2">
            ✕
          </button>
        </div>
        <div className="overflow-y-auto p-5">
          {loading ? (
            <div className="text-gray-500 text-center py-8">Loading...</div>
          ) : (
            <pre className="text-sm text-gray-300 whitespace-pre-wrap font-mono leading-relaxed">
              {body}
            </pre>
          )}
        </div>
      </div>
    </div>
  );
}

/** A small status label: the shape of the dispatcher's "Read-only", in whatever colour it means. */
function Tag({ colour, children }: { colour: string; children: ReactNode }) {
  return (
    <span
      style={{
        font: `700 8.5px ${DISPLAY}`, letterSpacing: track(.16), textTransform: CAPS,
        color: colour, border: `1px solid ${colour}`, padding: '2px 7px', whiteSpace: 'nowrap',
      }}
    >
      {children}
    </span>
  );
}

/** How the copy last came to be in step with its template, in words. */
const HOW: Record<string, string> = {
  scaffolded: 'written from the template',
  matched: 'found to match the template',
  merged: 'template change applied',
  ported: 'marked as ported by hand',
  skipped: 'a template change skipped',
};

/**
 * One copy that its template has moved on from: what changed in the template, why (its commits),
 * and the ways to settle it. Apply is a three-way merge done by the server, offered only when it
 * is clean; "ported" and "skip" move the baseline without touching the copy.
 */
function DriftCard({ project, item, onResolved }: { project: string; item: DriftItem; onResolved: () => void }) {
  const [open, setOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [preview, setPreview] = useState<string | null>(null);
  const behind = item.status === 'behind';

  const post = async (path: string, body: object) => {
    setBusy(true);
    setError(null);
    try {
      const res = await fetch(`/api/skill-drift/${encodeURIComponent(project)}/${path}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ file: item.file, ...body }),
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok) { setError(data.error ?? `Failed (${res.status}).`); return null; }
      return data;
    } catch {
      setError('Could not reach the server.');
      return null;
    } finally {
      setBusy(false);
    }
  };

  // Also the conflict preview: a conflicted apply writes nothing and returns the merge with its markers.
  const apply = async () => {
    const r = await post('apply', {});
    if (!r) return;
    if (r.ok) { onResolved(); return; }
    setOpen(false);
    setPreview(r.preview ?? null);
  };
  const mark = async (how: 'ported' | 'skipped') => { if (await post('mark-synced', { how })) onResolved(); };

  const diff = behind ? item.templateDiff : item.copyDiff;
  const synced = item.syncedAt ? new Date(item.syncedAt).toLocaleDateString() : null;
  const places = (n: number) => `${n} place${n === 1 ? '' : 's'}`;

  return (
    <Panel tone="var(--w-gate)">
      <div className="flex items-center gap-x-3 gap-y-1 flex-wrap mb-2">
        <span className="min-w-0 break-all" style={{ font: `700 13px ${MONO}`, color: 'var(--w-t100)' }}>{item.file}</span>
        <Tag colour="var(--w-gate)">{behind ? 'Behind template' : 'No baseline'}</Tag>
        {synced && (
          <span className="text-xs text-gray-500">
            last synced {synced}{item.how && HOW[item.how] ? ` · ${HOW[item.how]}` : ''}
          </span>
        )}
      </div>

      {behind ? (
        <>
          {item.commits.length > 0 && (
            <ul className="mb-2 space-y-0.5">
              {item.commits.map(c => (
                <li key={c.sha} className="text-sm text-gray-300 flex gap-2 min-w-0">
                  <span className="flex-none font-mono text-xs text-gray-500" style={{ paddingTop: 2 }}>{c.sha}</span>
                  <span className="min-w-0 flex-1">{c.subject}</span>
                  <span className="flex-none text-xs text-gray-600" style={{ paddingTop: 2 }}>{c.date}</span>
                </li>
              ))}
            </ul>
          )}
          <div className="text-sm text-gray-400 mb-3">
            {item.conflicts === 0
              ? 'The change merges cleanly with whatever this project has edited in its copy.'
              : `${places(item.conflicts ?? 1)} in the change ${item.conflicts === 1 ? 'overlaps' : 'overlap'} this project's own edits, so it has to be ported by hand.`}
          </div>
        </>
      ) : (
        <div className="text-sm text-gray-400 mb-3">
          This copy matches no version of the template, so there is no earlier template to merge from.
          Compare it with the template as it is now, port what applies, then mark it as ported. Template
          changes are tracked from then on.
        </div>
      )}

      <div className="flex flex-wrap gap-2">
        {diff && (
          <GhostButton onClick={() => { setOpen(o => !o); setPreview(null); }}>
            {open ? 'Hide' : behind ? 'Show template change' : 'Compare with template'}
          </GhostButton>
        )}
        {behind && (item.conflicts === 0 ? (
          <GhostButton tone="go" onClick={apply} disabled={busy}
                       title="Merge the template's change into this project's copy">
            Apply
          </GhostButton>
        ) : (
          <GhostButton onClick={apply} disabled={busy}
                       title="Show the merge with its conflict markers. Writes nothing.">
            Show conflicts
          </GhostButton>
        ))}
        <GhostButton onClick={() => mark('ported')} disabled={busy}
                     title="You changed the copy yourself. Track from the template as it is now.">
          I&apos;ve ported it
        </GhostButton>
        {behind && (
          <GhostButton onClick={() => mark('skipped')} disabled={busy}
                       title="This change is not for this project. The copy is left as it is.">
            Skip this change
          </GhostButton>
        )}
      </div>

      {error && <div className="text-sm text-red-300 mt-2">{error}</div>}

      {open && diff && (
        <div key="diff" className="orbit-swap mt-3 max-h-96 overflow-auto border border-hair dark-scrollbar">
          {!behind && (
            <div className="px-3 py-1.5 text-xs text-gray-500 border-b border-hair">
              <span className="text-red-300">−</span> this project&apos;s copy · <span className="text-emerald-300">+</span> the template now
            </div>
          )}
          <PatchView patch={diff} />
        </div>
      )}
      {preview && (
        <div key="preview" className="orbit-swap mt-3 max-h-96 overflow-auto border border-hair dark-scrollbar">
          <div className="px-3 py-1.5 text-xs text-gray-500 border-b border-hair">
            The merge with its conflicts marked. Nothing was written; copy what you need into the project&apos;s file.
          </div>
          <pre className="text-[10.5px] font-mono leading-[1.45] p-3 whitespace-pre-wrap break-all text-gray-300">{preview}</pre>
        </div>
      )}
    </Panel>
  );
}
