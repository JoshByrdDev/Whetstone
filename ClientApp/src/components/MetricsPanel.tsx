import { useEffect, useState } from 'react';
import { useProjectList, ProjectPicker } from './ProjectPicker';
import { UsageMeters } from './ClaudeUsage';
import { PageHead, Chip, SectionRule } from './ui';

/**
 * What the pipeline actually costs and how often it has to redo its work.
 *
 * Two independent sources feed this, and they fill up at different speeds. Phase transitions go back
 * to the day the board was created (recovered from the old comment prose), so the quality numbers
 * are meaningful immediately. Cycle runs only exist from the moment the heartbeat started reporting
 * them, so the cost numbers stay empty until it has run - which the panel says outright rather than
 * drawing a chart full of zeroes that reads like a finding.
 *
 * No money anywhere on this page. The system runs on a Max subscription with no per-call billing, so
 * the scarce resource is rate-limit headroom, and headroom is tokens and time.
 */

interface PhaseBounce { from: string; total: number; backward: number; rate: number }
interface PhaseCost {
  phase: string; runs: number; medianMinutes: number; medianTokens: number;
  completed: number; timeouts: number; errors: number;
}
interface DayTokens {
  day: string; input: number; output: number; cacheRead: number; cacheCreate: number;
  runs: number; minutes: number;
}
interface TokenTotals { input: number; output: number; cacheRead: number; cacheCreate: number; runs: number; minutes: number }
interface Quality {
  qaAttempted: number; firstPassQa: number; totalTransitions: number; totalBackward: number;
  backfilledTransitions: number; blocked: number; reachedDone: number;
}
interface Summary {
  days: number; project: string | null; hasCycleData: boolean; cycleCount: number;
  bounces: PhaseBounce[]; phaseCosts: PhaseCost[]; daily: DayTokens[];
  last5Hours: TokenTotals; last24Hours: TokenTotals; quality: Quality;
}
interface Cycle {
  id: number; ticketId: number; phase: string; skill: string; resumed: boolean;
  startedAt: string; durationSeconds: number; numTurns: number | null;
  inputTokens: number | null; outputTokens: number | null; cacheReadTokens: number | null;
  mutations: number; outcome: string; phaseAfter: string | null;
}

const WINDOWS = [7, 30, 90];

/**
 * The page box every other tab uses. This one was the odd one out — it capped its width without
 * centring, so the whole page hugged the left edge and its content jumped sideways whenever you
 * arrived from History or Skills.
 */
const PAGE = 'max-w-7xl mx-auto px-3 sm:px-6 py-4';

