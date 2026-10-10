import { useState, useEffect } from 'react';
import type { CSSProperties } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { PageHead, SectionRule } from './ui';
import { CAPS, chamfer, DISPLAY, MONO } from './orbit';
import { track } from '../theme';
import { useVeil } from '../hooks/useVeil';

interface Skill {
  name: string;
  description: string;
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
    <div className="max-w-7xl mx-auto px-6 py-4">
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
            <div style={{ font: `700 13px ${MONO}`, color: 'var(--w-t100)', marginBottom: 8 }}>
              /{skill.name}
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
