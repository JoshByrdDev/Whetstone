import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../../hooks/useVeil';
import type { DashboardState } from '../../types/events';
import type { Phase } from '../../types/tickets';
import type { WorkerHost } from '../../hooks/useWorkerHosts';
import { AgentMascot, agentAccent } from '../AgentMascot';
import type { Mood } from '../../theme';
import { reconState } from '../../theme';
import { STATIONS, STATION_OF } from '../JourneyTrack';
import { BODY, MONO, TIER, chamfer } from '../orbit';
import { ago, label } from '../stewardParts';
import { LANES, shortTitle } from '../StewardPlan';
import { colKey, shape } from '../recon/Coverage';
import { useAllClaudeUsage, tone, formatReset } from '../ClaudeUsage';
import type { UsageLimit } from '../ClaudeUsage';
import { useCapacity, onDutyOf, pct, hours } from '../CapacityStrip';
import { SmartSwitchCard } from '../SmartSwitchField';
import type { SmartSwitchSetting } from '../SmartSwitchField';
import { getStewardOwnerFlags } from '../../api/steward';
import type { StewardOwnerFlag, StewardPage, StewardPlanUnitView, StewardStatus } from '../../api/steward';
import type { CoverageCell } from '../recon/types';

/**
 * The Tower's glances: each tab's state, said in the fewest lines that still say it, so the whole
 * system fits one screen. They read the same endpoints and the same helpers as the full pages, and
 * where something is to be done - a question to answer, the switch to change - they open the full
 * component over the page rather than growing a second way to do it.
 */

const small = { font: `400 11px/1.45 ${BODY}`, color: 'var(--w-t500)' } as const;

/** A row that is one line and stays one line. */
function Line({ children, className = '', style }: { children: ReactNode; className?: string; style?: React.CSSProperties }) {
  return <div className={`truncate min-w-0 ${className}`} style={style}>{children}</div>;
}

// ---- Steward ----------------------------------------------------------------------------------

/** The steward in two lines: what it is doing and since when, and what its last run did. */
export function StewardStrip({ page, state }: { page: StewardPage | null | undefined; state: DashboardState }) {
  if (!page) return <div style={small}>Reading the steward.</div>;
  if (page.mode === 'off') return <div style={small}>No steward for this project.</div>;
  const running = state.steward?.project === page.project || page.running;
  const owed = page.due || (!running && !!page.heldUntil);
  const held = owed && !running && (state.status === 'stopped' || state.status === 'off-hours');
  const mood: Mood = running ? 'running' : held ? 'stopped' : owed ? 'idle' : 'sleeping';
  const word = running ? 'Working' : owed ? 'A run is due' : page.resting ? 'Resting' : 'Standing by';
  const when = running
    ? page.lastRunStartedAt ? `started ${ago(page.lastRunStartedAt)}` : ''
    : page.lastRunEndedAt ? `last run ${ago(page.lastRunEndedAt)}` : '';
  return (
    <div className="flex items-start gap-3 min-w-0">
      <div className="flex-none" style={{ marginTop: 2 }}><AgentMascot mood={mood} size={30} /></div>
      <div className="min-w-0 flex-1">
        <Line style={label(running ? TIER.active.c : 'var(--w-t300)')}>{word}{when ? ` - ${when}` : ''} · {page.mode}</Line>
        <div className="line-clamp-2" style={{ font: `400 11.5px/1.45 ${BODY}`, color: 'var(--w-t400)', marginTop: 2 }}
          title={page.lastRunSummary ?? undefined}>
          {page.lastRunSummary ?? 'No run has finished yet.'}
        </div>
      </div>
    </div>
  );
}

/** One thing waiting on the owner, as a row: a question, or a ticket whose merge a flag holds. */
export interface WaitingItem {
  project: string;
  projectName: string;
  /** The question's key, or `t:<id>` for a ticket's flags - what WaitingOnYou's `only` takes. */
  key: string;
  title: string;
  sub: string;
  at?: string | null;
}

/**
 * Every project's waiting items. Questions come with the page; a ticket in review is waiting only
 * while a flag on it is uncleared, which takes a git read per ticket, so those are asked on a slower tick.
 */