function compact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n < 10_000_000 ? 1 : 0)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(n < 10_000 ? 1 : 0)}k`;
  return `${Math.round(n)}`;
}

function minutes(m: number): string {
  if (m < 1) return '<1m';
  if (m < 60) return `${Math.round(m)}m`;
  return `${(m / 60).toFixed(1)}h`;
}

const PHASE_SHORT: Record<string, string> = {
  NeedsSpeculation: 'Needs Spec', Speculating: 'Speculating', NeedsApproval: 'Approval',
  NeedsImplementation: 'Needs Impl', Implementing: 'Implementing',
  NeedsImplementationReview: 'Impl Review', NeedsQa: 'Needs QA', QaTesting: 'QA Testing',
  NeedsRefinement: 'Needs Refine', Refining: 'Refining', NeedsReview: 'Review',
  Backlog: 'Backlog', Done: 'Done', Blocked: 'Blocked',
};
const short = (p: string) => PHASE_SHORT[p] ?? p;

/** One headline number. A single figure is a tile, not a chart - drawing it would add nothing. */
function Stat({ label, value, unit, context, muted }: {
  label: string; value: string; unit?: string; context?: string; muted?: boolean;
}) {
  return (
    <div className="flex-1 min-w-[150px] bg-panel border border-hair rounded-lg px-4 py-3">
      <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">{label}</div>
      <div className={`mt-1.5 font-mono ${muted ? 'text-gray-600' : 'text-gray-100'}`}>
        <span className="text-2xl font-semibold">{value}</span>
        {unit && <span className="text-sm text-gray-500 ml-1">{unit}</span>}
      </div>
      {context && <div className="mt-1 text-[11px] text-gray-500 leading-snug">{context}</div>}
    </div>
  );
}

/** The board's zone heading, with the note carried along as its meta line. */
function SectionLabel({ children, note }: { children: string; note?: string }) {
  return <SectionRule title={children} meta={note} colour="var(--w-queue)" />;
}

function Empty({ children }: { children: React.ReactNode }) {
  return <p className="text-xs text-gray-600 leading-relaxed">{children}</p>;
}

export function MetricsPanel() {
  const [projects, project, setProject] = useProjectList();
  const [days, setDays] = useState(30);
  const [data, setData] = useState<Summary | null>(null);
  const [cycles, setCycles] = useState<Cycle[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const q = new URLSearchParams({ days: String(days) });
    if (project) q.set('project', project);
    let live = true;
    setError(null);
    fetch(`/api/metrics?${q}`)
      .then(r => r.json())
      .then(d => { if (live) setData(d); })
      .catch(e => { if (live) setError(String(e)); });
    fetch(`/api/metrics/cycles?${q}&limit=20`)
      .then(r => r.json())
      .then(d => { if (live) setCycles(Array.isArray(d) ? d : []); })
      .catch(() => {});
    return () => { live = false; };
  }, [project, days]);

  // The waiting states sit in the same box the page will occupy, so the text does not move across
  // the screen the moment the fetch lands.
  if (error) return <div className={`${PAGE} text-sm text-red-300`}>Could not load metrics: {error}</div>;
  if (!data) return <div className={`${PAGE} text-sm text-gray-500`}>Loading…</div>;

  const q = data.quality;
  const bounceRate = q.totalTransitions > 0 ? (100 * q.totalBackward) / q.totalTransitions : 0;
  const firstPass = q.qaAttempted > 0 ? (100 * q.firstPassQa) / q.qaAttempted : 0;
  const window5h = data.last5Hours;
  const tokens5h = window5h.input + window5h.output + window5h.cacheCreate;

  // One scale for the whole bounce list, so a long bar means more work redone than a short one -
  // per-row scaling would make every phase look equally bad.
  const maxBounce = Math.max(1, ...data.bounces.map(b => b.backward));
  const maxDayTokens = Math.max(1, ...data.daily.map(d => d.input + d.output + d.cacheCreate));

  return (
    <div className={PAGE}>
      {/* Filters live in the title block: they scope everything under it, so they belong to the
          page's name rather than to the first chart that happens to sit below them. */}
      <PageHead
        eyebrow="Whetstone // cost"
        title="Metrics"
        sub={`${q.totalTransitions} phase change${q.totalTransitions === 1 ? '' : 's'} · ${data.cycleCount} worker run${data.cycleCount === 1 ? '' : 's'}`}
        left={
          <div className="flex items-center gap-2" style={{ paddingBottom: 5 }}>
            <ProjectPicker projects={projects} project={project} onChange={setProject} />
            {WINDOWS.map(d => (
              <Chip key={d} on={days === d} onClick={() => setDays(d)}>{d}d</Chip>
            ))}
          </div>
        }
      />
      <div className="space-y-6">
      {/* Account-wide and unfiltered on purpose: the rate limit is one budget shared by every
          project and by the human's own sessions, so the project/day filters above do not apply. */}
      <UsageMeters />

      <div className="flex gap-3 flex-wrap">
        <Stat
          label="Rework"
          value={q.totalTransitions > 0 ? bounceRate.toFixed(1) : '—'}
          unit={q.totalTransitions > 0 ? '%' : undefined}
          muted={q.totalTransitions === 0}
          context={q.totalTransitions > 0
            ? `${q.totalBackward} of ${q.totalTransitions} phase changes went backwards`
            : 'no phase changes in this window'}
        />
        <Stat
          label="First-pass QA"
          value={q.qaAttempted > 0 ? firstPass.toFixed(0) : '—'}
          unit={q.qaAttempted > 0 ? '%' : undefined}
          muted={q.qaAttempted === 0}
          context={q.qaAttempted > 0
            ? `${q.firstPassQa} of ${q.qaAttempted} tickets QA never sent back`
            : 'no ticket has reached QA yet'}
        />
        <Stat
          label="Tokens, last 5h"
          value={data.hasCycleData ? compact(tokens5h) : '—'}
          muted={!data.hasCycleData}
          context={data.hasCycleData
            ? `${window5h.runs} run${window5h.runs === 1 ? '' : 's'} · ${compact(window5h.cacheRead)} more from cache`
            : 'no worker runs recorded yet'}
        />
        <Stat
          label="Worker time, 24h"
          value={data.hasCycleData ? minutes(data.last24Hours.minutes) : '—'}
          muted={!data.hasCycleData}
          context={data.hasCycleData
            ? `${data.last24Hours.runs} run${data.last24Hours.runs === 1 ? '' : 's'}, wall clock`
            : 'starts filling once the heartbeat runs'}
        />
      </div>

      {/* Where work goes backwards. Magnitude across a handful of named things - bars, one scale,
          direct labels, no legend (a single series names itself in the heading). */}
      <div>
        <SectionLabel note={q.backfilledTransitions > 0
          ? `${q.backfilledTransitions} recovered from older comment history`
          : undefined}>
          Where work goes back
        </SectionLabel>
        {data.bounces.length === 0 ? (
          <Empty>No phase changes in this window.</Empty>
        ) : (
          <div className="bg-panel border border-hair rounded-lg divide-y divide-hairsoft">
            {data.bounces.map(b => (
              <div key={b.from} className="flex items-center gap-3 px-4 py-2 text-xs">
                <span className="w-28 shrink-0 text-gray-300">{short(b.from)}</span>
                <div className="flex-1 h-4 flex items-center" title={`${b.backward} of ${b.total} moves out of ${short(b.from)} went backwards`}>
                  {b.backward > 0 && (
                    <div
                      className="h-2 rounded-r-[4px] bg-sky-500/70"
                      style={{ width: `${Math.max(2, (100 * b.backward) / maxBounce)}%` }}
                    />
                  )}
                </div>
                <span className="w-24 shrink-0 text-right font-mono text-gray-500">
                  {b.backward}/{b.total}
                </span>
                <span className={`w-12 shrink-0 text-right font-mono ${b.backward > 0 ? 'text-gray-200' : 'text-gray-600'}`}>
                  {b.rate}%
                </span>
              </div>
            ))}
          </div>
        )}
      </div>

      {/* Several measures per phase - that is a table, not a chart. */}
      <div>
        <SectionLabel note="median per run">What each phase costs</SectionLabel>
        {!data.hasCycleData ? (
          <Empty>
            No worker runs recorded yet. These fill in as the heartbeat works tickets — each run
            reports its own duration, turns and token usage when it finishes.
          </Empty>
        ) : (
          <div className="bg-panel border border-hair rounded-lg overflow-x-auto">
            <table className="w-full text-xs">
              <thead>
                <tr className="text-gray-500 border-b border-hair">
                  <th className="text-left font-medium px-4 py-2">Phase</th>
                  <th className="text-right font-medium px-4 py-2">Runs</th>
                  <th className="text-right font-medium px-4 py-2">Time</th>
                  <th className="text-right font-medium px-4 py-2">Tokens</th>
                  <th className="text-right font-medium px-4 py-2">Didn't finish</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-hairsoft">
                {data.phaseCosts.map(p => {
                  const unfinished = p.timeouts + p.errors;
                  return (
                    <tr key={p.phase}>
                      <td className="px-4 py-2 text-gray-300">{short(p.phase)}</td>
                      <td className="px-4 py-2 text-right font-mono text-gray-400">{p.runs}</td>
                      <td className="px-4 py-2 text-right font-mono text-gray-200">{minutes(p.medianMinutes)}</td>
                      <td className="px-4 py-2 text-right font-mono text-gray-200">{compact(p.medianTokens)}</td>
                      <td className="px-4 py-2 text-right font-mono">
                        {unfinished === 0
                          ? <span className="text-gray-600">none</span>
                          : <span className="text-red-300">{p.timeouts > 0 ? `${p.timeouts} timed out` : ''}{p.timeouts > 0 && p.errors > 0 ? ', ' : ''}{p.errors > 0 ? `${p.errors} failed` : ''}</span>}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Change over time, one series. Deliberately NOT stacked by token kind: cache reads run two
          orders of magnitude above input, so a stacked bar would be a cache-read chart with three
          invisible stripes on it. The split lives in the hover text instead. */}
      <div>
        <SectionLabel note="input + output + cache writes, per day">Token spend</SectionLabel>
        {!data.hasCycleData || data.daily.length === 0 ? (
          <Empty>Nothing recorded yet.</Empty>
        ) : (
          <div className="bg-panel border border-hair rounded-lg px-4 py-3">
            <div className="flex items-end gap-[2px] h-28">
              {data.daily.map(d => {
                const total = d.input + d.output + d.cacheCreate;
                return (
                  <div
                    key={d.day}
                    className="flex-1 min-w-[6px] bg-sky-500/70 hover:bg-sky-400 rounded-t-[4px] transition-colors"
                    style={{ height: `${Math.max(2, (100 * total) / maxDayTokens)}%` }}
                    title={`${d.day}\n${compact(total)} tokens (${compact(d.input)} in, ${compact(d.output)} out, ${compact(d.cacheCreate)} cache writes)\n${compact(d.cacheRead)} read from cache\n${d.runs} run${d.runs === 1 ? '' : 's'}, ${minutes(d.minutes)} wall clock`}
                  />
                );
              })}
            </div>
            <div className="mt-1.5 flex justify-between text-[10px] text-gray-600 font-mono">
              <span>{data.daily[0]?.day}</span>
              <span>{data.daily[data.daily.length - 1]?.day}</span>
            </div>
          </div>
        )}
      </div>

      {/* The rows behind the aggregates, so a surprising median can be chased down. */}
      <div>
        <SectionLabel>Recent runs</SectionLabel>
        {cycles.length === 0 ? (
          <Empty>No worker runs recorded yet.</Empty>
        ) : (
          <div className="bg-panel border border-hair rounded-lg overflow-x-auto">
            <table className="w-full text-xs">
              <thead>
                <tr className="text-gray-500 border-b border-hair">
                  <th className="text-left font-medium px-4 py-2">Ticket</th>
                  <th className="text-left font-medium px-4 py-2">Phase</th>
                  <th className="text-right font-medium px-4 py-2">Time</th>
                  <th className="text-right font-medium px-4 py-2">Turns</th>
                  <th className="text-right font-medium px-4 py-2">Tokens</th>
                  <th className="text-left font-medium px-4 py-2">Ended</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-hairsoft">
                {cycles.map(c => (
                  <tr key={c.id}>
                    <td className="px-4 py-2 font-mono text-gray-400">#{c.ticketId}</td>
                    <td className="px-4 py-2 text-gray-300">
                      {short(c.phase)}
                      {c.resumed && <span className="ml-1.5 text-[10px] text-gray-600">resumed</span>}
                    </td>
                    <td className="px-4 py-2 text-right font-mono text-gray-200">{minutes(c.durationSeconds / 60)}</td>
                    <td className="px-4 py-2 text-right font-mono text-gray-400">{c.numTurns ?? '—'}</td>
                    <td className="px-4 py-2 text-right font-mono text-gray-200">
                      {c.inputTokens === null ? '—' : compact((c.inputTokens ?? 0) + (c.outputTokens ?? 0))}
                    </td>
                    <td className="px-4 py-2">
                      {c.outcome === 'Completed'
                        ? <span className="text-gray-500">{c.phaseAfter && c.phaseAfter !== c.phase ? `→ ${short(c.phaseAfter)}` : 'still there'}</span>
                        : <span className="text-red-300">{c.outcome === 'Timeout' ? 'timed out' : 'failed'}</span>}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
      </div>
    </div>
  );
}
