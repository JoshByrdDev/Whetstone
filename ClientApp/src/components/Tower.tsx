import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useTickets } from '../hooks/useTickets';
import { useNarrow } from '../hooks/useNarrow';
import { useWorkerHosts } from '../hooks/useWorkerHosts';
import type { Track } from '../types/tickets';
import type { DashboardState, HeartbeatEvent } from '../types/events';
import { isStewardEvent } from '../types/events';
import { useArcFeed, useLanding } from './Board';
import { SectorArc } from './SectorArc';
import { STATIONS, stationOf } from './JourneyTrack';
import { AgentDock } from './AgentMascot';
import { ActivityFeed } from './ActivityFeed';
import { ClaudeLogins } from './ClaudeUsage';
import { BusyWhileOnDuty } from './CapacityStrip';
import { SmartSwitchCard } from './SmartSwitchField';
import { StewardCard, WaitingOnYou } from './StewardPanel';
import { Lanes, SliceStrip, unitsOf } from './StewardPlan';
import { CoverageTable } from './recon/Coverage';
import { useJson } from './metrics/parts';
import { useProjectList, rememberProject } from './ProjectPicker';
import type { ProjectOpt } from './ProjectPicker';
import { remember } from './stewardParts';
import { Chip, SectionRule } from './ui';
import { BODY, DISPLAY, TIER, chamfer } from './orbit';
import { track } from '../theme';
import { getSteward, getStewardPlan, getStewardStatus } from '../api/steward';
import type { StewardPage, StewardPlanView, StewardStatus } from '../api/steward';
import type { CoverageCell, ReconProjects } from './recon/types';

/**
 * The Tower: the whole system on one screen, to leave up on a monitor and watch.
 *
 * Every other tab does its own thing well and is where that thing is worked. This one borrows the
 * part of each that says how things stand - the board's arc and berths, the steward's Now and its
 * slices, Recon's coverage, the heartbeat's feed, the Claude headroom and the fleet's busy share -
 * and draws them all at once, sized to the window so nothing needs scrolling to. Each part is the
 * same component its own tab draws, so the two never disagree.
 *
 * It is for watching. The only things here that write are the two whose whole point is to be acted
 * on while you watch: the questions waiting on you, and the smart switch. The arc lights on hover
 * and says what is parked where, and filters nothing.
 *
 * The steward's questions are every project's, each tagged with its project, because a question
 * missed is the one thing on this page that costs anything. The steward card, the slices and the
 * coverage are per project, and follow the one picker at the top.
 */

const POLL_MS = 15_000;

