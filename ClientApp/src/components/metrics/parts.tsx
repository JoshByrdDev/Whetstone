import { useEffect, useState, type ReactNode } from 'react';
import { SectionRule } from '../ui';
import { chamfer, MONO } from '../orbit';

/**
 * The small pieces every section of the Metrics page is built from: how a number is written, what
 * each part of a split is called and coloured, and the few marks the page draws with.
 *
 * Colours are Tailwind's accent families, which tailwind.config.js points at the six tiers - so a
 * swatch here follows the theme like everything else, and the page never names a hex.
 */

// ---- numbers ----------------------------------------------------------------------

export function compact(n: number | null | undefined): string {
  if (n === null || n === undefined || Number.isNaN(n)) return '—';
  const a = Math.abs(n);
  if (a >= 1_000_000) return `${(n / 1_000_000).toFixed(a < 10_000_000 ? 1 : 0)}M`;
  if (a >= 1_000) return `${(n / 1_000).toFixed(a < 10_000 ? 1 : 0)}k`;
  return `${Math.round(n)}`;
}

/** Minutes as the page writes a duration: 40s, 6.2m, 3.1h. */
export function mins(m: number | null | undefined): string {
  if (m === null || m === undefined || Number.isNaN(m)) return '—';
  if (m < 1) return `${Math.round(m * 60)}s`;
  if (m < 10) return `${m.toFixed(1)}m`;
  if (m < 60) return `${Math.round(m)}m`;
  return `${(m / 60).toFixed(m < 600 ? 1 : 0)}h`;
}

export const secs = (s: number | null | undefined) =>
  s === null || s === undefined || Number.isNaN(s) ? '—' : s < 10 ? `${s.toFixed(1)}s` : mins(s / 60);

export function pct(f: number | null | undefined, digits = 0): string {
  if (f === null || f === undefined || Number.isNaN(f)) return '—';
  return `${(100 * f).toFixed(digits)}%`;
}

