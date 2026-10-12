import { useState, useEffect } from 'react';
import { PageHead, Chip } from './ui';

/**
 * What the heartbeat has actually done, newest first.
 *
 * One row per worker run — the ticket it worked, the phase it ran, where it handed the ticket on,
 * and what it spent doing it. The metrics tab aggregates the same runs; this one keeps them in
 * chronological order so a day can be read back as it happened.
 *
 * Rows marked "no run record" were recovered from the event log because the run never filed a
 * report of its own. They carry the ticket and phase and nothing else, and they say so rather than
 * showing zeroes that would read like measurements.
 */

interface Cycle {
  startTime: string;
  endTime: string | null;
  durationSeconds: number | null;
  ticketId: number | null;
  project: string | null;
  phase: string | null;
  phaseAfter: string | null;
  skill: string | null;
  resumed: boolean;
  model: string | null;
  outcome: string | null;
  mutations: number | null;
  numTurns: number | null;
  tokens: number | null;
  cacheReadTokens: number | null;
  partial: boolean;
}

interface PhaseCount {
  phase: string;
  count: number;
}

interface Stats {
  totalCycles: number;
  cyclesToday: number;
  ticketsToday: number;
  avgDurationSeconds: number;
  tokensToday: number;
  completed: number;
  timeouts: number;
  errors: number;
  partialCycles: number;
  topPhase: string | null;
  phaseBreakdown: PhaseCount[];
}

interface HistoryData {
  days: number;
  cycles: Cycle[];
  stats: Stats;
}

// The four phases a worker ever runs in. Anything else is a resting phase the board moves tickets
// through, and it gets the neutral badge rather than a colour that implies work happened in it.
const PHASE_COLORS: Record<string, string> = {
  Speculating: 'bg-blue-500/20 text-blue-400',
  Implementing: 'bg-emerald-500/20 text-emerald-400',
  QaTesting: 'bg-amber-500/20 text-amber-400',
  Refining: 'bg-violet-500/20 text-violet-400',
};

const PHASE_SHORT: Record<string, string> = {
  NeedsSpeculation: 'Needs Spec', Speculating: 'Speculating', NeedsApproval: 'Approval',
  NeedsImplementation: 'Needs Impl', Implementing: 'Implementing',
  NeedsImplementationReview: 'Impl Review', NeedsQa: 'Needs QA', QaTesting: 'QA Testing',
  NeedsRefinement: 'Needs Refine', Refining: 'Refining', NeedsReview: 'Review',
  Backlog: 'Backlog', Done: 'Done', Blocked: 'Blocked',
};
const short = (p: string) => PHASE_SHORT[p] ?? p;

function formatDuration(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  if (m < 60) return s > 0 ? `${m}m ${s}s` : `${m}m`;
  return `${(m / 60).toFixed(1)}h`;
}

function compact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(n < 10_000_000 ? 1 : 0)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(n < 10_000 ? 1 : 0)}k`;
  return `${Math.round(n)}`;
}

function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function formatDate(iso: string): string {
  const d = new Date(iso);
  const today = new Date();
  const yesterday = new Date(today);
  yesterday.setDate(yesterday.getDate() - 1);

  if (d.toDateString() === today.toDateString()) return 'Today';
  if (d.toDateString() === yesterday.toDateString()) return 'Yesterday';
  return d.toLocaleDateString([], { month: 'short', day: 'numeric' });
}

function StatCard({ label, value, sub }: { label: string; value: string | number; sub?: string }) {
  return (
    <div className="bg-gray-800 border border-hair rounded-lg px-4 py-3">
      <p className="text-xs text-gray-500 uppercase tracking-wider">{label}</p>
      <p className="text-2xl font-bold text-gray-100 mt-1">{value}</p>
      {sub && <p className="text-xs text-gray-500 mt-0.5">{sub}</p>}
    </div>
  );
}

function Badge({ className, children }: { className: string; children: React.ReactNode }) {
  return (
    <span className={`px-1.5 py-0.5 text-[10px] font-semibold rounded uppercase tracking-wider ${className}`}>
      {children}
    </span>
  );
}

/** One run. Everything past the ticket and phase is absent on a recovered row, hence the guards. */
function CycleRow({ cycle }: { cycle: Cycle }) {
  const failed = cycle.outcome === 'Timeout' || cycle.outcome === 'Error';
  const movedOn = cycle.phaseAfter && cycle.phaseAfter !== cycle.phase;

  return (
    <div className="px-4 py-2.5 hover:bg-gray-750 transition-colors">
      <div className="flex items-center gap-3 flex-wrap">
        <span className="text-sm font-mono text-gray-400 w-14 shrink-0">
          {formatTime(cycle.startTime)}
        </span>
        <span className="text-xs font-mono text-gray-500 w-14 shrink-0">
          {cycle.durationSeconds === null ? '—' : formatDuration(cycle.durationSeconds)}
        </span>

        <span className={`text-sm font-mono shrink-0 ${cycle.partial ? 'text-gray-500' : 'text-gray-300'}`}>
          {cycle.ticketId === null ? '—' : `#${cycle.ticketId}`}
        </span>
        {cycle.project && <span className="text-xs text-gray-600">{cycle.project}</span>}

        {cycle.phase && (
          <Badge className={PHASE_COLORS[cycle.phase] ?? 'bg-gray-600/30 text-gray-400'}>
            {short(cycle.phase)}
          </Badge>
        )}
        {cycle.resumed && <span className="text-[10px] text-gray-600 uppercase tracking-wider">resumed</span>}

        {failed && (
          <Badge className="bg-red-500/20 text-red-400">
            {cycle.outcome === 'Timeout' ? 'timed out' : 'failed'}
          </Badge>
        )}
        {cycle.endTime === null && !cycle.partial && (
          <Badge className="bg-gray-600/30 text-gray-400">no end</Badge>
        )}
        {cycle.partial && (
          <span
            className="text-[10px] text-gray-600 uppercase tracking-wider"
            title="Recovered from the event log — this run never filed a report, so only the ticket and phase are known."
          >
            {cycle.endTime === null ? 'no end recorded' : 'no run record'}
          </span>
        )}

        {/* Spend sits right-aligned so a long day scans as a column of numbers. */}
        <span className="ml-auto flex items-center gap-3 text-xs font-mono text-gray-500">
          {movedOn && <span className="text-gray-400">&rarr; {short(cycle.phaseAfter!)}</span>}
          {cycle.numTurns !== null && <span title="turns">{cycle.numTurns}t</span>}
          {cycle.tokens !== null && cycle.tokens > 0 && (
            <span title={`${cycle.tokens.toLocaleString()} tokens in + out + cache writes` +
              (cycle.cacheReadTokens ? `, ${cycle.cacheReadTokens.toLocaleString()} read from cache` : '')}>
              {compact(cycle.tokens)}
            </span>
          )}
          {cycle.mutations !== null && cycle.mutations > 0 && (
            <span className="text-gray-600" title="ticket-mutating tool calls">
              {cycle.mutations} edit{cycle.mutations === 1 ? '' : 's'}
            </span>
          )}
        </span>
      </div>
    </div>
  );
}