export function Tower({ state, wakeAt, events, onGo }: {
  state: DashboardState;
  wakeAt: Date | null;
  events: HeartbeatEvent[];
  /** Open the Steward tab, which the Tower has already pointed at the slice that was picked. */
  onGo: (tab: 'steward') => void;
}) {
  const narrow = useNarrow();
  const [projects, project, setProject] = useProjectList('tower');
  const pages = useStewardPages(projects);
  const page = project ? pages.get(project) : undefined;

  const openSlice = (unit: number) => {
    if (!project) return;
    rememberProject('steward', project);
    remember('view', 'plan');
    remember('pick', String(unit));
    onGo('steward');
  };

  const fill = useFillHeight(!narrow);
  const hosts = useWorkerHosts();

  const picker = projects.length > 1 && (
    <div className="flex flex-wrap gap-1.5">
      {projects.map(p => <Chip key={p.key} on={project === p.key} onClick={() => setProject(p.key)}>{p.displayName}</Chip>)}
    </div>
  );

  const steward = (
    <StewardNow page={page?.page} state={state} events={events} reload={page?.reload} />
  );
  const waiting = <WaitingAll projects={projects} pages={pages} />;
  const agents = (
    <AgentDock status={state.status} wakeAt={wakeAt} workers={state.workers} agents={state.agents}
      visible={NONE} events={events} hosts={hosts} stacked />
  );
  const slices = project ? <Slices project={project} narrow={narrow} onPick={openSlice} /> : null;
  const coverage = project ? <ProjectCoverage project={project} /> : null;
  const feed = <ActivityFeed events={events} fill />;

  if (narrow) {
    // A phone: one column, what needs you first. No arc - the board draws none on a phone either.
    return (
      <div className="px-3 py-3 flex flex-col gap-4">
        <Head picker={picker} />
        <Cell title="Waiting on you" colour={TIER.blocked.c}>{waiting}</Cell>
        <Cell title="Agents">{agents}</Cell>
        <Cell title="Claude headroom"><ClaudeLogins /></Cell>
        <Cell title="Capacity"><BusyWhileOnDuty /></Cell>
        <Cell title="Activity"><div style={{ height: 360 }}>{feed}</div></Cell>
        <Cell title="Steward">{steward}</Cell>
        <Cell title="Slices">{slices}</Cell>
        <Cell title="Coverage">{coverage}</Cell>
        <Cell title="Smart switch"><SmartSwitchCard /></Cell>
      </div>
    );
  }

  return (
    <div ref={fill.ref} className="px-4 pt-2 pb-3 flex flex-col gap-3 min-h-0" style={{ height: fill.height }}>
      <Head picker={picker} />
      <Arc state={state} events={events} />

      <div className="grid flex-1 min-h-0 gap-3" style={{
        gridTemplateColumns: 'repeat(12, minmax(0, 1fr))',
        gridTemplateRows: 'minmax(0, 1.35fr) minmax(0, 1fr) minmax(0, 1fr) minmax(0, 1.1fr)',
      }}>
        <Cell title="Steward - now" colour={TIER.blocked.c} span={7}>
          <div className="grid gap-3 h-full min-h-0" style={{ gridTemplateColumns: 'minmax(0, 2fr) minmax(0, 3fr)' }}>
            <div className="min-h-0 overflow-y-auto dark-scrollbar">{steward}</div>
            <div className="min-h-0 overflow-y-auto dark-scrollbar pr-1">{waiting}</div>
          </div>
        </Cell>
        <Cell title="Agents by machine" span={5} scroll>{agents}</Cell>

        <Cell title="Slices" colour={TIER.gate.c} span={12} scroll>{slices}</Cell>

        <Cell title="Claude headroom" colour={TIER.active.c} span={5} scroll><ClaudeLogins /></Cell>
        <Cell title="Capacity" span={2}><BusyWhileOnDuty /></Cell>
        <Cell title="Smart switch" span={5} scroll><SmartSwitchCard /></Cell>

        <Cell title="Coverage" colour={TIER.queue.c} span={7} scroll>{coverage}</Cell>
        <Cell title="Activity" colour={TIER.queue.c} span={5}>{feed}</Cell>
      </div>
    </div>
  );
}

const NONE: Set<number> = new Set();

function Head({ picker }: { picker: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div style={{ font: `700 15px ${DISPLAY}`, letterSpacing: track(0.3), color: 'var(--w-t200)', textTransform: 'uppercase' }}>Tower</div>
      {picker}
    </div>
  );
}

/**
 * One part of the Tower: a rule naming it, and a body that keeps to the height its row was given.
 * `scroll` lets the body scroll inside itself when what it has to say is taller than the row; a part
 * that manages its own height (the feed, the steward's two columns) leaves it off.
 */
function Cell({ title, colour, span, scroll = false, children }: {
  title: string; colour?: string; span?: number; scroll?: boolean; children: ReactNode;
}) {
  return (
    <section className="min-w-0 min-h-0 flex flex-col border border-hair bg-panel"
      style={{ gridColumn: span ? `span ${span} / span ${span}` : undefined, clipPath: chamfer(10), padding: '8px 12px 10px' }}>
      <SectionRule title={title} colour={colour} />
      <div className={`min-h-0 flex-1 min-w-0 ${scroll ? 'overflow-y-auto dark-scrollbar pr-1' : ''}`}>{children}</div>
    </section>
  );
}

/** The window's height below the page's own top, so the Tower is exactly one screen tall at a desk. */
function useFillHeight(on: boolean) {
  const ref = useRef<HTMLDivElement>(null);
  const [height, setHeight] = useState<number | undefined>(undefined);
  useLayoutEffect(() => {
    if (!on) { setHeight(undefined); return; }
    const measure = () => {
      const top = ref.current?.getBoundingClientRect().top ?? 0;
      // A floor, so a short window scrolls the page rather than crushing every part to a sliver.
      setHeight(Math.max(640, Math.floor(window.innerHeight - top - window.scrollY)));
    };
    measure();
    // A banner arriving above the page moves its top without the window changing size.
    const ro = new ResizeObserver(measure);
    ro.observe(document.body);
    window.addEventListener('resize', measure);
    return () => { ro.disconnect(); window.removeEventListener('resize', measure); };
  }, [on]);
  return { ref, height };
}

