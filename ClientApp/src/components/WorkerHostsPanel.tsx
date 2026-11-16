import { useCallback, useEffect, useState } from 'react';
import { Chip, GhostButton, Panel, SectionRule, DIM, MUTED, BRIGHT } from './ui';
import { DISPLAY, MONO, BODY, CAPS, chamfer } from './orbit';
import { label, ago } from './stewardParts';
import { track } from '../theme';
import type { WorkerHost, HostDisk } from '../hooks/useWorkerHosts';
import { diskLine } from '../hooks/useWorkerHosts';
import { useMachines, hardwareLine, BOARD_PLACE, type Machine } from '../hooks/useMachines';
import { SlotSuggestionNote } from './SlotSuggestionNote';
import { ConfirmHostOffModal } from './ConfirmHostOffModal';

/**
 * The board's worker hosts: other computers that run workers for it and hand their branches back
 * (docs/worker-hosts-plan.md). One switch for all remote work, then a panel per computer - is it
 * answering, is it on this board's commit, what is it running - with what it may be given: its
 * berths, how many QA stacks at once, which phases, which projects. Above them, the order work goes
 * to the computers in, this one included, and whether it fills them in turn or spreads across them.
 *
 * Every change saves as it is made and applies on the orchestrator's next pass; nothing restarts.
 * A computer starts switched off with no projects, so nothing reaches it until its owner says so.
 *
 * Off takes a computer out of dispatch and kills nothing: its runs finish and hand back. "Stop after
 * current runs" is the same with a finish line - the computer stays On, says what it is still
 * finishing, and the board switches it Off once nothing is out (the per-computer twin of the
 * heartbeat's stop after cycle).
 *
 * This computer has the same card, first in the grid: its workers On or Off, stopped after their
 * current runs, and its berths and stacks at once (heartbeatWorkers and stackCopies, which used to live in
 * Settings and needed a restart). It is drawn even with the worker hosts hidden, because it is this
 * machine's own control. None of it touches the heartbeat itself or the steward's berth - the
 * heartbeat's own Stop After Cycle is for all of that.
 */

interface Settings {
  enabled: boolean;
  /** Whether Settings asks for this section at all. Absent from an older server, which always drew it. */
  shown?: boolean;
  boardCommit?: string | null;
  hosts: (WorkerHost & { skills: string[]; projects: string[]; stackCopies: number; remoteOn: boolean; commit?: string | null })[];
  dispatch: Dispatch;
  seen: { name: string; lastSeen: string; commit?: string | null }[];
  projects: { key: string; displayName: string }[];
  skills: string[];
  /** This computer's own place. Absent from an older server, which had no card for it. */
  board?: BoardPlace;
}

/** This computer's card, from /api/hosts/settings: its place, and what its berths are running. */
interface BoardPlace {
  enabled: boolean;
  stopPending: boolean;
  slots: number;
  stackCopies: number;
  maxSlots: number;
  maxStackCopies: number;
  /** Free space on the drives holding its projects' worktree roots. Absent from an older server. */
  disks?: HostDisk[];
  /** Set when one is under the floor (workerDiskFloorGb): the heartbeat gives this computer nothing new. */
  diskLow?: string | null;
  /** How many of its berths are running now: what a pending stop waits for. */
  out: number;
  runs: { slot: number; ticketId?: string | null; project?: string | null; phase?: string | null }[];
}

type BoardEdit = Partial<{ enabled: boolean; slots: number; stackCopies: number }>;

type HostEdit = Partial<{ enabled: boolean; slots: number; stackCopies: number; skills: string[]; projects: string[] }>;

/** Where work goes first. `@board` is this computer; the API always lists it and every host once. */
type DispatchMode = 'fill' | 'spread';
interface Dispatch { mode: DispatchMode; order: string[] }
const BOARD = '@board';

const MODE: Record<DispatchMode, { name: string; says: string }> = {
  fill: {
    name: 'Fill in order',
    says: 'Each computer is filled before the next one in the order is given anything.',
  },
  spread: {
    name: 'Spread',
    says: 'Each ticket goes to whichever computer is running the fewest; the order breaks ties.',
  },
};

const SKILL_LABEL: Record<string, string> = {
  speculate: 'Speculate', implement: 'Implement', refine: 'Refine', 'qa-test': 'QA', 'review-pr': 'Review',
};

