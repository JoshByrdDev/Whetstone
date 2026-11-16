import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { useTickets } from '../hooks/useTickets';
import { useNarrow } from '../hooks/useNarrow';
import { useWorkerHosts } from '../hooks/useWorkerHosts';
import type { Track } from '../types/tickets';
import type { DashboardState, HeartbeatEvent } from '../types/events';
import { useArcFeed, useLanding } from './Board';
import { SectorArc } from './SectorArc';
import { STATIONS, stationOf } from './JourneyTrack';
import { ActivityFeed } from './ActivityFeed';
import { WaitingOnYou } from './StewardPanel';
import { unitsOf } from './StewardPlan';
import { useJson } from './metrics/parts';
import { useProjectList, rememberProject } from './ProjectPicker';
import type { ProjectOpt } from './ProjectPicker';
import { remember } from './stewardParts';
import { Chip, SectionRule } from './ui';
import { BODY, TIER, chamfer } from './orbit';
import { getSteward, getStewardPlan, getStewardStatus } from '../api/steward';
import type { StewardPage, StewardPlanView, StewardStatus } from '../api/steward';
import type { CoverageCell, ReconProjects } from './recon/types';
import {
  AgentTiles, ClaudeRows, CoverageHeat, SliceGlance, StewardStrip, SwitchLine, Veil, WaitingRows, useBusyShare, useWaitingItems,
} from './tower/Glances';
import type { WaitingItem } from './tower/Glances';

/**
 * The Tower: the whole system on one screen, to leave up on a monitor and watch.
 *
 * Every other tab does its own thing well and is where that thing is worked. This one says how each
 * of them stands - the board's arc and berths, the steward and its slices, Recon's coverage, the
 * heartbeat's feed, the Claude headroom and the fleet's busy share - and draws them all at once,
 * sized to the window. Nothing on it scrolls: each part is a glance (tower/Glances.tsx) that says
 * its tab's state in as few lines as still say it, read from the same endpoints and helpers.
 *
 * It is for watching. The two things that write - a question waiting on you, and the smart switch -
 * open their full components over the page, the same ones the Steward and Settings tabs draw, so
 * there is one way to answer and one way to switch. The arc lights on hover and says what is
 * parked where, and filters nothing.
 *
 * The steward's questions are every project's, each tagged with its project, because a question
 * missed is the one thing on this page that costs anything. The steward strip, the slices and the
 * coverage are per project, and follow the picker in the arc's corner.
 */

const POLL_MS = 15_000;