/** The board's arc over every project: hover says what is parked at a stop, and nothing filters. */
function Arc({ state, events }: { state: DashboardState; events: HeartbeatEvent[] }) {
  const { tickets, phases, projects, refreshTickets } = useTickets(null);
  // As on the board: the heartbeat moves tickets under us, so re-read on every change it reports
  // and on a tick while it runs.
  const beat = `${state.status}|${state.agents.map(a => `${a.slot}:${a.ticketId ?? ''}:${a.phase ?? ''}`).join(',')}`;
  useEffect(() => { refreshTickets(); }, [beat, refreshTickets]);
  useEffect(() => {
    if (state.status !== 'running') return;
    const id = setInterval(refreshTickets, 8000);
    return () => clearInterval(id);
  }, [state.status, refreshTickets]);

  const kindOf = useMemo(() => new Map(phases.map(p => [p.name, p.kind])), [phases]);
  const { load, pulses } = useArcFeed(state, events, tickets, kindOf);
  const { touchdown } = useLanding(tickets, kindOf, phases.length > 0);
  const tracks = useMemo<Track[]>(() => {
    const all = new Set<Track>();
    for (const p of projects) for (const t of p.tracks ?? []) all.add(t);
    return all.size ? [...all] : ['Delivery'];
  }, [projects]);

  // What a stop says under the pointer: how many, and which. Done is every ticket ever finished,
  // so it is the count alone.
  const describe = useCallback((i: number) => {
    const here = tickets.filter(t => stationOf(t) === i);
    const head = `${STATIONS[i].label} — ${here.length} ticket${here.length === 1 ? '' : 's'}`;
    if (i === STATIONS.length - 1 || here.length === 0) return head;
    const shown = here.slice(0, 8).map(t => `• ${t.projectKey}-${t.id} ${t.title}`);
    return [head, ...shown, ...(here.length > 8 ? [`and ${here.length - 8} more`] : [])].join('\n');
  }, [tickets]);

  return (
    <div className="flex-none border border-hair bg-panel overflow-hidden" style={{ clipPath: chamfer(10), padding: '0 0 6px' }}>
      {/* The arc is laid out on the board's row grid, whose first and last columns hold a row's
          title and actions. The Tower has no rows, so those two columns are pulled off the edges and
          the stops spread across the whole width. */}
      <div style={{ margin: '-12px -110px 0 -250px' }}>
      <SectorArc squash={0.55}
        load={load} tracks={tracks} status={state.status} pulses={pulses}
        burst={touchdown ? { station: STATIONS.length - 1, key: touchdown } : null}
        insetLeft={20} insetRight={16} describe={describe}
      />
      </div>
    </div>
  );
}

interface ProjectSteward { page: StewardPage | null; status: StewardStatus | null; reload: () => Promise<void> }

/** Every project's steward page and status, re-read on a slow tick: a question waits for a person either way. */
function useStewardPages(projects: ProjectOpt[]): Map<string, ProjectSteward> {
  const [read, setRead] = useState<Map<string, { page: StewardPage | null; status: StewardStatus | null }>>(() => new Map());
  const keys = projects.map(p => p.key).join(',');

  const loadOne = useCallback(async (key: string) => {
    try {
      const [page, status] = await Promise.all([getSteward(key), getStewardStatus(key).catch(() => null)]);
      setRead(m => new Map(m).set(key, { page, status }));
    } catch { /* this project keeps what it last showed; the next tick is the retry */ }
  }, []);

  useEffect(() => {
    if (!keys) return;
    const all = () => { for (const k of keys.split(',')) void loadOne(k); };
    all();
    const t = setInterval(all, POLL_MS);
    return () => clearInterval(t);
  }, [keys, loadOne]);

  return useMemo(() => {
    const m = new Map<string, ProjectSteward>();
    for (const p of projects) {
      const r = read.get(p.key);
      m.set(p.key, { page: r?.page ?? null, status: r?.status ?? null, reload: () => loadOne(p.key) });
    }
    return m;
  }, [projects, read, loadOne]);
}

