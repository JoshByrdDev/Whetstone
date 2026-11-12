import { useEffect, useMemo, useState } from 'react';
import { Panel, SectionRule, DIM, MUTED, BRIGHT } from './ui';
import { BODY, MONO, DISPLAY, CAPS } from './orbit';
import { track } from '../theme';
import { label, ago } from './stewardParts';
import { useMachines, hardwareLine, machineName, type Machine, type MachineLoad, type MachineInventory } from '../hooks/useMachines';
import { SlotSuggestionNote } from './SlotSuggestionNote';

/**
 * Every machine in the fleet: what it is (and when that changed), how loaded it is, what its own
 * record suggests for its berths, and how its runs slowed with overlap - split into time waiting on
 * the model and time on the machine. docs/machine-limits-plan.md; Services/MachineLimits.cs.
 * The filter searches the hardware and its history, which is the "search" this section offers.
 */

const PRESSURE = ['normal', 'warning', 'critical'];

export function MachinesSection() {
  const m = useMachines();
  const [q, setQ] = useState('');
  const shown = useMemo(() => {
    if (!m) return [];
    const needle = q.trim().toLowerCase();
    if (!needle) return m.machines;
    return m.machines.filter(x => [x.place, hardwareLine(x.inventory), JSON.stringify(x.inventory?.tools ?? {}),
      ...x.history.map(h => hardwareLine(h.report))].join(' ').toLowerCase().includes(needle));
  }, [m, q]);
  if (!m) return null;

  return (
    <div className="mb-6">
      <SectionRule title="Machines" colour="var(--w-queue)" count={m.machines.length}
        meta={m.auto ? 'automatic slot limits on (not built yet)' : 'suggestions are advice only'} />
      <div className="flex items-center gap-2 mb-2 flex-wrap">
        <input value={q} onChange={e => setQ(e.target.value)} placeholder="Filter by CPU, OS, memory, tool version..."
          className="flex-1 min-w-0 max-w-sm bg-gray-900 border border-hair rounded px-3 py-1.5 text-sm text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500" />
      </div>
      {m.fleet && <p className="mb-2" style={{ font: `400 11px ${BODY}`, color: MUTED }}>{m.fleet}</p>}
      <div className="grid grid-cols-1 xl:grid-cols-2 gap-3">
        {shown.map(x => <MachineCard key={x.place} x={x} />)}
      </div>
      {shown.length === 0 && <p style={{ font: `400 11px ${BODY}`, color: DIM }}>No machine matches.</p>}
    </div>
  );
}