export function Tower({ state, events, onGo }: {
  state: DashboardState;
  wakeAt?: Date | null;
  events: HeartbeatEvent[];
  /** Open the Steward tab, which the Tower has already pointed at the slice that was picked. */
  onGo: (tab: 'steward') => void;
}) {
  const narrow = useNarrow();
  const [projects, project, setProject] = useProjectList('tower');
  const pages = useStewardPages(projects);
  const page = project ? pages.get(project) : undefined;
  const hosts = useWorkerHosts();
  const busy = useBusyShare();
  const fill = useFillHeight(!narrow);
  const { items, checking } = useWaitingItems(projects, pages);
  const [open, setOpen] = useState<WaitingItem | null>(null);
  const many = projects.filter(p => { const pg = pages.get(p.key)?.page; return pg && pg.mode !== 'off'; }).length > 1;

  const openSlice = (unit: number) => {
    if (!project) return;
    rememberProject('steward', project);
    remember('view', 'plan');
    remember('pick', String(unit));
    onGo('steward');
  };

  const picker = projects.length > 1 && (
    <div className="flex flex-wrap gap-1.5">
      {projects.map(p => <Chip key={p.key} on={project === p.key} onClick={() => setProject(p.key)}>{p.displayName}</Chip>)}
    </div>
  );
  const name = projects.find(p => p.key === project)?.displayName;

  const answering = open && (() => {
    const s = pages.get(open.project);
    if (!s?.page) return null;
    return (
      <Veil title={`${open.projectName} - waiting on you`} onClose={() => setOpen(null)}>
        <WaitingOnYou page={s.page} status={s.status} only={open.key} quietWhenEmpty
          onChanged={async () => { await s.reload(); if (!open.key.startsWith('t:')) setOpen(null); }} />
      </Veil>
    );
  })();

  const steward = <StewardStrip page={page?.page} state={state} />;
  const waiting = <WaitingRows items={items} checking={checking} many={many} max={narrow ? 8 : 5} onOpen={setOpen} />;
  const agents = <AgentTiles state={state} hosts={hosts} />;
  const slices = project ? <Slices project={project} onPick={openSlice} /> : null;
  const coverage = project ? <ProjectCoverage project={project} /> : null;
  const claude = <ClaudeRows />;
  const feed = <ActivityFeed events={events} fill clip />;

  if (narrow) {
    // A phone: one column, what needs you first. No arc - the board draws none on a phone either.
    return (
      <div className="px-3 py-3 flex flex-col gap-3">
        {picker}
        <Cell title="Waiting on you" colour={TIER.blocked.c} count={items.length}>{waiting}</Cell>
        <Cell title="Steward" meta={name}>{steward}</Cell>
        <Cell title="Agents">{agents}</Cell>
        <Cell title="Claude" meta={busy?.text}>{claude}</Cell>
        <Cell title="Activity"><div style={{ height: 300 }}>{feed}</div></Cell>
        <Cell title="Slices" meta={name}><div className="overflow-x-auto"><div style={{ minWidth: 640 }}>{slices}</div></div></Cell>
        <Cell title="Coverage" meta={name}><div className="overflow-x-auto"><div style={{ minWidth: 520 }}>{coverage}</div></div></Cell>
        <Cell title="Smart switch"><SwitchLine /></Cell>
        {answering}
      </div>
    );
  }

  return (
    <div ref={fill.ref} className="px-4 pt-2 pb-3 flex flex-col gap-2.5 min-h-0" style={{ height: fill.height }}>
      <div className="relative flex-none">
        <Arc state={state} events={events} />
        {picker && <div className="absolute" style={{ top: 8, right: 10 }}>{picker}</div>}
      </div>

      <div className="grid flex-1 min-h-0 gap-2.5" style={{ gridTemplateColumns: 'minmax(0, 4fr) minmax(0, 5.2fr) minmax(0, 3.6fr)' }}>
        <div className="flex flex-col gap-2.5 min-h-0">
          <Cell title="Steward" meta={name}>{steward}</Cell>
          <Cell title="Waiting on you" colour={TIER.blocked.c} count={items.length} grow>{waiting}</Cell>
        </div>
        <div className="flex flex-col gap-2.5 min-h-0">
          <Cell title="Agents">{agents}</Cell>
          <Cell title="Slices" colour={TIER.gate.c} meta={name}>{slices}</Cell>
          <Cell title="Coverage" colour={TIER.queue.c} meta={name} grow>{coverage}</Cell>
        </div>
        <div className="flex flex-col gap-2.5 min-h-0">
          <Cell title="Claude" colour={TIER.active.c} meta={busy?.text} metaTitle={busy?.title}>{claude}</Cell>
          <Cell title="Smart switch">{<SwitchLine />}</Cell>
          <Cell title="Activity" colour={TIER.queue.c} grow>{feed}</Cell>
        </div>
      </div>
      {answering}
    </div>
  );
}

/**
 * One part of the Tower: a rule naming it, and its glance under it. Nothing in it scrolls; `grow`
 * gives a part the rest of its column, and what does not fit there is clipped, never scrolled.
 */
function Cell({ title, colour, count, meta, metaTitle, grow = false, children }: {
  title: string; colour?: string; count?: number; meta?: string; metaTitle?: string; grow?: boolean; children: ReactNode;
}) {
  return (
    <section className={`min-w-0 flex flex-col border border-hair bg-panel overflow-hidden ${grow ? 'flex-1 min-h-0' : 'flex-none'}`}
      style={{ clipPath: chamfer(10), padding: '6px 12px 10px' }}>
      <div title={metaTitle}>
        <SectionRule title={title} colour={colour} count={count || undefined} meta={meta} />
      </div>
      <div className={`min-w-0 ${grow ? 'flex-1 min-h-0 overflow-hidden' : ''}`}>{children}</div>
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
      setHeight(Math.max(600, Math.floor(window.innerHeight - top - window.scrollY)));
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
    <div className="border border-hair bg-panel overflow-hidden" style={{ clipPath: chamfer(10), padding: '0 0 4px' }}>
      {/* The arc is laid out on the board's row grid, whose first and last columns hold a row's
          title and actions. The Tower has no rows, so those two columns are pulled off the edges and
          the stops spread across the whole width. */}
      <div style={{ margin: '-16px -110px 0 -250px' }}>
        <SectorArc squash={0.36}
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

/** The picked project's slices. Picking one opens it on the Steward tab, where it is worked. */
function Slices({ project, onPick }: { project: string; onPick: (unit: number) => void }) {
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
  return <SliceGlance units={units} onPick={onPick} />;
}

/** The picked project's coverage as a heatmap, re-read every minute. */
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
  return <CoverageHeat cells={cov.data.cells} />;
}

function Quiet({ children }: { children: ReactNode }) {
  return <div style={{ font: `400 11.5px/1.5 ${BODY}`, color: 'var(--w-t500)' }}>{children}</div>;
}