/** The picked project's steward, as the Steward page's Now draws it on its left. */
function StewardNow({ page, state, events, reload }: {
  page: StewardPage | null | undefined;
  state: DashboardState; events: HeartbeatEvent[]; reload?: () => Promise<void>;
}) {
  const live = useMemo(() => events.filter(e => isStewardEvent(e) && e.project === page?.project), [events, page?.project]);
  if (!page) return <Quiet>Reading the steward.</Quiet>;
  if (page.mode === 'off') return <Quiet>No steward for this project.</Quiet>;
  return (
    <StewardCard page={page} running={state.steward?.project === page.project || page.running}
      fleet={state.status} live={live} onRan={() => { void reload?.(); }} />
  );
}

/** Every project's questions in one list, each said with its project when there is more than one. */
function WaitingAll({ projects, pages }: { projects: ProjectOpt[]; pages: Map<string, ProjectSteward> }) {
  const [counts, setCounts] = useState<Record<string, { n: number; checking: boolean }>>({});
  const heard = useCallback((key: string) => (n: number, checking: boolean) =>
    setCounts(c => (c[key]?.n === n && c[key]?.checking === checking ? c : { ...c, [key]: { n, checking } })), []);
  const hearers = useMemo(() => new Map(projects.map(p => [p.key, heard(p.key)])), [projects, heard]);

  const on = projects.filter(p => { const pg = pages.get(p.key)?.page; return pg && pg.mode !== 'off'; });
  const total = on.reduce((s, p) => s + (counts[p.key]?.n ?? 0), 0);
  const checking = on.some(p => counts[p.key]?.checking ?? true);
  const many = on.length > 1;

  return (
    <div className="flex flex-col gap-3">
      {on.map(p => {
        const s = pages.get(p.key)!;
        return (
          <WaitingOnYou key={p.key} page={s.page!} status={s.status} onChanged={s.reload}
            onCount={hearers.get(p.key)} tag={many ? p.displayName : undefined} quietWhenEmpty />
        );
      })}
      {total === 0 && (
        <Quiet>{on.length === 0 ? 'No project has a steward switched on.'
          : checking ? 'Checking finished tickets for flags only you can clear.'
          : 'Nothing is waiting on you.'}</Quiet>
      )}
    </div>
  );
}

/** The picked project's slices in their lanes. Picking one opens it on the Steward tab, where it is worked. */
function Slices({ project, narrow, onPick }: { project: string; narrow: boolean; onPick: (unit: number) => void }) {
  const [view, setView] = useState<StewardPlanView | null>(null);
  useEffect(() => {
    let live = true;
    setView(null);
    const load = () => getStewardPlan(project)
      .then(v => { if (live) setView(v); })
      .catch(() => { if (live) setView(v => v ?? { why: 'The slices could not be read just now.' } as StewardPlanView); });
    void load();
    const t = setInterval(load, 30_000);
    return () => { live = false; clearInterval(t); };
  }, [project]);

  if (!view) return <Quiet>Reading the slices.</Quiet>;
  const units = unitsOf(view);
  if (units.length === 0) return <Quiet>{view.why ?? 'There are no slices yet.'}</Quiet>;
  return narrow
    ? <SliceStrip units={units} selected={null} onPick={onPick} />
    : <Lanes units={units} selected={null} onPick={onPick} className="" />;
}

/** The picked project's coverage, read-only, re-read every minute. */
function ProjectCoverage({ project }: { project: string }) {
  const [bump, setBump] = useState(0);
  useEffect(() => { const t = setInterval(() => setBump(b => b + 1), 60_000); return () => clearInterval(t); }, []);
  const recon = useJson<ReconProjects>('/api/recon/projects', bump);
  const p = recon.data?.projects.find(x => x.key === project);
  const showable = !!p && p.built && p.areas.length > 0 && !!recon.data?.status.hasMarts;
  const cov = useJson<{ cells: CoverageCell[] }>(showable ? `/api/recon/${encodeURIComponent(project)}/coverage` : null, bump);

  if (!recon.data) return <Quiet>{recon.error ? `Could not read Recon: ${recon.error}` : 'Reading Recon.'}</Quiet>;
  if (!p) return <Quiet>This project has no discovery block, so Recon has nothing to cover.</Quiet>;
  if (!showable) return <Quiet>Recon has not built this project's coverage yet. The Recon tab builds it.</Quiet>;
  if (cov.error) return <Quiet>Could not read the coverage: {cov.error}</Quiet>;
  if (!cov.data) return <Quiet>Reading the coverage.</Quiet>;
  return <CoverageTable cells={cov.data.cells} />;
}

function Quiet({ children }: { children: ReactNode }) {
  return <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t500)' }}>{children}</div>;
}
