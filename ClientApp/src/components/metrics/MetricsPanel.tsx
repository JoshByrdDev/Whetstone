import { useCallback, useEffect, useRef, useState } from 'react';
import { useProjectList, ProjectPicker } from '../ProjectPicker';
import { UsageMeters } from '../ClaudeUsage';
import { CapacityStrip } from '../CapacityStrip';
import { PageHead, Chip, GhostButton } from '../ui';
import { BoardMetrics } from './BoardMetrics';
import type { AnalyticsStatus, Overview, Scope } from './types';
import { scopeQuery } from './types';
import { Empty, Stat, ago, compact, mins, pct, phaseName, reasonOf, useJson } from './parts';
import { PhaseTable, Rework, TimeSplit } from './Phases';
import { DiffAndRebase, SplitBy, Tools, Versions, type RunFilter } from './PhaseDetail';
import { Daily, FirstPassQa } from './Quality';
import { RunList } from './Runs';
import { Bugs } from './Bugs';
import { ExperimentResults } from './ExperimentResults';
import { Experiments } from '../Experiments';

/**
 * The Metrics page: what each phase costs, where its time goes, how much of it is redoing work, and
 * whether a new skill or prompt made it better - read from the analytics marts (docs/analytics.md).
 *
 * The page is a funnel. The phase table picks a phase; everything under it is about that phase; any
 * number below can be clicked to list the runs behind it; any run opens to its tool calls. While the
 * marts are not set up or not built yet, the board's own numbers stand in, and say why.
 */

const PAGE = 'max-w-7xl mx-auto px-3 sm:px-6 py-4';
const WINDOWS = [{ d: 7, label: '7d' }, { d: 30, label: '30d' }, { d: 90, label: '90d' }, { d: 0, label: 'All' }];
const TRACKS = [{ t: 'Delivery', label: 'Delivery' }, { t: '*', label: 'All tracks' }];

