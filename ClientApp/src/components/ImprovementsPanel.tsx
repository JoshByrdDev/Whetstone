import { useState, useEffect, useCallback } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { PageHead, PrimaryButton } from './ui';

// The three the reflection writes today, then the older pair still attached to anything filed
// before that changed. Order matters: it is the dropdown order, knowledge kinds first.
const CATEGORIES = ['environment', 'codebase', 'process', 'lesson', 'system'] as const;
type Category = (typeof CATEGORIES)[number];

// Colour by DESTINATION, not by name — that is the distinction the reviewer is actually making.
// environment/codebase/lesson are facts and get written into the project's own notes store;
// process/system change a skill or a prompt in the dashboard and are why this queue needs a human.
const CATEGORY_STYLE: Record<Category, string> = {
  environment: 'bg-indigo-500/20 text-indigo-400 hover:bg-indigo-500/30',
  codebase: 'bg-indigo-500/20 text-indigo-400 hover:bg-indigo-500/30',
  lesson: 'bg-indigo-500/20 text-indigo-400 hover:bg-indigo-500/30',
  process: 'bg-amber-500/20 text-amber-400 hover:bg-amber-500/30',
  system: 'bg-amber-500/20 text-amber-400 hover:bg-amber-500/30',
};

interface ImprovementItem {
  id: number;
  text: string;
  checked: boolean;
  category: Category;
  timestamp: string;
  /** When the reflection filed it. Null for items that predate stamping. */
  filedAt: string | null;
  ageDays: number | null;
}

/**
 * How long an item has gone unactioned. This queue is only worth reading if it is short, and it has
 * sat at ~50 items before - an undated list gives no sign of which end of it has gone stale, so a
 * fortnight is called out and a month is called out louder.
 */
function ageBadge(ageDays: number | null): { label: string; className: string } | null {
  if (ageDays === null || ageDays < 14) return null;
  const label = ageDays >= 60 ? `${Math.floor(ageDays / 30)}mo` : `${ageDays}d`;
  return {
    label,
    className: ageDays >= 30 ? 'text-amber-400/90' : 'text-gray-500',
  };
}

interface ApplyStatus {
  running: boolean;
  output: string;
  success?: boolean;
  error?: string;
}

