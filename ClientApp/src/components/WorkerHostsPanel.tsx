import { useCallback, useEffect, useState } from 'react';
import { Chip, GhostButton, Panel, SectionRule, DIM, MUTED, BRIGHT } from './ui';
import { DISPLAY, MONO, BODY, CAPS } from './orbit';
import { label, ago } from './stewardParts';
import { track } from '../theme';
import type { WorkerHost } from '../hooks/useWorkerHosts';

/**
 * The board's worker hosts: other computers that run workers for it and hand their branches back
 * (docs/worker-hosts-plan.md). One switch for all remote work, then a panel per computer - is it
 * answering, is it on this board's commit, what is it running - with what it may be given: its
 * berths, how many QA stacks at once, which phases, which projects.
 *
 * Every change saves as it is made and applies on the orchestrator's next pass; nothing restarts.
 * A computer starts switched off with no projects, so nothing reaches it until its owner says so.
 */

interface Settings {
  enabled: boolean;
  boardCommit?: string | null;
  hosts: (WorkerHost & { skills: string[]; projects: string[]; qaLeases: number; remoteOn: boolean; commit?: string | null })[];
  seen: { name: string; lastSeen: string; commit?: string | null }[];
  projects: { key: string; displayName: string }[];
  skills: string[];
}

type HostEdit = Partial<{ enabled: boolean; slots: number; qaLeases: number; skills: string[]; projects: string[] }>;

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
  if (!h.online) return { text: h.lastCheckin ? 'offline' : 'never checked in', colour: 'rgb(var(--w-blocked-rgb))' };
  if (!h.commitMatches) return { text: 'on another commit', colour: 'rgb(var(--w-gate-rgb))' };
  if (h.usageLimitUntil) return { text: 'at its usage limit', colour: 'rgb(var(--w-gate-rgb))' };
  return { text: live > 0 ? `working · ${live}` : 'ready', colour: 'rgb(var(--w-done-rgb))' };
}

export function WorkerHostsPanel() {
  const [s, setS] = useState<Settings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [newName, setNewName] = useState('');

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

  const empty = s.hosts.length === 0 && s.seen.length === 0;

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
        <p style={{ font: `400 11px ${BODY}`, color: DIM }}>
          No other computers yet. A computer shows up here once its runner checks in, and then it can be
          added with one click. Setting one up: docs/worker-host-setup.md.
        </p>
      )}

      <div className="grid grid-cols-1 xl:grid-cols-2 gap-3">
        {s.hosts.map(h => (
          <HostCard key={h.name} h={h} s={s} busy={busy} onEdit={e => edit(h.name, e)}
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

function HostCard({ h, s, busy, onEdit, onRemove }: {
  h: Settings['hosts'][number];
  s: Settings;
  busy: boolean;
  onEdit: (e: HostEdit) => void;
  onRemove: () => void;
}) {
  const st = stateOf(h);
  const live = h.runs.filter(r => !r.exited);
  const toggle = (list: string[], item: string) => (list.includes(item) ? list.filter(x => x !== item) : [...list, item]);
  const lastBerth = h.firstSlot + h.slots - 1;

  return (
    <Panel tone={h.enabled && h.remoteOn && h.online ? st.colour : undefined}>
      <div className="flex items-center gap-3 flex-wrap">
        <span style={{ font: `700 14px ${DISPLAY}`, letterSpacing: track(0.12), color: BRIGHT, textTransform: CAPS }}>{h.name}</span>
        <span style={{ font: `600 10px ${DISPLAY}`, letterSpacing: track(0.2), color: st.colour, textTransform: CAPS }}>{st.text}</span>
        <span className="flex-1" />
        <Chip on={h.enabled} disabled={busy} onClick={() => onEdit({ enabled: !h.enabled })}
          title={h.enabled ? 'Switch this computer off: no new work; runs already out finish.' : 'Switch this computer on.'}>
          {h.enabled ? 'On' : 'Off'}
        </Chip>
      </div>

      <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
        <span>checked in {ago(h.lastCheckin)}</span>
        <span>
          commit <span style={{ fontFamily: MONO }}>{short(h.commit)}</span>
          {!h.commitMatches && h.commit && <> · the board is on <span style={{ fontFamily: MONO }}>{short(s.boardCommit)}</span> - pull on that computer</>}
        </span>
        <span>berths {h.slots === 1 ? h.firstSlot : `${h.firstSlot}–${lastBerth}`}</span>
        {h.usageLimitUntil && <span>paused until {new Date(h.usageLimitUntil).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}</span>}
      </div>

      {live.length > 0 && (
        <div className="mt-1" style={{ font: `400 10.5px ${BODY}`, color: BRIGHT }}>
          Working {live.map(r => `#${r.ticketId}${r.project ? ` (${r.project})` : ''}`).join(', ')}
        </div>
      )}

      <div className="mt-3 grid gap-2" style={{ gridTemplateColumns: 'minmax(0,auto) minmax(0,1fr)', alignItems: 'center' }}>
        <span style={label()}>Berths</span>
        <Stepper value={h.slots} min={1} max={8} busy={busy} onChange={v => onEdit({ slots: v })}
          hint="How many workers this computer runs at once." />

        <span style={label()}>QA at once</span>
        <Stepper value={h.qaLeases} min={1} max={8} busy={busy} onChange={v => onEdit({ qaLeases: v })}
          hint="Copies of one project's stack this computer may run together. Above one, only for a project whose config sets parallelStacks." />

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
