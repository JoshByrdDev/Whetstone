import { useState, useEffect, useCallback, useId } from 'react';
import { CAPS, DISPLAY } from './orbit';
import { track } from '../theme';

export interface ProjectOpt {
  key: string;
  displayName: string;
}

/** One entry per tab that opts in, so switching project in one does not move the others. */
const STORE_PREFIX = 'whetstone.project.';

function readRemembered(slot: string | undefined): string | null {
  if (!slot) return null;
  try {
    return localStorage.getItem(STORE_PREFIX + slot);
  } catch {
    return null; // private mode, blocked storage: the tab just opens on the first project
  }
}

function writeRemembered(slot: string | undefined, key: string): void {
  if (!slot) return;
  try {
    localStorage.setItem(STORE_PREFIX + slot, key);
  } catch {
    /* the choice still stands for this page; it just will not survive a re-entry */
  }
}

/**
 * Loads the enabled-project list and tracks a selection (defaults to the first).
 * Used by the Classroom, Skills, Metrics and System-Improvement tabs to scope their content.
 *
 * Pass `rememberAs` and the choice is kept per-machine in localStorage under that slot, so
 * re-entering the tab lands back on the project you were reading. A remembered key that is no
 * longer an enabled project is ignored rather than left dangling — the first project stands in.
 */
export function useProjectList(rememberAs?: string): [ProjectOpt[], string | null, (key: string) => void] {
  const [projects, setProjects] = useState<ProjectOpt[]>([]);
  const [project, setProject] = useState<string | null>(null);

  useEffect(() => {
    fetch('/api/projects')
      .then(r => r.json())
      .then((data: { projects?: ProjectOpt[] }) => {
        const list = data.projects ?? [];
        setProjects(list);
        const saved = readRemembered(rememberAs);
        const restored = list.some(p => p.key === saved) ? saved : null;
        setProject(prev => prev ?? restored ?? list[0]?.key ?? null);
      })
      .catch(() => {});
  }, [rememberAs]);

  const choose = useCallback((key: string) => {
    setProject(key);
    writeRemembered(rememberAs, key);
  }, [rememberAs]);

  return [projects, project, choose];
}

export function ProjectPicker({ projects, project, onChange, label = 'Project' }: {
  projects: ProjectOpt[];
  project: string | null;
  onChange: (key: string) => void;
  /** What the control is scoping. Shown above it, and read out as the select's name. */
  label?: string;
}) {
  // Called unconditionally: a hook cannot sit after the single-project early return below.
  const id = useId();
  // Nothing to pick when there's a single project.
  if (projects.length <= 1) return null;
  return (
    <div className="flex flex-col gap-1">
      <label
        htmlFor={id}
        style={{
          font: `600 9px ${DISPLAY}`, letterSpacing: track(0.28), textTransform: CAPS,
          color: 'var(--w-t500)',
        }}
      >
        {label}
      </label>
      {/* `orbit-tap`: this is a control, not a choice among rows, so it lifts. The class also
          carries the colour transitions, which is what makes the hover utilities below fade
          rather than snap. */}
      <select
        id={id}
        value={project ?? ''}
        onChange={e => onChange(e.target.value)}
        className="orbit-tap cursor-pointer bg-gray-800 border border-hair rounded px-2 py-1 text-sm text-gray-200
                   hover:bg-gray-700 hover:border-gray-500 hover:text-gray-100
                   focus:outline-none focus:border-gray-500"
      >
        {projects.map(p => <option key={p.key} value={p.key}>{p.displayName}</option>)}
      </select>
    </div>
  );
}