export function ImprovementsPanel() {
  const [projects, project, setProject] = useProjectList();
  const [items, setItems] = useState<ImprovementItem[]>([]);
  const [knowledge, setKnowledge] = useState('');
  const [knowledgePath, setKnowledgePath] = useState('');
  const [loading, setLoading] = useState(true);
  const [applyStatus, setApplyStatus] = useState<ApplyStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [editText, setEditText] = useState('');

  // ?project= suffix for every call, so all reads/writes hit the selected project's files. The
  // server refuses a project-scoped call that names no project rather than picking one, so the
  // empty branch here is a 400 by design: with no project selected nothing has loaded, so none of
  // the mutators below can be reached, and if one ever is it must fail loudly rather than edit
  // whichever project happened to sort first.
  const pq = project ? `?project=${encodeURIComponent(project)}` : '';

  // The index of what this project knows. It lives in the project's own repo, not in dashboard
  // data - so this panel is a window onto a file humans and interactive sessions also write.
  const fetchLessons = useCallback(async () => {
    if (!project) return;
    try {
      const res = await fetch(`/api/knowledge?project=${encodeURIComponent(project)}`);
      const data = await res.json();
      setKnowledge(data.content ?? '');
      setKnowledgePath(data.path ?? '');
    } catch { /* ignore */ }
  }, [project]);

  const fetchItems = useCallback(async () => {
    if (!project) return;
    try {
      const res = await fetch(`/api/improvements?project=${encodeURIComponent(project)}`);
      const data = await res.json();
      setItems(data);
      setError(null);
    } catch {
      setError('Failed to load improvements');
    } finally {
      setLoading(false);
    }
  }, [project]);

  useEffect(() => { fetchItems(); fetchLessons(); }, [fetchItems, fetchLessons]);

  // Check initial apply status on mount
  useEffect(() => {
    fetch('/api/improvements/apply-status')
      .then(r => r.json())
      .then(data => {
        if (data.running || data.success !== null) {
          setApplyStatus(data);
        }
      })
      .catch(() => {});
  }, []);

  // Poll apply status when running
  useEffect(() => {
    if (!applyStatus?.running) return;
    const interval = setInterval(async () => {
      try {
        const res = await fetch('/api/improvements/apply-status');
        const data = await res.json();
        setApplyStatus(data);
        if (!data.running) {
          clearInterval(interval);
          fetchItems();
          fetchLessons();
        }
      } catch { /* ignore poll errors */ }
    }, 2000);
    return () => clearInterval(interval);
  }, [applyStatus?.running, fetchItems]);

  const deleteItem = async (id: number) => {
    const prev = items;
    setItems(items.filter(item => item.id !== id));
    try {
      const res = await fetch(`/api/improvements/${id}${pq}`, { method: 'DELETE' });
      if (!res.ok) setItems(prev);
    } catch {
      setItems(prev);
    }
  };

  const setCategory = async (id: number, newCategory: Category) => {
    const item = items.find(i => i.id === id);
    if (!item || item.category === newCategory) return;
    setItems(prev => prev.map(i => i.id === id ? { ...i, category: newCategory } : i));
    try {
      const res = await fetch(`/api/improvements/${id}/category${pq}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ category: newCategory }),
      });
      if (!res.ok) {
        setItems(prev => prev.map(i => i.id === id ? { ...i, category: item.category } : i));
      }
    } catch {
      setItems(prev => prev.map(i => i.id === id ? { ...i, category: item.category } : i));
    }
  };

  const toggleItem = async (id: number, checked: boolean) => {
    setItems(prev => prev.map(item => item.id === id ? { ...item, checked } : item));
    try {
      const res = await fetch(`/api/improvements/${id}${pq}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ checked }),
      });
      if (!res.ok) {
        setItems(prev => prev.map(item => item.id === id ? { ...item, checked: !checked } : item));
      }
    } catch {
      setItems(prev => prev.map(item => item.id === id ? { ...item, checked: !checked } : item));
    }
  };

  const startEdit = (item: ImprovementItem) => {
    setEditingId(item.id);
    setEditText(item.text);
  };

  const cancelEdit = () => {
    setEditingId(null);
    setEditText('');
  };

  const saveEdit = async (id: number) => {
    const trimmed = editText.trim();
    if (!trimmed) return;
    const item = items.find(i => i.id === id);
    if (!item || item.text === trimmed) { cancelEdit(); return; }

    const prev = items;
    setItems(items.map(i => i.id === id ? { ...i, text: trimmed } : i));
    setEditingId(null);
    setEditText('');

    try {
      const res = await fetch(`/api/improvements/${id}/text${pq}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: trimmed }),
      });
      if (!res.ok) setItems(prev);
    } catch {
      setItems(prev);
    }
  };

  const handleApply = async () => {
    const checkedCount = items.filter(i => i.checked).length;
    if (checkedCount === 0) return;

    try {
      const res = await fetch(`/api/improvements/apply${pq}`, { method: 'POST' });
      const data = await res.json();
      if (res.ok) {
        setApplyStatus({ running: true, output: '' });
      } else {
        setApplyStatus({ running: false, output: '', error: data.message || 'Failed to start' });
      }
    } catch {
      setApplyStatus({ running: false, output: '', error: 'Failed to start apply process' });
    }
  };

  const dismissStatus = () => {
    setApplyStatus(null);
    fetch('/api/improvements/apply-reset', { method: 'POST' }).catch(() => {});
  };

  const checkedCount = items.filter(i => i.checked).length;
  const pendingCount = items.filter(i => !i.checked).length;

  // Group by timestamp
  const grouped: Record<string, ImprovementItem[]> = {};
  for (const item of items) {
    if (!grouped[item.timestamp]) grouped[item.timestamp] = [];
    grouped[item.timestamp].push(item);
  }

  if (loading) {
    return <div className="text-gray-500 text-center py-12">Loading improvements...</div>;
  }

  return (
    <div className="max-w-7xl mx-auto px-3 sm:px-6 py-4">
      <PageHead
        eyebrow="Whetstone // self"
        title="System"
        sub={items.length === 0 ? 'No suggestions yet' : `${checkedCount} approved, ${pendingCount} pending review`}
        left={<div style={{ paddingBottom: 5 }}><ProjectPicker projects={projects} project={project} onChange={setProject} /></div>}
        right={
          <PrimaryButton
            onClick={handleApply}
            disabled={checkedCount === 0 || !!applyStatus?.running}
          >
            {applyStatus?.running ? 'Applying…' : `Apply (${checkedCount})`}
          </PrimaryButton>
        }
      />

      {/* Apply status banner */}
      {applyStatus && (
        <div className={`mb-6 rounded-lg border p-4 text-sm ${
          applyStatus.running
            ? 'bg-blue-900/20 border-blue-700/40'
            : applyStatus.error
              ? 'bg-red-900/20 border-red-700/40'
              : applyStatus.success
                ? 'bg-emerald-900/20 border-emerald-700/40'
                : 'bg-gray-800 border-hair'
        }`}>
          <div className="flex items-center justify-between mb-2">
            {applyStatus.running && (
              <div className="flex items-center gap-2 text-blue-300">
                <div className="h-2 w-2 bg-blue-400 rounded-full animate-pulse" />
                Running apply-improvements...
              </div>
            )}
            {!applyStatus.running && applyStatus.error && (
              <span className="text-red-300">Failed: {applyStatus.error}</span>
            )}
            {!applyStatus.running && applyStatus.success && (
              <span className="text-emerald-300">Improvements applied successfully</span>
            )}
            {!applyStatus.running && (
              <button
                onClick={dismissStatus}
                className="text-xs text-gray-400 hover:text-gray-200 transition-colors"
              >
                Dismiss
              </button>
            )}
          </div>
          {applyStatus.output && (
            <pre className="whitespace-pre-wrap font-mono text-xs text-gray-300 mt-2 max-h-60 overflow-y-auto bg-gray-900/50 rounded p-3">
              {applyStatus.output}
            </pre>
          )}
        </div>
      )}

      {error && <div className="text-red-400 text-sm mb-4">{error}</div>}

      {/* Two-column layout: Suggestions | Lessons Learned */}
      <div className="grid grid-cols-1 lg:grid-cols-5 gap-6">
        {/* Left: Suggestions (3/5) */}
        <div className="lg:col-span-3">
          <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-3">
            Suggestions
          </h3>
          {items.length === 0 ? (
            <div className="text-center py-16 bg-gray-800 border border-hair rounded-lg">
              <p className="text-gray-500">No improvement suggestions yet.</p>
              <p className="text-xs text-gray-600 mt-2">
                Suggestions are generated automatically after each heartbeat cycle.
              </p>
            </div>
          ) : (
            <div className="space-y-3">
              {Object.entries(grouped).map(([timestamp, groupItems]) => (
                <div key={timestamp} className="bg-gray-800 border border-hair rounded-lg overflow-hidden">
                  <div className="px-4 py-2 bg-gray-800/80 border-b border-hair">
                    <span className="text-xs font-mono text-gray-500">{timestamp}</span>
                  </div>
                  <div className="divide-y divide-hairsoft">
                    {groupItems.map(item => (
                      <div
                        key={item.id}
                        className={`flex items-start gap-3 px-4 py-3 transition-colors group ${
                          item.checked
                            ? 'bg-emerald-900/10 hover:bg-emerald-900/20'
                            : 'hover:bg-gray-750'
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={item.checked}
                          onChange={e => toggleItem(item.id, e.target.checked)}
                          className="mt-0.5 h-4 w-4 shrink-0 rounded border-gray-600 bg-gray-700 cursor-pointer accent-emerald-500"
                          style={{ colorScheme: 'dark' }}
                        />
                        <select
                          value={item.category}
                          onChange={e => setCategory(item.id, e.target.value as Category)}
                          onClick={e => e.stopPropagation()}
                          className={`shrink-0 mt-px py-0.5 pl-1.5 pr-5 text-[10px] font-semibold uppercase tracking-wider rounded border-0 cursor-pointer appearance-none bg-no-repeat transition-colors focus:outline-none focus:ring-1 focus:ring-gray-500 ${CATEGORY_STYLE[item.category] ?? CATEGORY_STYLE.lesson}`}
                          style={{
                            colorScheme: 'dark',
                            backgroundImage:
                              "url(\"data:image/svg+xml,%3csvg xmlns='http://www.w3.org/2000/svg' fill='none' viewBox='0 0 20 20'%3e%3cpath stroke='%239ca3af' stroke-linecap='round' stroke-linejoin='round' stroke-width='2' d='M6 8l4 4 4-4'/%3e%3c/svg%3e\")",
                            backgroundPosition: 'right 0.1rem center',
                            backgroundSize: '0.9em 0.9em',
                          }}
                          title="Facts (environment, codebase, lesson) go to the project's notes store. Pipeline changes (process, system) edit a skill or prompt."
                        >
                          {CATEGORIES.map(c => (
                            <option key={c} value={c} className="bg-gray-800 text-gray-200">
                              {c}
                            </option>
                          ))}
                        </select>
                        {editingId === item.id ? (
                          <input
                            autoFocus
                            value={editText}
                            onChange={e => setEditText(e.target.value)}
                            onBlur={() => saveEdit(item.id)}
                            onKeyDown={e => {
                              if (e.key === 'Enter') saveEdit(item.id);
                              if (e.key === 'Escape') cancelEdit();
                            }}
                            className="flex-1 text-sm leading-relaxed bg-gray-900 border border-gray-600 rounded px-2 py-1 text-gray-200 focus:outline-none focus:border-gray-400"
                          />
                        ) : (
                          <span
                            className={`flex-1 text-sm leading-relaxed ${
                              item.checked ? 'text-emerald-300' : 'text-gray-300'
                            }`}
                            onClick={() => toggleItem(item.id, !item.checked)}
                            onDoubleClick={e => { e.stopPropagation(); startEdit(item); }}
                            title="Double-click to edit"
                          >
                            {item.text}
                          </span>
                        )}
                        {(() => {
                          const age = ageBadge(item.ageDays);
                          if (!age) return null;
                          return (
                            <span
                              className={`shrink-0 mt-0.5 text-[10px] font-mono tabular-nums ${age.className}`}
                              title={`Filed ${item.filedAt?.slice(0, 16).replace('T', ' ')} - ${item.ageDays} days unactioned`}
                            >
                              {age.label}
                            </span>
                          );
                        })()}
                        <button
                          onClick={() => deleteItem(item.id)}
                          className="shrink-0 mt-0.5 text-gray-600 hover:text-red-400 opacity-0 group-hover:opacity-100 transition-all"
                          title="Delete suggestion"
                        >
                          <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                          </svg>
                        </button>
                      </div>
                    ))}
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>

        {/* Right: what this project knows (2/5) */}
        <div className="lg:col-span-2">
          <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-1">
            What this project knows
          </h3>
          <p className="text-xs text-gray-500 mb-3 font-mono break-all">
            {knowledgePath || 'no notes store configured'}
          </p>
          <div className="bg-gray-800 border border-hair rounded-lg overflow-hidden sticky top-[72px]">
            <pre className="p-4 text-sm text-gray-300 leading-relaxed whitespace-pre-wrap font-mono overflow-y-auto max-h-[calc(100vh-200px)]">
              {knowledge || 'Nothing recorded yet. Durable facts live in the project repo, one file each, indexed by MEMORY.md.'}
            </pre>
          </div>
        </div>
      </div>
    </div>
  );
}