export function MetricsPanel() {
  const [projects, project, setProject] = useProjectList('metrics');
  const [allProjects, setAllProjects] = useState(true);
  const [days, setDays] = useState(30);
  const [track, setTrack] = useState('Delivery');
  const [focus, setFocus] = useState<string | null>(null);
  const [filter, setFilter] = useState<RunFilter | null>(null);
  const [bump, setBump] = useState(0);
  const [status, setStatus] = useState<AnalyticsStatus | null>(null);
  const [log, setLog] = useState<string | null>(null);
  const asked = useRef(false);

  const scope: Scope = { days, project: allProjects ? null : project, track };

  const readStatus = useCallback(async () => {
    try {
      const r = await fetch('/api/analytics/status');
      const j = await r.json() as { status: AnalyticsStatus; log: string | null };
      setStatus(prev => {
        // A refresh just finished: everything on the page is re-read.
        if (prev?.running && !j.status.running) setBump(b => b + 1);
        return j.status;
      });
      setLog(j.log);
      return j.status;
    } catch { return null; }
  }, []);

  const refresh = useCallback(async () => {
    try {
      const r = await fetch('/api/analytics/refresh', { method: 'POST' });
      const j = await r.json() as { status: AnalyticsStatus };
      if (j.status) setStatus(j.status);
    } catch { /* the status poll says what happened */ }
  }, []);

  // On arrival: read the status, and start a refresh once if the marts are old or missing.
  useEffect(() => {
    readStatus().then(s => {
      if (!s || asked.current) return;
      asked.current = true;
      if (s.available && !s.running && (s.stale || !s.hasMarts)) void refresh();
    });
  }, [readStatus, refresh]);

  // While one runs, watch it. No timer otherwise.
  useEffect(() => {
    if (!status?.running) return;
    const t = window.setInterval(() => { void readStatus(); }, 3000);
    return () => window.clearInterval(t);
  }, [status?.running, readStatus]);

  const ready = status?.available && status.hasMarts;
  const { data, error } = useJson<Overview>(ready ? `/api/analytics/overview?${scopeQuery(scope)}` : null, bump);

  // The focus follows the data: the costliest phase until one is picked, and a pick that is no
  // longer in the window falls back too.
  const phases = data?.phases ?? [];
  const shownFocus = focus && phases.some(p => p.phase === focus) ? focus
    : [...phases].sort((a, b) => b.totalMinutes - a.totalMinutes)[0]?.phase ?? null;

  const pickPhase = (p: string) => { if (p !== shownFocus) setFilter(null); setFocus(p); };
  const pick = (f: RunFilter) => {
    setFilter(f);
    document.getElementById('metrics-runs')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  };

  if (status && !ready) {
    const notice = (
      <div className="border border-hair bg-panel px-4 py-3 text-xs leading-relaxed text-gray-400">
        {!status.available
          ? <>The detailed metrics are not set up on this machine: {status.unavailable}. These are the board's own numbers. See docs/analytics.md.</>
          : status.running
            ? <>Building the detailed metrics for the first time. This page switches over when it finishes; until then, the board's own numbers.</>
            : <>The detailed metrics have not been built yet{status.lastError ? ` (the last build failed: ${status.lastError})` : ''}.{' '}
                <button className="orbit-tap text-amber-300 underline" onClick={() => void refresh()}>Build them now</button>.</>}
      </div>
    );
    return <BoardMetrics notice={notice} />;
  }

  const all = phases.reduce((s, p) => ({
    runs: s.runs + p.runs, minutes: s.minutes + p.totalMinutes, rework: s.rework + p.reworkMinutes,
    tokens: s.tokens + p.totalTokens, unfinished: s.unfinished + p.timeouts + p.errors,
  }), { runs: 0, minutes: 0, rework: 0, tokens: 0, unfinished: 0 });
  const fp = data?.firstPass.find(r => r.dimension === 'all');
  const split = data?.timeSplit ?? [];
  const toolMin = split.reduce((s, r) => s + r.toolMinutes, 0);
  const modelMin = split.reduce((s, r) => s + r.modelMinutes, 0);
  const overMin = split.reduce((s, r) => s + r.prepMinutes + r.startupMinutes + r.handoffMinutes + r.overheadMinutes, 0);
  const splitTotal = split.reduce((s, r) => s + r.totalMinutes, 0);

  return (
    <div className={PAGE}>
      <PageHead
        eyebrow="Whetstone // efficiency"
        title="Metrics"
        sub={status?.running ? 'refreshing…' : status?.asOf ? `as of ${ago(status.asOf)}${status.transcripts ? '' : ' · without transcripts'}` : 'loading…'}
        left={
          <div className="flex min-w-0 flex-wrap items-end gap-2" style={{ paddingBottom: 5 }}>
            {projects.length > 1 && (
              <>
                <Chip on={allProjects} onClick={() => setAllProjects(true)}>All projects</Chip>
                <div onClick={() => setAllProjects(false)}>
                  <ProjectPicker projects={projects} project={project} onChange={k => { setProject(k); setAllProjects(false); }} />
                </div>
              </>
            )}
            {WINDOWS.map(w => <Chip key={w.d} on={days === w.d} onClick={() => setDays(w.d)}>{w.label}</Chip>)}
            {TRACKS.map(t => <Chip key={t.t} on={track === t.t} onClick={() => setTrack(t.t)}>{t.label}</Chip>)}
          </div>
        }
        right={
          <GhostButton onClick={() => void refresh()} disabled={!status?.available || status.running}
            title={status?.lastSeconds ? `The last refresh took ${Math.round(status.lastSeconds)}s` : undefined}>
            {status?.running ? 'Refreshing…' : 'Refresh'}
          </GhostButton>
        }
      />

      <div className="space-y-6">
        {status?.lastOk === false && status.lastError && (
          <div className="border border-hair bg-panel px-4 py-3 text-xs text-red-300" title={log ?? ''}>
            The last refresh failed: {status.lastError}. The page shows the build before it.
          </div>
        )}
        {status?.isolated && (
          <div className="text-[11px] text-gray-500">QA copy: refreshes here read the board only, never the worker transcripts.</div>
        )}

        <UsageMeters />
        <CapacityStrip days={days || 30} />

        {error && <Empty>Could not read the metrics: {error}</Empty>}

        <div className="flex flex-wrap gap-3">
          <Stat label="Worker time" value={mins(all.minutes)} context={`${all.runs} runs · ${compact(all.tokens)} tokens`} />
          <Stat label="Rework" value={all.minutes ? pct(all.rework / all.minutes) : '—'} muted={!all.minutes}
            context={`${mins(all.rework)} spent redoing work`} />
          <Stat label="First-pass QA" value={fp ? pct(fp.firstPassRate) : '—'} muted={!fp}
            context={fp ? `${fp.firstPass} of ${fp.qaAttempted} tickets never sent back` : 'no ticket reached QA'} />
          <Stat label="In tools" value={splitTotal ? pct(toolMin / splitTotal) : '—'} muted={!splitTotal}
            context={splitTotal ? `${pct(modelMin / splitTotal)} model · ${pct(overMin / splitTotal)} around the agent` : undefined} />
          <Stat label="Unfinished" value={String(all.unfinished)} muted={!all.unfinished} context="runs that timed out or failed" />
        </div>

        {data && (
          <>
            <PhaseTable phases={phases} focus={shownFocus} onFocus={pickPhase} />
            <div className="grid min-w-0 grid-cols-1 gap-6 lg:grid-cols-2">
              <TimeSplit rows={data.timeSplit} focus={shownFocus} onFocus={pickPhase} />
              <Rework rows={data.rework} focus={shownFocus}
                onPick={(p, reason) => { setFocus(p); pick({ name: 'entry_reason', value: reason, label: `Why it ran: ${reasonOf(reason).label}` }); }} />
            </div>

            {shownFocus && (
              <div key={shownFocus} className="orbit-swap space-y-6">
                <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-amber-300">
                  {phaseName(shownFocus)}, in detail
                </div>
                <div className="grid min-w-0 grid-cols-1 gap-6 lg:grid-cols-2">
                  <SplitBy scope={scope} phase={shownFocus} bump={bump} onPick={pick} />
                  <Versions scope={scope} phase={shownFocus} bump={bump} onPick={pick} />
                </div>
                <Tools scope={scope} phase={shownFocus} bump={bump} />
                <DiffAndRebase phase={shownFocus} diff={data.diffSize} rebase={data.startRebase} onPick={pick} />
              </div>
            )}

            <div className="grid min-w-0 grid-cols-1 gap-6 lg:grid-cols-2">
              <FirstPassQa rows={data.firstPass} />
              <Daily rows={data.daily} phase={shownFocus} />
            </div>

            <Bugs scope={scope} bump={bump} onPick={pick} />
            <ExperimentResults project={scope.project} bump={bump} onPick={pick} />
            {/* Where an experiment is started, stopped and decided; the results above only read. */}
            <Experiments project={scope.project} />

            <div id="metrics-runs" className="scroll-mt-4">
              <RunList scope={scope} phase={shownFocus} filter={filter} onClearFilter={() => setFilter(null)} bump={bump} />
            </div>
          </>
        )}
      </div>
    </div>
  );
}