export function ago(iso: string | null | undefined): string {
  if (!iso) return 'never';
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  if (s < 90) return 'just now';
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86400 * 2) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86400)}d ago`;
}

export const day = (iso: string | null | undefined) => (iso ? iso.slice(0, 10) : '—');

// ---- names --------------------------------------------------------------------------

const PHASE: Record<string, string> = {
  NeedsSpeculation: 'Needs spec', Speculating: 'Speculate', NeedsApproval: 'Approval',
  NeedsImplementation: 'Needs impl', Implementing: 'Implement', NeedsImplementationReview: 'Impl review',
  NeedsQa: 'Needs QA', QaTesting: 'QA', NeedsRefinement: 'Needs refine', Refining: 'Refine',
  NeedsReview: 'Review', Backlog: 'Backlog', Done: 'Done', Blocked: 'Blocked',
};
export const phaseName = (p: string | null | undefined) => (p ? PHASE[p] ?? p : '—');

/** The pipeline's order, so phases list the way tickets travel. */
const ORDER = ['Speculating', 'Implementing', 'QaTesting', 'Refining', 'NeedsReview'];
export const byPipeline = (a: string, b: string) =>
  (ORDER.indexOf(a) + 1 || 99) - (ORDER.indexOf(b) + 1 || 99) || a.localeCompare(b);

// ---- the time split -----------------------------------------------------------------

export interface Part { key: string; label: string; swatch: string; note: string }

/** The parts of a run's wall clock, left to right as they happen. */
export const TIME_PARTS: Part[] = [
  { key: 'prep', label: 'Prep', swatch: 'bg-sky-500', note: 'worktree, setup, start rebase' },
  { key: 'startup', label: 'Startup', swatch: 'bg-sky-300', note: 'agent launching, MCP servers connecting' },
  { key: 'model', label: 'Model', swatch: 'bg-amber-500', note: 'thinking and writing between calls' },
  { key: 'tool', label: 'Tools', swatch: 'bg-emerald-500', note: 'some tool running (parallel calls counted once)' },
  { key: 'agentUnsplit', label: 'Agent, unsplit', swatch: 'bg-amber-300', note: 'agent time with no transcript to split it (worker host)' },
  { key: 'handoff', label: 'Hand-off', swatch: 'bg-violet-500', note: 'commit, squash, hand-off rebase, branch hand-back' },
  { key: 'overhead', label: 'Overhead, unsplit', swatch: 'bg-gray-500', note: 'prep + startup + hand-off, before the worker measured them' },
  { key: 'unmeasured', label: 'Unmeasured', swatch: 'bg-gray-700 ring-1 ring-inset ring-gray-500', note: 'no transcript and no stage timings' },
];

// ---- why a run happened ---------------------------------------------------------------

export const REASONS: Part[] = [
  { key: 'forward', label: 'First time', swatch: 'bg-emerald-500', note: 'came forward from the phase before' },
  { key: 'qa-failed', label: 'QA failed it', swatch: 'bg-red-500', note: 'QA sent it back' },
  { key: 'refine-sent-back', label: 'Refine sent back', swatch: 'bg-red-300', note: 'refinement found gaps' },
  { key: 'review-changes', label: 'Review changes', swatch: 'bg-sky-500', note: 'a reviewer asked for changes' },
  { key: 'conflict', label: 'Merge conflict', swatch: 'bg-violet-500', note: 'the branch no longer merged with its base' },
  { key: 'resumed', label: 'Resumed', swatch: 'bg-amber-500', note: 'an earlier run ended without handing off' },
  { key: 'unblocked', label: 'Unblocked', swatch: 'bg-amber-300', note: 'was blocked, then let go' },
  { key: 'unknown', label: 'Unknown', swatch: 'bg-gray-700 ring-1 ring-inset ring-gray-500', note: 'nothing in the log before it' },
];
export const reasonOf = (k: string) => REASONS.find(r => r.key === k) ?? REASONS[REASONS.length - 1];

/** The dimensions a phase can be split by, as the breakdown mart names them. */
export const DIMENSIONS: { key: string; label: string; filter: string }[] = [
  { key: 'model', label: 'Model', filter: 'model' },
  { key: 'effort', label: 'Effort', filter: 'effort' },
  { key: 'provider', label: 'Provider', filter: 'provider' },
  { key: 'skill_version', label: 'Skill version', filter: 'skill_version' },
  { key: 'prompt_version', label: 'Prompt version', filter: 'prompt_version' },
  { key: 'entry_reason', label: 'Why it ran', filter: 'entry_reason' },
  { key: 'pass', label: 'Pass', filter: 'pass' },
  { key: 'cli_version', label: 'CLI version', filter: 'cli_version' },
  { key: 'experiment_arm', label: 'A/B arm', filter: 'experiment_arm' },
  { key: 'machine', label: 'Machine', filter: 'machine' },
  { key: 'machine_load', label: 'Machine, by load', filter: 'machine_load' },
];

/** A machine as a person calls it: the dispatch order's `@board` is this computer. */
export const machineName = (m: string | null | undefined) =>
  !m || m === 'unknown' ? 'Unknown' : m === '@board' ? 'This machine' : m;

// ---- marks ------------------------------------------------------------------------------

/** One headline number. A single figure is a tile, not a chart. */
export function Stat({ label, value, unit, context, muted }: {
  label: string; value: string; unit?: string; context?: ReactNode; muted?: boolean;
}) {
  return (
    <div className="min-w-0 flex-1 basis-[150px] border border-hair bg-panel px-4 py-3" style={{ clipPath: chamfer(10) }}>
      <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">{label}</div>
      <div className={`mt-1.5 ${muted ? 'text-gray-600' : 'text-gray-100'}`} style={{ fontFamily: MONO }}>
        <span className="text-2xl font-semibold">{value}</span>
        {unit && <span className="ml-1 text-sm text-gray-500">{unit}</span>}
      </div>
      {context && <div className="mt-1 text-[11px] leading-snug text-gray-500">{context}</div>}
    </div>
  );
}

/** A section of the page: its rule, and a note under it when there is something to say. */
export function Section({ title, meta, right, children, id }: {
  title: string; meta?: string; right?: ReactNode; children: ReactNode; id?: string;
}) {
  return (
    <section id={id} className="min-w-0">
      <SectionRule title={title} meta={meta} colour="var(--w-queue)" right={right} />
      {children}
    </section>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <p className="text-xs leading-relaxed text-gray-500">{children}</p>;
}

/** A chamfered sheet with a hairline - the surface every table and chart on the page sits on. */
export function Sheet({ children, className = '', pad = true }: { children: ReactNode; className?: string; pad?: boolean }) {
  return (
    <div className={`min-w-0 border border-hair bg-panel ${pad ? 'p-4' : ''} ${className}`} style={{ clipPath: chamfer(10) }}>
      {children}
    </div>
  );
}

/**
 * A part-to-whole bar. Segments carry their own label in the hover text and in `aria-label`; the
 * legend beside it is the table, so nothing is told by colour alone. `width` is the bar's share of
 * its row, for a set of bars drawn to one scale.
 */
export function SegBar({ parts, width = 1, height = 'h-3' }: {
  parts: { key: string; label: string; swatch: string; value: number; title?: string }[];
  width?: number; height?: string;
}) {
  const shown = parts.filter(p => p.value > 0);
  return (
    <div className="flex w-full items-center" role="img" aria-label={shown.map(p => `${p.label} ${p.title ?? ''}`).join(', ')}>
      <div className={`flex ${height} gap-[2px]`} style={{ width: `${Math.max(0.5, Math.min(1, width)) * 100}%` }}>
        {shown.map((p, i) => (
          <div
            key={p.key}
            className={`${p.swatch} h-full ${i === 0 ? 'rounded-l-[4px]' : ''} ${i === shown.length - 1 ? 'rounded-r-[4px]' : ''}`}
            style={{ flexGrow: p.value, flexBasis: 0, minWidth: 2 }}
            title={`${p.label}${p.title ? `: ${p.title}` : ''}`}
          />
        ))}
      </div>
    </div>
  );
}

/** A single magnitude, on a scale the caller shares across rows. */
export function Meter({ value, max, swatch = 'bg-sky-500', title }: { value: number; max: number; swatch?: string; title?: string }) {
  const w = max > 0 ? Math.max(value > 0 ? 2 : 0, (100 * value) / max) : 0;
  return (
    <div className="flex h-3 min-w-0 flex-1 items-center" title={title}>
      {w > 0 && <div className={`h-2 rounded-r-[4px] ${swatch}`} style={{ width: `${w}%` }} />}
    </div>
  );
}

export function Swatch({ swatch }: { swatch: string }) {
  return <span className={`h-2.5 w-2.5 shrink-0 rounded-[2px] ${swatch}`} aria-hidden />;
}

/** A filter on what is shown, said in words, with a way to drop it. */
export function FilterChip({ label, onClear }: { label: string; onClear: () => void }) {
  return (
    <button onClick={onClear} className="orbit-tap inline-flex min-w-0 items-center gap-1.5 border border-hair bg-sunk px-2 py-0.5 text-[11px] text-gray-300"
      style={{ clipPath: chamfer(6) }} title="Clear this filter">
      <span className="min-w-0 truncate">{label}</span>
      <span className="text-gray-500" aria-hidden>×</span>
    </button>
  );
}

/** Fetch JSON for a URL, again whenever `url` or `bump` changes. Null while loading or when it failed. */
export function useJson<T>(url: string | null, bump = 0): { data: T | null; error: string | null } {
  const [state, setState] = useState<{ data: T | null; error: string | null }>({ data: null, error: null });
  useEffect(() => {
    if (!url) { setState({ data: null, error: null }); return; }
    let live = true;
    fetch(url)
      .then(r => (r.ok ? r.json() : Promise.reject(new Error(`${r.status} ${r.statusText}`))))
      .then(d => { if (live) setState({ data: d, error: null }); })
      .catch(e => { if (live) setState({ data: null, error: String(e) }); });
    return () => { live = false; };
  }, [url, bump]);
  return state;
}

/** Table cells, spelled once so every table on the page lines up the same way. */
export const TH = 'px-3 py-2 font-medium';
export const TD = 'px-3 py-2';
export const NUM = 'text-right';