export function useWaitingItems(projects: { key: string; displayName: string }[],
  pages: Map<string, { page: StewardPage | null; status: StewardStatus | null }>): { items: WaitingItem[]; checking: boolean } {
  const reviews = useMemo(() => projects.flatMap(p => {
    const s = pages.get(p.key);
    if (!s?.page || s.page.mode === 'off') return [];
    const asked = new Set(s.page.waiting.map(q => q.ticketId).filter(Boolean));
    return (s.status?.tickets ?? []).filter(t => t.phase === 'NeedsReview' && !asked.has(t.id))
      .map(t => ({ project: p.key, projectName: p.displayName, id: t.id, title: t.title }));
  }), [projects, pages]);
  const reviewKey = reviews.map(r => `${r.project}:${r.id}`).join(',');

  const [flags, setFlags] = useState<Map<string, StewardOwnerFlag[]>>(() => new Map());
  const [heard, setHeard] = useState(false);
  useEffect(() => {
    let live = true;
    const read = async () => {
      const got = await Promise.all(reviews.map(r =>
        getStewardOwnerFlags(r.project, r.id).then(x => [`${r.project}:${r.id}`, x.flags] as [string, StewardOwnerFlag[]]).catch(() => null)));
      if (!live) return;
      setFlags(new Map(got.filter((x): x is [string, StewardOwnerFlag[]] => !!x)));
      setHeard(true);
    };
    void read();
    const t = setInterval(read, 60_000);
    return () => { live = false; clearInterval(t); };
    // The list is keyed by what is in review, not by the array's identity.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reviewKey]);

  const items: WaitingItem[] = [];
  for (const p of projects) {
    const s = pages.get(p.key);
    if (!s?.page || s.page.mode === 'off') continue;
    for (const q of s.page.waiting) {
      items.push({ project: p.key, projectName: p.displayName, key: q.key, title: q.question, sub: `held up: ${q.heldUp}`, at: q.askedAt });
    }
  }
  for (const r of reviews) {
    const open = (flags.get(`${r.project}:${r.id}`) ?? []).filter(f => !f.cleared);
    if (open.length === 0) continue;
    items.push({
      project: r.project, projectName: r.projectName, key: `t:${r.id}`,
      title: `#${r.id} ${r.title}`, sub: `merge held: ${open.map(f => f.say ?? f.flag).join('; ')}`,
    });
  }
  items.sort((a, b) => (b.at ?? '').localeCompare(a.at ?? ''));
  return { items, checking: reviews.length > 0 && !heard };
}

/** The waiting items as rows, newest first; Answer opens the one item's full card. */
export function WaitingRows({ items, checking, many, max, onOpen }: {
  items: WaitingItem[]; checking: boolean; many: boolean; max: number;
  onOpen: (item: WaitingItem) => void;
}) {
  if (items.length === 0) {
    return <div style={small}>{checking ? 'Checking finished tickets for flags only you can clear.' : 'Nothing is waiting on you.'}</div>;
  }
  const shown = items.slice(0, max);
  return (
    <div className="flex flex-col">
      {shown.map((it, i) => (
        <div key={`${it.project}:${it.key}`} className="min-w-0 flex items-start gap-3"
          style={{ padding: '6px 0', borderTop: i ? '1px solid rgb(var(--w-line-rgb) / .1)' : undefined }}>
          <div className="min-w-0 flex-1">
            <div className="line-clamp-2" style={{ font: `600 12.5px/1.4 ${BODY}`, color: 'var(--w-t100)' }} title={it.title}>
              {many && <span style={{ ...label('var(--w-t400)'), marginRight: 6 }}>{it.projectName}</span>}
              {it.title}
            </div>
            <Line style={small}>{it.at ? `asked ${ago(it.at)} · ` : ''}{it.sub}</Line>
          </div>
          <button type="button" onClick={() => onOpen(it)} className="orbit-tap flex-none"
            style={{ ...label(TIER.blocked.c), padding: '4px 10px', marginTop: 2, clipPath: chamfer(6), border: `1px solid ${TIER.blocked.c}` }}>
            {it.key.startsWith('t:') ? 'Clear' : 'Answer'}
          </button>
        </div>
      ))}
      {items.length > max && <div style={{ ...small, paddingTop: 4 }}>and {items.length - max} more on the Steward tab</div>}
    </div>
  );
}

// ---- Overlay ----------------------------------------------------------------------------------

/** The full component over the page: what the Tower opens when something is to be done. */
export function Veil({ title, onClose, children }: { title: string; onClose: () => void; children: ReactNode }) {
  const { fx, dismiss } = useVeil(onClose);
  useEffect(() => {
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape') dismiss(); };
    document.addEventListener('keydown', esc);
    return () => document.removeEventListener('keydown', esc);
  }, [dismiss]);
  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/70" onClick={dismiss} />
      <div className="orbit-modal relative w-full max-w-3xl max-h-[88vh] flex flex-col overflow-hidden border border-hair bg-panel"
        style={{ clipPath: chamfer(12) }}>
        <div className="flex items-center justify-between gap-3 px-5 py-3 border-b border-hair">
          <div style={label('var(--w-t200)')}>{title}</div>
          <button type="button" className="orbit-tap" onClick={dismiss} style={label('var(--w-t400)')} aria-label="Close">Close</button>
        </div>
        <div className="overflow-y-auto dark-scrollbar p-5">{children}</div>
      </div>
    </div>,
    document.body,
  );
}

// ---- Agents -----------------------------------------------------------------------------------

/** What a phase is called on the arc, so a tile and the arc above it use the same word. */
const stopWord = (phase?: string) => (phase && phase in STATION_OF ? STATIONS[STATION_OF[phase as Phase]].short : phase ?? '');

/** Every berth as a tile, a line per machine: its slot, and the ticket and stop of what it is working. */
export function AgentTiles({ state, hosts }: { state: DashboardState; hosts: WorkerHost[] }) {
  const bySlot = new Map(state.agents.map(a => [a.slot, a]));
  const remote = hosts.filter(h => h.enabled);
  const working = state.status === 'running' ? state.agents.length : 0;
  const total = Math.max(1, state.workers) + remote.reduce((s, h) => s + Math.max(0, h.slots), 0);

  const tile = (slot: number) => {
    const a = state.status === 'running' ? bySlot.get(slot) : undefined;
    const ticket = a?.ticketId ? `#${a.ticketId.replace(/\D/g, '')}` : '';
    return (
      <span key={slot} className={`inline-flex items-center gap-1.5 border ${a ? agentAccent(slot).chip : 'border-hair'}`}
        title={a ? `Agent ${slot} - ${a.project ?? ''} ${ticket} ${a.phase ?? ''}${a.since ? `, since ${ago(a.since)}` : ''}` : `Agent ${slot} - free`}
        style={{ clipPath: chamfer(5), padding: '2px 7px', font: `500 11px ${MONO}`, color: a ? undefined : 'var(--w-t600)' }}>
        <span style={{ opacity: .7 }}>{slot}</span>
        {a ? <>{ticket}<span style={{ opacity: .75 }}>{stopWord(a.phase)}</span></> : <span>-</span>}
      </span>
    );
  };
  // One grid for every machine, so the names' column is as wide as the longest name and no wider.
  const row = (name: string, slots: number[], why?: string) => [
    <span key={`${name}:n`} className="self-center min-w-0 truncate" style={label(why ? TIER.blocked.c : 'var(--w-t400)')}
      title={why ? `${name} - ${why}` : name}>
      {name}{why && <span style={{ ...small, color: TIER.blocked.c, textTransform: 'none', letterSpacing: 0 }}> · {why}</span>}
    </span>,
    <div key={`${name}:s`} className="flex flex-wrap gap-1 min-w-0">{slots.map(tile)}</div>,
  ];
  return (
    <div className="flex flex-col gap-1.5">
      <div style={small}>{working} of {total} berths working · fleet {state.status}</div>
      <div className="grid gap-x-2.5 gap-y-1.5" style={{ gridTemplateColumns: 'minmax(0, max-content) minmax(0, 1fr)' }}>
        {row('This machine', Array.from({ length: Math.max(1, state.workers) }, (_, i) => i + 1))}
        {remote.map(h => row(h.name, Array.from({ length: Math.max(0, h.slots) }, (_, i) => h.firstSlot + i),
          !h.online ? 'offline' : h.commitMatches === false ? 'on another commit' : undefined))}
      </div>
    </div>
  );
}

// ---- Slices -----------------------------------------------------------------------------------

/** The slices' lanes, three to a lane and a count of the rest. A pick opens it on the Steward tab. */
export function SliceGlance({ units, onPick, per = 3 }: { units: StewardPlanUnitView[]; onPick: (unit: number) => void; per?: number }) {
  return (
    <div className="grid gap-3" style={{ gridTemplateColumns: `repeat(${LANES.length}, minmax(0, 1fr))` }}>
      {LANES.map(l => {
        const all = units.filter(u => l.stages.includes(u.stage));
        const here = l.stages.includes('released') ? [...all].sort((a, b) => b.unit - a.unit) : all;
        return (
          <div key={l.title} className="min-w-0">
            <div className="flex justify-between" style={label(l.c)}><span className="truncate">{l.title}</span><span style={{ color: 'var(--w-t500)' }}>{here.length}</span></div>
            {here.length === 0 && <div style={{ ...small, color: 'var(--w-t600)' }}>-</div>}
            {here.slice(0, per).map(u => (
              <button key={u.unit} type="button" onClick={() => onPick(u.unit)} title={u.title}
                className="orbit-pick block w-full text-left min-w-0" style={{ padding: '2px 0' }}>
                <Line style={{ font: `500 11.5px/1.4 ${BODY}`, color: 'var(--w-t200)' }}>
                  <span style={{ font: `700 10.5px ${MONO}`, color: 'var(--w-t500)', marginRight: 5 }}>{String(u.unit).padStart(2, '0')}</span>
                  {shortTitle(u)}
                </Line>
                {u.stage === 'building' && u.progress.outcomes > 0 && (
                  <Line style={{ ...small, fontSize: 10.5 }}>{u.progress.done} of {u.progress.outcomes} done</Line>
                )}
              </button>
            ))}
            {here.length > per && <div style={{ ...small, color: 'var(--w-t600)' }}>+{here.length - per}</div>}
          </div>
        );
      })}
    </div>
  );
}

// ---- Coverage ---------------------------------------------------------------------------------

/**
 * Coverage as a heatmap: an area to a row, a role and width to a column, each cell a square in its
 * state's colour. The words are on hover and in the legend, so no state is told by colour alone.
 */
export function CoverageHeat({ cells }: { cells: CoverageCell[] }) {
  const { columns, rows } = useMemo(() => shape(cells), [cells]);
  if (rows.length === 0) return <div style={small}>Recon has no coverage rows for this project yet.</div>;
  // Each role once, over its widths: the columns come role by role, so a role's widths are neighbours.
  const roles: { role: string; span: number }[] = [];
  for (const c of columns) {
    const last = roles[roles.length - 1];
    if (last && last.role === c.role) last.span++; else roles.push({ role: c.role, span: 1 });
  }
  const grid = { display: 'grid', gridTemplateColumns: `minmax(0, 8.5rem) repeat(${columns.length}, minmax(0, 1fr))`, columnGap: 3, rowGap: 3, alignItems: 'center' } as const;
  return (
    <div className="min-w-0">
      <div style={grid}>
        <span />
        {roles.map((r, i) => <span key={i} className="truncate text-center" title={r.role}
          style={{ gridColumn: `span ${r.span}`, font: `500 10.5px ${BODY}`, color: 'var(--w-t300)' }}>{r.role}</span>)}
        <span />
        {columns.map(c => <span key={colKey(c.role, c.width)} className="text-center" style={{ font: `400 10px ${BODY}`, color: 'var(--w-t500)' }}>{c.width}</span>)}
        {rows.map(r => (
          <Row key={r.area} name={r.area}>
            {columns.map(c => {
              const cell = r.cells.get(colKey(c.role, c.width));
              const st = reconState(cell?.state ?? 'gap');
              const lit = cell && cell.state !== 'gap';
              return (
                <span key={colKey(c.role, c.width)}
                  title={`${r.area} · ${c.role} ${c.width}: ${st.word}${cell?.lastVisitedAt ? ` · scouted ${ago(cell.lastVisitedAt)}` : ''}${cell && cell.shown > 0 ? ` · yield ${cell.approved}/${cell.shown}` : ''}${cell?.cooling ? ' · cooling' : ''}`}
                  style={{
                    height: 13, clipPath: chamfer(3),
                    background: lit ? `color-mix(in srgb, ${TIER[st.tier].c} 70%, transparent)` : 'rgb(var(--w-line-rgb) / .08)',
                    outline: cell?.cooling ? `1px dashed ${TIER.blocked.c}` : undefined, outlineOffset: -1,
                  }} />
              );
            })}
          </Row>
        ))}
      </div>
      <div className="flex flex-wrap gap-x-3 mt-2" style={small}>
        {(['fresh', 'changed', 'unknown', 'gap'] as const).map(s => {
          const st = reconState(s);
          return <span key={s} className="inline-flex items-center gap-1">
            <span style={{ width: 9, height: 9, display: 'inline-block', clipPath: chamfer(2), background: s === 'gap' ? 'rgb(var(--w-line-rgb) / .14)' : TIER[st.tier].c }} />{st.word}
          </span>;
        })}
      </div>
    </div>
  );
}

function Row({ name, children }: { name: string; children: ReactNode }) {
  return (
    <>
      <span className="truncate" title={name} style={{ font: `400 11px ${BODY}`, color: 'var(--w-t300)' }}>{name}</span>
      {children}
    </>
  );
}

// ---- Claude and capacity ----------------------------------------------------------------------

/** Every Claude login as one row: session and weekly as bars, resets on hover. */
export function ClaudeRows() {
  const logins = useAllClaudeUsage();
  if (!logins) return <div style={small}>Reading Claude usage.</div>;
  const bar = (l: UsageLimit | undefined, word: string) => {
    if (!l) return <span className="flex-1" style={{ ...small, color: 'var(--w-t600)' }}>{word} -</span>;
    const t = tone(l);
    const reset = formatReset(l.resetsAt);
    return (
      <span className="flex-1 min-w-0 flex items-center gap-1.5" title={`${l.label} ${Math.round(l.percent)}%${reset ? `, resets ${reset}` : ''}`}>
        <span className="flex-1 h-1.5 rounded-full bg-gray-800 overflow-hidden">
          <span className={`block h-full rounded-full ${t.bar}`} style={{ width: `${Math.min(100, Math.max(1, l.percent))}%` }} />
        </span>
        <span className={`text-[11px] w-8 text-right ${t.text}`}>{Math.round(l.percent)}%</span>
      </span>
    );
  };
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex gap-3" style={small}><span style={{ width: 92 }} /><span className="flex-1">session</span><span className="flex-1">weekly</span></div>
      {logins.map(l => (
        <div key={l.name} className="flex items-center gap-3 min-w-0">
          <span className="truncate flex-none" style={{ width: 92, font: `500 11.5px ${BODY}`, color: l.active ? 'var(--w-t100)' : 'var(--w-t400)' }}
            title={l.fetchedAt ? `read ${ago(l.fetchedAt)}` : 'no reading yet'}>
            {l.active && <span style={{ color: TIER.done.c }}>● </span>}{l.name}
          </span>
          {!l.signedIn
            ? <span className="flex-1" style={{ ...small, color: TIER.gate.c }}>not signed in</span>
            : <>{bar(l.limits.find(x => x.group === 'session'), 'session')}{bar(l.limits.find(x => x.kind === 'weekly_all') ?? l.limits.find(x => x.group === 'weekly'), 'weekly')}</>}
        </div>
      ))}
    </div>
  );
}