const short = (sha?: string | null) => (sha ? sha.slice(0, 7) : '—');

async function send(url: string, method: string, body?: unknown): Promise<string | null> {
  try {
    const r = await fetch(url, {
      method,
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (r.ok) return null;
    const j = await r.json().catch(() => null);
    return j?.error ?? `Failed (${r.status})`;
  } catch {
    return 'Cannot reach the board';
  }
}

/** What a host's panel says about it, and the colour it says it in. */
function stateOf(h: Settings['hosts'][number]): { text: string; colour: string } {
  const live = h.runs.filter(r => !r.exited).length;
  if (!h.remoteOn) return { text: 'remote work off', colour: DIM };
  if (!h.enabled) return { text: 'switched off', colour: DIM };
  if (h.stopPending) return { text: live > 0 ? `stopping · ${live} finishing` : 'stopping', colour: 'rgb(var(--w-active-rgb))' };
  if (!h.online) return { text: h.lastCheckin ? 'offline' : 'never checked in', colour: 'rgb(var(--w-blocked-rgb))' };
  if (!h.commitMatches) return { text: 'on another commit', colour: 'rgb(var(--w-gate-rgb))' };
  if (h.diskLow) return { text: 'low on disk', colour: 'rgb(var(--w-blocked-rgb))' };
  if (h.usageLimitUntil) return { text: 'at its usage limit', colour: 'rgb(var(--w-gate-rgb))' };
  if (h.claudeOpen === false && live === 0) return { text: 'Claude accounts full', colour: 'rgb(var(--w-gate-rgb))' };
  return { text: live > 0 ? `working · ${live}` : 'ready', colour: 'rgb(var(--w-done-rgb))' };
}

export function WorkerHostsPanel() {
  const [s, setS] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [newName, setNewName] = useState('');
  const machines = useMachines();

  const load = useCallback(async () => {
    try {
      const r = await fetch('/api/hosts/settings');
      if (r.ok) setS(await r.json());
    } catch { /* keep what we have; the next poll tries again */ }
  }, []);

  useEffect(() => {
    load();
    const id = window.setInterval(load, 10000);
    return () => window.clearInterval(id);
  }, [load]);

  const act = async (url: string, method: string, body?: unknown) => {
    setBusy(true);
    const why = await send(url, method, body);
    setError(why);
    await load();
    setBusy(false);
  };

  const edit = (name: string, e: HostEdit) => act(`/api/hosts/settings/hosts/${encodeURIComponent(name)}`, 'PUT', e);
  const add = (name: string) => edit(name, {});

  if (!s) return null;
  // Hidden by Settings: only this computer's own card is drawn, and nothing about the hosts changes.
  const hostsShown = s.shown !== false;
  if (!hostsShown && !s.board) return null;

  const empty = s.hosts.length === 0 && s.seen.length === 0;
  const boardCard = s.board && (
    <BoardCard b={s.board} busy={busy}
      machine={machines?.machines.find(m => m.place === BOARD_PLACE)}
      onEdit={e => act('/api/hosts/settings/board', 'PUT', e)}
      onStop={pending => act(`/api/hosts/settings/board/${pending ? 'stop-after-runs' : 'cancel-stop'}`, 'POST')} />
  );

  if (!hostsShown) {
    return (
      <div>
        <SectionRule title="This computer" colour="var(--w-queue)" meta="the workers on this board's own machine" />
        {error && <p className="text-xs mb-2" style={{ color: 'rgb(var(--w-blocked-rgb))' }}>{error}</p>}
        <div className="grid grid-cols-1 xl:grid-cols-2 gap-3">{boardCard}</div>
      </div>
    );
  }

  return (
    <div>
      <SectionRule
        title="Worker hosts"
        colour="var(--w-queue)"
        count={s.hosts.length}
        meta={s.enabled ? 'other computers working this board' : 'remote work is off'}
        right={
          <Chip
            on={s.enabled}
            disabled={busy}
            onClick={() => act('/api/hosts/settings', 'PUT', { enabled: !s.enabled })}
            title={s.enabled
              ? 'Remote work is on. Click to stop giving any worker host new work; runs already out finish.'
              : 'Remote work is off. Click to let switched-on worker hosts take work again.'}
          >
            {s.enabled ? 'Remote work on' : 'Remote work off'}
          </Chip>
        }
      />

      {error && <p className="text-xs mb-2" style={{ color: 'rgb(var(--w-blocked-rgb))' }}>{error}</p>}

      {empty && (
        <p className="mb-3" style={{ font: `400 11px ${BODY}`, color: DIM }}>
          No other computers yet. A computer shows up here once its runner checks in, and then it can be
          added with one click. Setting one up: docs/worker-host-setup.md.
        </p>
      )}

      {s.hosts.length > 0 && s.dispatch && (
        <DispatchPanel s={s} busy={busy} onSave={e => act('/api/hosts/settings/dispatch', 'PUT', e)} />
      )}

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-3">
        {boardCard}
        {s.hosts.map(h => (
          <HostCard key={h.name} h={h} s={s} busy={busy} onEdit={e => edit(h.name, e)}
            onStop={pending => act(`/api/hosts/settings/hosts/${encodeURIComponent(h.name)}/${pending ? 'stop-after-runs' : 'cancel-stop'}`, 'POST')}
            machine={machines?.machines.find(m => m.place.toLowerCase() === h.name.toLowerCase())}
            onRemove={() => act(`/api/hosts/settings/hosts/${encodeURIComponent(h.name)}`, 'DELETE')} />
        ))}
      </div>

      {s.seen.length > 0 && (
        <div className="mt-3 space-y-2">
          {s.seen.map(m => (
            <Panel key={m.name} tone="var(--w-queue)">
              <div className="flex items-center gap-3 flex-wrap">
                <span style={{ font: `700 13px ${DISPLAY}`, letterSpacing: track(0.12), color: BRIGHT, textTransform: CAPS }}>{m.name}</span>
                <span className="min-w-0" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
                  checked in {ago(m.lastSeen)} but is not one of this board's worker hosts
                  {m.commit && s.boardCommit && m.commit !== s.boardCommit ? ` · on commit ${short(m.commit)}, the board is on ${short(s.boardCommit)}` : ''}
                </span>
                <span className="flex-1" />
                <GhostButton tone="go" disabled={busy} onClick={() => add(m.name)}
                  title="Add it switched off, with one berth and no projects. Nothing is sent to it until you choose.">
                  Add
                </GhostButton>
              </div>
            </Panel>
          ))}
        </div>
      )}

      <div className="mt-3 flex items-center gap-2 flex-wrap">
        <span style={label()}>Add by name</span>
        <input
          value={newName}
          onChange={e => setNewName(e.target.value)}
          onKeyDown={e => { if (e.key === 'Enter' && newName.trim()) { add(newName.trim()); setNewName(''); } }}
          placeholder="the name in that computer's host.json"
          className="flex-1 min-w-0 max-w-xs bg-gray-900 border border-hair rounded px-3 py-1.5 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500 font-mono"
        />
        <GhostButton disabled={busy || !newName.trim()} onClick={() => { add(newName.trim()); setNewName(''); }}>Add</GhostButton>
      </div>
    </div>
  );
}

/**
 * Where the orchestrator sends work first, and how it shares it out along that order. Read on its
 * next pass, like everything else here. Up and down rather than dragging, so it works under a thumb.
 */
function DispatchPanel({ s, busy, onSave }: {
  s: Settings;
  busy: boolean;
  onSave: (e: Partial<Dispatch>) => void;
}) {
  const { mode, order } = s.dispatch;
  const move = (i: number, by: number) => {
    const next = [...order];
    [next[i], next[i + by]] = [next[i + by], next[i]];
    onSave({ order: next });
  };

  return (
    <div className="mb-3">
      <Panel tone="var(--w-queue)">
        <div className="flex items-center gap-3 flex-wrap">
          <span style={label()}>Dispatch</span>
          <span className="flex-1" />
          {(Object.keys(MODE) as DispatchMode[]).map(m => (
            <Chip key={m} on={mode === m} disabled={busy} onClick={() => { if (mode !== m) onSave({ mode: m }); }} title={MODE[m].says}>
              {MODE[m].name}
            </Chip>
          ))}
        </div>
        <p className="mt-1" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>{MODE[mode].says}</p>

        <ol className="mt-2 space-y-1">
          {order.map((name, i) => {
            const h = s.hosts.find(x => x.name === name);
            const st = h ? stateOf(h) : s.board ? boardStateOf(s.board) : { text: 'this board\'s own workers', colour: MUTED };
            return (
              <li key={name} className="flex items-center gap-3 min-w-0 border border-hair px-2 py-1" style={{ clipPath: chamfer(7) }}>
                <span style={{ font: `700 13px ${MONO}`, color: DIM, minWidth: 18, textAlign: 'right' }}>{i + 1}</span>
                <span className="min-w-0 truncate" style={{ font: `700 12px ${DISPLAY}`, letterSpacing: track(0.12), color: BRIGHT, textTransform: CAPS }}>
                  {name === BOARD ? 'This computer' : name}
                </span>
                <span className="min-w-0 truncate max-sm:hidden" style={{ font: `600 10px ${DISPLAY}`, letterSpacing: track(0.2), color: st.colour, textTransform: CAPS }}>
                  {st.text}
                </span>
                <span className="flex-1" />
                <GhostButton disabled={busy || i === 0} onClick={() => move(i, -1)} title="Give work here sooner">↑</GhostButton>
                <GhostButton disabled={busy || i === order.length - 1} onClick={() => move(i, 1)} title="Give work here later">↓</GhostButton>
              </li>
            );
          })}
        </ol>
      </Panel>
    </div>
  );
}

function HostCard({ h, s, busy, onEdit, onStop, onRemove, machine }: {
  h: Settings['hosts'][number];
  s: Settings;
  /** Its hardware and suggested counts (docs/machine-limits-plan.md), once it has reported. */
  machine?: Machine;
  busy: boolean;
  onEdit: (e: HostEdit) => void;
  /** Start (true) or cancel (false) its "stop after current runs". */
  onStop: (pending: boolean) => void;
  onRemove: () => void;
}) {
  const st = stateOf(h);
  const live = h.runs.filter(r => !r.exited);
  const [confirmOff, setConfirmOff] = useState(false);
  // What a pending stop is waiting for: the API's count, which also holds starts not yet reported.
  const out = h.out ?? live.length;
  // On cancels a pending stop too (the API clears it on any On/Off). Off with runs out asks first.
  const flip = () => {
    if (h.enabled && !h.stopPending && live.length > 0) setConfirmOff(true);
    else onEdit({ enabled: h.stopPending ? true : !h.enabled });
  };
  const toggle = (list: string[], item: string) => (list.includes(item) ? list.filter(x => x !== item) : [...list, item]);
  const lastBerth = h.firstSlot + h.slots - 1;

  return (
    <Panel tone={h.enabled && h.remoteOn && h.online ? st.colour : undefined}>
      <div className="flex items-center gap-3 flex-wrap">
        <span style={{ font: `700 14px ${DISPLAY}`, letterSpacing: track(0.12), color: BRIGHT, textTransform: CAPS }}>{h.name}</span>
        <span style={{ font: `600 10px ${DISPLAY}`, letterSpacing: track(0.2), color: st.colour, textTransform: CAPS }}>{st.text}</span>
        <span className="flex-1" />
        {h.enabled && !h.stopPending && (
          <GhostButton tone="hold" disabled={busy} onClick={() => onStop(true)}
            title="Take no new work here. The runs it has out finish and hand back, then the board switches it off.">
            Stop after current runs
          </GhostButton>
        )}
        <Chip on={h.enabled && !h.stopPending} disabled={busy} onClick={flip}
          title={h.stopPending ? 'Cancel the pending stop and keep this computer on.'
            : h.enabled ? 'Switch this computer off: no new work; runs already out finish.' : 'Switch this computer on.'}>
          {h.enabled ? 'On' : 'Off'}
        </Chip>
      </div>

      {h.stopPending && (
        <div className="mt-2 flex items-center gap-2 flex-wrap">
          <span className="flex items-center gap-1.5 min-w-0" style={{ font: `400 11px ${BODY}`, color: 'rgb(var(--w-active-rgb))' }}>
            <span className="inline-block h-2 w-2 rounded-full animate-pulse flex-none" style={{ background: 'rgb(var(--w-active-rgb))' }} />
            {out > 0
              ? `Stopping - ${out} run${out === 1 ? '' : 's'} finishing, then off`
              : h.online ? 'Stopping - nothing out, switching off' : 'Stopping - switches off once it checks in with nothing out'}
          </span>
          <span className="flex-1" />
          <GhostButton tone="hold" disabled={busy} onClick={() => onStop(false)}
            title="Keep this computer on and let it take work again.">
            Cancel pending stop
          </GhostButton>
        </div>
      )}

      {confirmOff && (
        <ConfirmHostOffModal name={h.name} runs={live}
          onOff={() => onEdit({ enabled: false })}
          onStopAfter={() => onStop(true)}
          onClose={() => setConfirmOff(false)} />
      )}

      <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
        <span>checked in {ago(h.lastCheckin)}</span>
        <span>
          commit <span style={{ fontFamily: MONO }}>{short(h.commit)}</span>
          {!h.commitMatches && h.commit && <> · the board is on <span style={{ fontFamily: MONO }}>{short(s.boardCommit)}</span> - pull on that computer</>}
        </span>
        <span>berths {h.slots === 1 ? h.firstSlot : `${h.firstSlot}–${lastBerth}`}</span>
        {diskLine(h.disks) && <span>{diskLine(h.disks)}</span>}
        {h.windowsPowerShell && (
          <span style={{ color: 'rgb(var(--w-gate-rgb))' }}
            title="Its scheduled task was set up before the fleet left Windows PowerShell 5.1. The runner points the task at PowerShell 7 and restarts on it as soon as nothing is running there; if this stays, double-click Update-Runner.bat on that computer.">
            on Windows PowerShell 5.1 - moving to 7 when idle
          </span>
        )}
        {h.usageLimitUntil && <span>paused until {new Date(h.usageLimitUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>}
      </div>

      {h.diskLow && h.enabled && h.remoteOn && (
        <p className="mt-1 min-w-0 break-words" style={{ font: `400 11px ${BODY}`, color: 'rgb(var(--w-blocked-rgb))' }}>
          No new work there: {h.diskLow}. Its runner removes the worktrees of tickets Done for a while; free more by hand if that is not enough.
        </p>
      )}

      {machine?.inventory && (
        <div className="mt-0.5 min-w-0 break-words" style={{ font: `400 10.5px ${BODY}`, color: DIM }}>
          {hardwareLine(machine.inventory)}
        </div>
      )}

      {h.claude?.gated && (
        <div className="mt-0.5" style={{ font: `400 10.5px ${BODY}`, color: h.claude.account ? MUTED : 'rgb(var(--w-gate-rgb))' }}
          title="The smart switch chooses this computer's Claude account, from the logins it has, by the same limits as this machine's.">
          {h.claude.account ? <>Claude: {h.claude.account}</> : <>No Claude work: {h.claude.reason}</>}
        </div>
      )}

      {live.length > 0 && (
        <div className="mt-1" style={{ font: `400 10.5px ${BODY}`, color: BRIGHT }}>
          Working {live.map(r => `#${r.ticketId}${r.project ? ` (${r.project})` : ''}`).join(', ')}
        </div>
      )}

      <div className="mt-3 grid gap-2" style={{ gridTemplateColumns: 'minmax(0,auto) minmax(0,1fr)', alignItems: 'center' }}>
        <span style={label()}>Berths</span>
        <Stepper value={h.slots} min={1} max={8} busy={busy} onChange={v => onEdit({ slots: v })}
          hint="How many workers this computer runs at once." />
        {machine && <><span /><SlotSuggestionNote s={machine.workers} current={h.slots} busy={busy} onUse={n => onEdit({ slots: Math.min(n, 8) })} /></>}

        <span style={label()}>Stacks at once</span>
        <Stepper value={h.stackCopies} min={1} max={8} busy={busy} onChange={v => onEdit({ stackCopies: v })}
          hint="Copies of one project's stack this computer may run together. Above one, only for a project whose config sets parallelStacks." />
        {machine && <><span /><SlotSuggestionNote s={machine.qa} current={h.stackCopies} busy={busy} onUse={n => onEdit({ stackCopies: Math.min(n, 8) })} /></>}

        <span style={label()}>Phases</span>
        <div className="flex flex-wrap gap-1.5">
          {s.skills.map(k => (
            <Chip key={k} on={h.skills.includes(k)} disabled={busy}
              onClick={() => onEdit({ skills: toggle(h.skills, k) })}
              title={k === 'qa-test' || k === 'review-pr' ? 'Needs docker and that project\'s stack on this computer.' : undefined}>
              {SKILL_LABEL[k] ?? k}
            </Chip>
          ))}
        </div>

        <span style={label()}>Projects</span>
        <div className="flex flex-wrap gap-1.5">
          {s.projects.map(p => (
            <Chip key={p.key} on={h.projects.includes(p.key)} disabled={busy}
              onClick={() => onEdit({ projects: toggle(h.projects, p.key) })}
              title={h.projects.includes(p.key)
                ? `${p.displayName} may be sent to ${h.name}. Click to keep it on this board's computer.`
                : `Let ${h.name} work ${p.displayName}'s tickets. It needs a clone of the repo, named in its host.json.`}>
              {p.displayName || p.key}
            </Chip>
          ))}
          {h.projects.length === 0 && (
            <span style={{ font: `400 10.5px ${BODY}`, color: DIM }}>none - nothing is sent here until you pick one</span>
          )}
        </div>
      </div>

      <div className="mt-3 flex justify-end">
        <GhostButton tone="danger" disabled={busy || live.length > 0} onClick={onRemove}
          title={live.length > 0 ? 'It has work out. Switch it off and let its runs finish first.' : 'Remove this computer from the board.'}>
          Remove
        </GhostButton>
      </div>
    </Panel>
  );
}

/** What this computer's card says about it, in the same words and colours as a host's. */
function boardStateOf(b: BoardPlace): { text: string; colour: string } {
  if (!b.enabled) return { text: 'switched off', colour: DIM };
  if (b.stopPending) return { text: b.out > 0 ? `stopping · ${b.out} finishing` : 'stopping', colour: 'rgb(var(--w-active-rgb))' };
  if (b.diskLow) return { text: 'low on disk', colour: 'rgb(var(--w-blocked-rgb))' };
  return { text: b.out > 0 ? `working · ${b.out}` : 'ready', colour: 'rgb(var(--w-done-rgb))' };
}

/**
 * This computer: the board host's own workers, with the controls a worker host's card has. Berths
 * are slots 1..N here whatever N is, so changing the count renumbers nothing, and lowering it below
 * what is running only stops refilling. No phases or projects: what this machine claims is the
 * heartbeat's own Work on filter, which narrows every computer.
 */
function BoardCard({ b, busy, onEdit, onStop, machine }: {
  b: BoardPlace;
  busy: boolean;
  machine?: Machine;
  onEdit: (e: BoardEdit) => void;
  /** Start (true) or cancel (false) its "stop after current runs". */
  onStop: (pending: boolean) => void;
}) {
  const st = boardStateOf(b);
  const [confirmOff, setConfirmOff] = useState(false);
  const flip = () => {
    if (b.enabled && !b.stopPending && b.out > 0) setConfirmOff(true);
    else onEdit({ enabled: b.stopPending ? true : !b.enabled });
  };
  const runs = b.runs.map(r => ({ slot: r.slot, ticketId: Number(r.ticketId) || 0, runId: r.slot, project: r.project, exited: false }));

  return (
    <Panel tone={b.enabled ? st.colour : undefined}>
      <div className="flex items-center gap-3 flex-wrap">
        <span style={{ font: `700 14px ${DISPLAY}`, letterSpacing: track(0.12), color: BRIGHT, textTransform: CAPS }}>This computer</span>
        <span style={{ font: `600 10px ${DISPLAY}`, letterSpacing: track(0.2), color: st.colour, textTransform: CAPS }}>{st.text}</span>
        <span className="flex-1" />
        {b.enabled && !b.stopPending && (
          <GhostButton tone="hold" disabled={busy} onClick={() => onStop(true)}
            title="Take no new work on this computer. Its runs finish here, then it switches off. The heartbeat, the steward and the worker hosts carry on.">
            Stop after current runs
          </GhostButton>
        )}
        <Chip on={b.enabled && !b.stopPending} disabled={busy} onClick={flip}
          title={b.stopPending ? 'Cancel the pending stop and keep this computer on.'
            : b.enabled ? 'Switch this computer\'s workers off: no new work here; runs already going finish. The heartbeat keeps running.'
            : 'Switch this computer\'s workers on.'}>
          {b.enabled ? 'On' : 'Off'}
        </Chip>
      </div>

      {b.stopPending && (
        <div className="mt-2 flex items-center gap-2 flex-wrap">
          <span className="flex items-center gap-1.5 min-w-0" style={{ font: `400 11px ${BODY}`, color: 'rgb(var(--w-active-rgb))' }}>
            <span className="inline-block h-2 w-2 rounded-full animate-pulse flex-none" style={{ background: 'rgb(var(--w-active-rgb))' }} />
            {b.out > 0 ? `Stopping - ${b.out} run${b.out === 1 ? '' : 's'} finishing, then off` : 'Stopping - nothing running, switching off'}
          </span>
          <span className="flex-1" />
          <GhostButton tone="hold" disabled={busy} onClick={() => onStop(false)}
            title="Keep this computer on and let it take work again.">
            Cancel pending stop
          </GhostButton>
        </div>
      )}

      {confirmOff && (
        <ConfirmHostOffModal name="this computer" runs={runs} local
          onOff={() => onEdit({ enabled: false })}
          onStopAfter={() => onStop(true)}
          onClose={() => setConfirmOff(false)} />
      )}

      <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
        <span>berths {b.slots === 1 ? 1 : `1–${b.slots}`}</span>
        {diskLine(b.disks) && <span>{diskLine(b.disks)}</span>}
        <span>the steward runs beside these, in its own berth</span>
      </div>

      {b.enabled && !b.stopPending && b.diskLow && (
        <p className="mt-1 min-w-0 break-words" style={{ font: `400 11px ${BODY}`, color: 'rgb(var(--w-blocked-rgb))' }}>
          No new work here: {b.diskLow}. Runs already going finish; worker hosts carry on. Free some space and it takes work again on the heartbeat's next pass.
        </p>
      )}

      {machine?.inventory && (
        <div className="mt-0.5 min-w-0 break-words" style={{ font: `400 10.5px ${BODY}`, color: DIM }}>
          {hardwareLine(machine.inventory)}
        </div>
      )}

      {b.runs.length > 0 && (
        <div className="mt-1" style={{ font: `400 10.5px ${BODY}`, color: BRIGHT }}>
          Working {b.runs.map(r => `#${r.ticketId ?? '?'}${r.project ? ` (${r.project})` : ''}`).join(', ')}
        </div>
      )}

      <div className="mt-3 grid gap-2" style={{ gridTemplateColumns: 'minmax(0,auto) minmax(0,1fr)', alignItems: 'center' }}>
        <span style={label()}>Berths</span>
        <Stepper value={b.slots} min={1} max={b.maxSlots} busy={busy} onChange={v => onEdit({ slots: v })}
          hint="How many workers this computer runs at once. Applies on the heartbeat's next pass; lowering it stops nothing that is running." />
        {machine && <><span /><SlotSuggestionNote s={machine.workers} current={b.slots} busy={busy} onUse={n => onEdit({ slots: Math.min(n, b.maxSlots) })} /></>}

        <span style={label()}>Stacks at once</span>
        <Stepper value={b.stackCopies} min={1} max={b.maxStackCopies} busy={busy} onChange={v => onEdit({ stackCopies: v })}
          hint="Copies of one project's stack this computer may run together. Above one, only for a project whose config sets parallelStacks." />
        {machine && <><span /><SlotSuggestionNote s={machine.qa} current={b.stackCopies} busy={busy} onUse={n => onEdit({ stackCopies: Math.min(n, b.maxStackCopies) })} /></>}
      </div>
      {b.stackCopies > b.slots && (
        <p className="mt-2" style={{ font: `400 10.5px ${BODY}`, color: DIM }}>
          Only {b.slots} worker{b.slots === 1 ? '' : 's'} run{b.slots === 1 ? 's' : ''} here at once, so no more than {b.slots} of
          the {b.stackCopies} copies of a stack can be in use together.
        </p>
      )}
    </Panel>
  );
}

function Stepper({ value, min, max, busy, onChange, hint }: {
  value: number; min: number; max: number; busy: boolean; onChange: (v: number) => void; hint: string;
}) {
  return (
    <div className="flex items-center gap-2" title={hint}>
      <GhostButton disabled={busy || value <= min} onClick={() => onChange(value - 1)}>−</GhostButton>
      <span style={{ font: `700 13px ${MONO}`, color: BRIGHT, minWidth: 18, textAlign: 'center' }}>{value}</span>
      <GhostButton disabled={busy || value >= max} onClick={() => onChange(value + 1)}>+</GhostButton>
    </div>
  );
}