function MachineCard({ x }: { x: Machine }) {
  const l = x.load;
  return (
    <Panel>
      <div className="flex items-baseline gap-3 flex-wrap">
        <span style={{ font: `700 14px ${DISPLAY}`, letterSpacing: track(0.12), color: BRIGHT, textTransform: CAPS }}>{machineName(x.place)}</span>
        {x.inventorySince && <span style={{ font: `400 10.5px ${BODY}`, color: DIM }}>this hardware since {new Date(x.inventorySince).toLocaleDateString()}</span>}
      </div>
      <div className="mt-1 min-w-0 break-words" style={{ font: `400 11px ${BODY}`, color: MUTED }}>{hardwareLine(x.inventory)}</div>
      {x.inventory?.tools && Object.keys(x.inventory.tools).length > 0 && (
        <div className="mt-0.5 min-w-0 break-words" style={{ font: `400 10.5px ${MONO}`, color: DIM }}>
          {Object.entries(x.inventory.tools).map(([k, v]) => `${k} ${v}`).join('  ')}
        </div>
      )}

      {l && (
        <div className="mt-2 flex flex-wrap gap-x-4 gap-y-0.5" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
          <span>CPU <b style={{ color: BRIGHT }}>{l.cpuPct != null ? Math.round(l.cpuPct) : '-'}%</b></span>
          {l.memAvailMb != null && <span>available <b style={{ color: BRIGHT }}>{(l.memAvailMb / 1024).toFixed(1)}GB</b>{l.memTotalMb ? ` of ${Math.round(l.memTotalMb / 1024)}GB` : ''}</span>}
          {l.swapUsedMb != null && <span>swap {(l.swapUsedMb / 1024).toFixed(1)}GB</span>}
          {l.pressure != null && <span style={{ color: l.pressure > 0 ? 'rgb(var(--w-gate-rgb))' : undefined }}>
            {l.pressure > 0 ? '⚠ ' : ''}pressure {PRESSURE[l.pressure]}</span>}
          {l.diskFreeGb != null && <span>{Math.round(l.diskFreeGb)}GB free</span>}
          <span>{l.busy} busy</span>
          <span style={{ color: DIM }}>{ago(l.at)}</span>
        </div>
      )}
      {!l && <div className="mt-2" style={{ font: `400 10.5px ${BODY}`, color: DIM }}>No load samples yet.</div>}

      <LoadChart place={x.place} />

      <div className="mt-3 grid gap-x-3 gap-y-2" style={{ gridTemplateColumns: 'minmax(0,auto) minmax(0,1fr)', alignItems: 'start' }}>
        <span style={label()}>Berths</span>
        <SlotSuggestionNote s={x.workers} />
        <span style={label()}>QA at once</span>
        <SlotSuggestionNote s={x.qa} />
      </div>

      {x.speed && <SpeedTable s={x.speed} />}

      {x.history.length > 1 && (
        <div className="mt-3">
          <span style={label()}>Hardware changes</span>
          <ol className="mt-1 space-y-0.5" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
            {[...x.history].reverse().map((h, i, all) => (
              <li key={h.firstSeen} className="min-w-0 break-words">
                <span style={{ fontFamily: MONO, color: DIM }}>{new Date(h.firstSeen).toLocaleDateString()}</span>{' '}
                {i === all.length - 1 ? hardwareLine(h.report) : changed(all[i + 1].report, h.report)}
              </li>
            ))}
          </ol>
        </div>
      )}
    </Panel>
  );
}

/** What differs between two reports, as "RAM 16GB -> 32GB; node 22.1.0 -> 22.11.0". */
function changed(a: MachineInventory | null, b: MachineInventory | null): string {
  if (!a || !b) return hardwareLine(b);
  const out: string[] = [];
  const f = (k: keyof MachineInventory, name: string, fmt = (v: unknown) => String(v)) => {
    if (a[k] !== b[k] && typeof a[k] !== 'object') out.push(`${name} ${a[k] == null ? '-' : fmt(a[k])} -> ${b[k] == null ? '-' : fmt(b[k])}`);
  };
  f('cpuModel', 'CPU'); f('ramMb', 'RAM', v => `${Math.round(Number(v) / 1024)}GB`); f('osVersion', 'OS');
  f('diskKind', 'disk'); f('dockerMemMb', 'Docker', v => `${(Number(v) / 1024).toFixed(1)}GB`); f('dockerCpus', 'Docker CPUs');
  const ta = a.tools ?? {}, tb = b.tools ?? {};
  for (const k of new Set([...Object.keys(ta), ...Object.keys(tb)])) if (ta[k] !== tb[k]) out.push(`${k} ${ta[k] ?? '-'} -> ${tb[k] ?? '-'}`);
  return out.length ? out.join('; ') : 'reported again';
}

