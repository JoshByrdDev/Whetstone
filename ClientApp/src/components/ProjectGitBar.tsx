import { useState, useEffect, useCallback } from 'react';
import { ProjectStackChip, type StackSnapshot } from './ProjectStackChip';
import { TfvcSyncModal } from './TfvcSyncModal';

interface ProjectGit {
  key: string;
  displayName: string;
  branch: string;
  changedFiles: number;
  projectPath: string;
  /** "tfvc" hubs take a Get Latest, whose landings the change count is mostly made of. */
  repoType?: 'git' | 'tfvc';
}

/**
 * Compact per-project state (branch + folder + change count + local stack), one row per
 * configured project. When a project filter is active, only that project shows.
 *
 * Git and stack are polled on separate clocks on purpose: git status is slow-moving and cheap to be
 * stale about, while the stack dot has to track a start the user just pressed.
 */
export function ProjectGitBar({ projectFilter }: { projectFilter: string | null }) {
  const [projects, setProjects] = useState<ProjectGit[]>([]);
  const [stacks, setStacks] = useState<Record<string, StackSnapshot>>({});
  const [syncFor, setSyncFor] = useState<string | null>(null);

  const fetchGit = useCallback(() => {
    fetch('/api/projects/git')
      .then(r => r.json())
      .then((data: ProjectGit[]) => setProjects(Array.isArray(data) ? data : []))
      .catch(() => {});
  }, []);

  useEffect(() => {
    fetchGit();
    const interval = setInterval(fetchGit, 15000);
    return () => clearInterval(interval);
  }, [fetchGit]);

  useEffect(() => {
    const fetchStacks = () => {
      fetch('/api/projects/stacks')
        .then(r => r.json())
        .then((data: StackSnapshot[]) => {
          if (!Array.isArray(data)) return;
          setStacks(Object.fromEntries(data.map(s => [s.key, s])));
        })
        .catch(() => {});
    };
    fetchStacks();
    const interval = setInterval(fetchStacks, 5000);
    return () => clearInterval(interval);
  }, []);

  // A start or stop replies with the snapshot it produced, so the chip flips immediately rather
  // than sitting on stale state until the next poll comes round.
  const applyStack = useCallback((next: StackSnapshot) => {
    setStacks(prev => ({ ...prev, [next.key]: next }));
  }, []);

  const shown = projectFilter ? projects.filter(p => p.key === projectFilter) : projects;
  if (shown.length === 0) return null;

  return (
    <div className="flex flex-wrap items-center gap-x-5 gap-y-1">
      {shown.map(p => (
        <div key={p.key} className="flex items-center gap-2 text-xs" title={p.projectPath}>
          <span className="text-gray-300 font-medium">{p.displayName}</span>
          <span className="flex items-center gap-1 font-mono text-gray-400">
            <svg className="h-3 w-3 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M13 10V3L4 14h7v7l9-11h-7z" />
            </svg>
            {p.branch}
          </span>
          {p.changedFiles > 0 && (
            <span className="px-1.5 py-0.5 rounded bg-amber-500/20 text-amber-400 text-[10px] font-semibold">
              {p.changedFiles} changed
            </span>
          )}
          {/* On a TFVC hub most of that count is a Get Latest's landings, and this is where it shows. */}
          {p.repoType === 'tfvc' && p.changedFiles > 0 && (
            <button
              onClick={() => setSyncFor(p.key)}
              title="Commit what a Get Latest brought in, so git main matches TFVC - your own edits are left alone"
              className="orbit-tap px-1.5 py-0.5 rounded border border-hair text-[10px] font-semibold text-sky-300 hover:text-sky-200"
            >
              Sync with TFVC
            </button>
          )}
          {stacks[p.key]?.configured && (
            <>
              <span className="h-3 w-px bg-gray-700" />
              <ProjectStackChip stack={stacks[p.key]} onChanged={applyStack} />
            </>
          )}
          {p.projectPath && (
            <span className="text-[10px] text-gray-600 font-mono truncate max-w-[260px]">{p.projectPath}</span>
          )}
        </div>
      ))}
      {syncFor && <TfvcSyncModal project={syncFor} onClose={() => setSyncFor(null)} onSynced={fetchGit} />}
    </div>
  );
}