export function HistoryPanel() {
  const [data, setData] = useState<HistoryData | null>(null);
  const [loading, setLoading] = useState(true);
  const [days, setDays] = useState(7);

  useEffect(() => {
    let live = true;
    setLoading(true);
    fetch(`/api/history?days=${days}`)
      .then(r => r.json())
      .then(d => { if (live) setData(d); })
      .catch(() => { if (live) setData(null); })
      .finally(() => { if (live) setLoading(false); });
    return () => { live = false; };
  }, [days]);

  if (loading) {
    return <div className="text-gray-500 text-center py-12">Loading history...</div>;
  }

  if (!data) {
    return <div className="text-red-400 text-center py-12">Failed to load history</div>;
  }

  const { cycles, stats } = data;

  // Group runs by the day they started, in the reader's timezone.
  const grouped: Record<string, Cycle[]> = {};
  for (const cycle of cycles) {
    const key = new Date(cycle.startTime).toDateString();
    (grouped[key] ??= []).push(cycle);
  }

  const trouble = [
    stats.timeouts > 0 ? `${stats.timeouts} timed out` : null,
    stats.errors > 0 ? `${stats.errors} failed` : null,
  ].filter(Boolean).join(', ');

  return (
    <div className="max-w-7xl mx-auto px-3 sm:px-6 py-4">
      <PageHead
        eyebrow="Whetstone // fleet"
        title="History"
        sub={<>
          {stats.totalCycles} run{stats.totalCycles === 1 ? '' : 's'} in the last {days} days
          {trouble && <span style={{ color: 'var(--w-blocked-300)' }}> · {trouble}</span>}
          {stats.partialCycles > 0 && (
            <span className="text-gray-600"> · {stats.partialCycles} with no run record</span>
          )}
        </>}
        right={[1, 3, 7, 14, 30].map(d => (
          <Chip key={d} on={days === d} onClick={() => setDays(d)}>{d}d</Chip>
        ))}
      />

      {/* Stats Cards */}
      <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-6">
        <StatCard label="Runs Today" value={stats.cyclesToday} />
        <StatCard label="Tickets Today" value={stats.ticketsToday} />
        <StatCard
          label="Avg Duration"
          value={stats.avgDurationSeconds > 0 ? formatDuration(stats.avgDurationSeconds) : '--'}
          sub={`over ${days} days`}
        />
        <StatCard
          label="Tokens Today"
          value={stats.tokensToday > 0 ? compact(stats.tokensToday) : '--'}
          sub="in + out + cache writes"
        />
      </div>

      {/* Which phases the window was spent in. A single figure ("top phase") hid the shape. */}
      {stats.phaseBreakdown.length > 0 && (
        <div className="flex items-center gap-2 flex-wrap mb-6">
          {stats.phaseBreakdown.map(p => (
            <span key={p.phase} className="flex items-center gap-1.5">
              <Badge className={PHASE_COLORS[p.phase] ?? 'bg-gray-600/30 text-gray-400'}>
                {short(p.phase)}
              </Badge>
              <span className="text-xs font-mono text-gray-500">{p.count}</span>
            </span>
          ))}
        </div>
      )}

      {/* Cycle List */}
      {cycles.length === 0 ? (
        <div className="text-center py-16 bg-gray-800 border border-hair rounded-lg">
          <p className="text-gray-500">No worker runs in the last {days} days.</p>
          <p className="text-xs text-gray-600 mt-2">
            Runs appear here as the heartbeat works tickets.
          </p>
        </div>
      ) : (
        <div className="space-y-4">
          {Object.entries(grouped).map(([dateKey, dayCycles]) => (
            <div key={dateKey}>
              <h3 className="text-xs font-semibold text-gray-500 uppercase tracking-wider mb-2">
                {formatDate(dayCycles[0].startTime)} — {dayCycles.length} run{dayCycles.length !== 1 ? 's' : ''}
              </h3>
              <div className="bg-gray-800 border border-hair rounded-lg overflow-hidden">
                <div className="divide-y divide-hairsoft">
                  {dayCycles.map((cycle, idx) => (
                    <CycleRow key={`${cycle.startTime}-${idx}`} cycle={cycle} />
                  ))}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