function SpeedTable({ s }: { s: NonNullable<Machine['speed']> }) {
  return (
    <div className="mt-3 min-w-0">
      <span style={label()}>Runs by how many overlapped here</span>
      <div className="overflow-x-auto">
        <table className="mt-1 w-full" style={{ font: `400 10.5px ${MONO}`, color: MUTED }}>
          <thead>
            <tr style={{ color: DIM, textAlign: 'right' }}>
              <th className="text-left font-normal">at once</th><th className="font-normal">runs</th><th className="font-normal">s/turn</th>
              <th className="font-normal">model</th><th className="font-normal">machine</th><th className="font-normal">work/day</th>
            </tr>
          </thead>
          <tbody>
            {s.points.map(p => (
              <tr key={p.others} style={{ textAlign: 'right' }}>
                <td className="text-left">{p.others + 1}</td><td>{p.runs}</td><td style={{ color: BRIGHT }}>{p.secPerTurn}</td>
                <td>{p.modelSecPerTurn}</td><td>{p.machineSecPerTurn}</td><td>{p.throughput}×</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="mt-1" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
        {s.summary} Most work per day at {s.mostWork ?? '-'}; each ticket fastest up to {s.fastest ?? '-'}.
      </p>
    </div>
  );
}

/**
 * The last day of one machine's minutes as two strips on one time axis: CPU %, and berths busy.
 * Two strips rather than one chart with two scales, so neither measure is read off the other's axis.
 */
function LoadChart({ place }: { place: string }) {
  const [rows, setRows] = useState<MachineLoad[] | null>(null);
  const [hover, setHover] = useState<number | null>(null);
  useEffect(() => {
    let live = true;
    fetch(`/api/machines/${encodeURIComponent(place)}/load?hours=24`).then(r => (r.ok ? r.json() : null))
      .then(j => { if (live && j) setRows(j); }).catch(() => {});
    return () => { live = false; };
  }, [place]);
  if (!rows || rows.length < 2) return null;

  const W = 600, H = 34, t0 = Date.parse(rows[0].at), t1 = Date.parse(rows[rows.length - 1].at);
  const x = (at: string) => ((Date.parse(at) - t0) / Math.max(1, t1 - t0)) * W;
  const maxBusy = Math.max(1, ...rows.map(r => r.busy));
  const line = (y: (r: MachineLoad) => number | null) =>
    rows.map(r => ({ r, v: y(r) })).filter(p => p.v != null)
      .map((p, i) => `${i ? 'L' : 'M'}${x(p.r.at).toFixed(1)},${(H - 2 - (p.v! * (H - 4))).toFixed(1)}`).join('');
  const cpu = line(r => (r.cpuPct == null ? null : r.cpuPct / 100));
  const busy = line(r => r.busy / maxBusy);
  const h = hover != null ? rows[hover] : null;
  const onMove = (e: React.PointerEvent<SVGSVGElement>) => {
    const b = e.currentTarget.getBoundingClientRect();
    const at = t0 + ((e.clientX - b.left) / b.width) * (t1 - t0);
    let best = 0;
    rows.forEach((r, i) => { if (Math.abs(Date.parse(r.at) - at) < Math.abs(Date.parse(rows[best].at) - at)) best = i; });
    setHover(best);
  };
  const strip = (name: string, d: string) => (
    <div className="mt-1">
      <div style={{ font: `400 9.5px ${BODY}`, color: DIM }}>{name}</div>
      <svg viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" className="w-full block" style={{ height: H }}
        onPointerMove={onMove} onPointerLeave={() => setHover(null)} role="img" aria-label={`${name}, last 24 hours`}>
        <line x1={0} x2={W} y1={H - 2} y2={H - 2} stroke="rgb(var(--w-line-rgb) / .15)" strokeWidth={1} vectorEffect="non-scaling-stroke" />
        <path d={d} fill="none" stroke="var(--w-queue)" strokeWidth={2} vectorEffect="non-scaling-stroke" strokeLinejoin="round" />
        {h && <line x1={x(h.at)} x2={x(h.at)} y1={0} y2={H} stroke="var(--w-t400)" strokeWidth={1} vectorEffect="non-scaling-stroke" />}
      </svg>
    </div>
  );
  return (
    <div className="mt-2">
      {strip('CPU %, last 24h (0-100)', cpu)}
      {strip(`Berths busy (0-${maxBusy})`, busy)}
      <div style={{ font: `400 10px ${MONO}`, color: h ? MUTED : DIM, minHeight: 14 }}>
        {h ? `${new Date(h.at).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} · CPU ${h.cpuPct != null ? Math.round(h.cpuPct) : '-'}% · ${h.memAvailMb != null ? `${(h.memAvailMb / 1024).toFixed(1)}GB available · ` : ''}${h.busy} busy`
          : 'Hover for a minute\'s reading.'}
      </div>
    </div>
  );
}