/** The fleet's busy share while on duty, over the last seven days, as a phrase for a header. */
export function useBusyShare(): { text: string; title: string } | null {
  const data = useCapacity(7);
  if (!data || data.summary.samples === 0) return null;
  const { total, onDuty } = onDutyOf(data);
  return { text: `${pct(total.busy, onDuty)} busy on duty · last 7 days`, title: `${hours(total.busy)} working of ${hours(onDuty)} slot time on duty` };
}

// ---- Smart switch -----------------------------------------------------------------------------

/** The switch in a line - on or off, and its order with each account's limits. Edit opens the whole field. */
export function SwitchLine() {
  const [value, setValue] = useState<SmartSwitchSetting | null>(null);
  const [editing, setEditing] = useState(false);
  const read = () => fetch('/api/config').then(r => r.json())
    .then((d: { claudeSmartSwitch?: SmartSwitchSetting }) => setValue(d.claudeSmartSwitch ?? { enabled: false, chain: [] }))
    .catch(() => {});
  useEffect(() => { void read(); const t = setInterval(read, 60_000); return () => clearInterval(t); }, []);

  const order = value?.chain.map(r => `${r.account} to ${r.weekly}/${r.session}%`).join(', then ') ?? '';
  return (
    <div className="flex items-start gap-3 min-w-0">
      <div className="min-w-0 flex-1">
        <div style={label(value?.enabled ? TIER.done.c : 'var(--w-t400)')}>{value ? (value.enabled ? 'On' : 'Off') : '...'}</div>
        <div className="line-clamp-2" style={{ font: `400 11.5px/1.45 ${BODY}`, color: 'var(--w-t300)' }}>
          {order || 'No accounts listed.'}
        </div>
      </div>
      <button type="button" className="orbit-tap flex-none" onClick={() => setEditing(true)}
        style={{ ...label('var(--w-accent-text)'), padding: '4px 10px', clipPath: chamfer(6), border: '1px solid rgb(var(--w-accent-rgb) / .5)' }}>
        Edit
      </button>
      {editing && (
        <Veil title="Smart switch" onClose={() => { setEditing(false); void read(); }}>
          <SmartSwitchCard compact={false} />
        </Veil>
      )}
    </div>
  );
}
