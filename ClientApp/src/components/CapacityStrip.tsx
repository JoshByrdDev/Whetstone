import { useEffect, useState } from 'react';
import { SectionRule } from './ui';
import { chamfer, MONO } from './orbit';

/**
 * Why berths were free. Every machine's slot-minutes over the window, split by the one reason each
 * free minute was free for - read from what the claim saw when it passed tickets over, and from
 * whether anyone was on duty. Services/Capacity.cs is the other half; the reason keys are its
 * CapacityReasons.
 *
 * The bar is part-to-whole of slot time, so it is one stacked bar per machine, and the legend under
 * it is also the table: every segment is listed with its label and its hours, so no reason is told
 * by colour alone.
 */

interface Bucket { slotMinutes: number; busy: number; idle: Record<string, number> }
interface PlaceSummary { place: string; slots: number; total: Bucket }
interface Summary { from: string; to: string; samples: number; total: Bucket; places: PlaceSummary[] }
interface PlaceNow { place: string; slots: number; busy: number; reason: string | null; detail: Record<string, unknown> | null }
interface Capacity { summary: Summary; now: PlaceNow[]; offDuty: string[]; lastNothingReady: string | null }

const LABEL: Record<string, string> = {
  stopped: 'Heartbeat stopped',
  'off-hours': 'Off-hours',
  'host-off': 'Machine switched off',
  'host-offline': 'Machine not answering',
  'host-behind': "Catching up to the board's commit",
  'usage-limit': 'Usage limit',
  'account-full': 'Claude accounts over their limits',
  'nothing-ready': 'Nothing ready to work',
  dependency: 'Waiting on another ticket',
  resource: 'Waiting for a QA stack or emulator',
  analysis: 'Waiting for a Classroom analysis',
  'not-here': "Phase this machine doesn't run",
  filling: 'Between runs',
  unknown: 'Not yet known',
};

/**
 * Reasons fold into a few families, each with a tier colour - so the bar has six bands rather
 * than thirteen hues, and the colours follow the theme. The order is the bar's left-to-right.
 */
const FAMILIES: { key: string; label: string; swatch: string; reasons: string[] }[] = [
  { key: 'busy', label: 'Working', swatch: 'bg-emerald-500', reasons: [] },
  { key: 'waiting', label: 'Work waiting on something', swatch: 'bg-amber-500', reasons: ['resource', 'dependency', 'analysis'] },
  { key: 'empty', label: 'Nothing ready', swatch: 'bg-sky-500', reasons: ['nothing-ready'] },
  { key: 'machine', label: "Machine couldn't take it", swatch: 'bg-violet-500', reasons: ['usage-limit', 'account-full', 'host-behind', 'host-offline', 'not-here'] },
  { key: 'other', label: 'Other', swatch: 'bg-gray-500', reasons: ['filling', 'unknown'] },
  // Outlined: it is the empty part of the bar, and a fill this dark vanishes on the panel.
  { key: 'off', label: 'Off duty', swatch: 'bg-gray-700 ring-1 ring-inset ring-gray-500', reasons: ['stopped', 'off-hours', 'host-off'] },
];
const familyOf = (reason: string) => FAMILIES.find(f => f.reasons.includes(reason)) ?? FAMILIES[4];

const hours = (m: number) => (m < 60 ? `${Math.round(m)}m` : `${(m / 60).toFixed(m < 600 ? 1 : 0)}h`);
const pct = (n: number, d: number) => (d > 0 ? `${Math.round((100 * n) / d)}%` : '-');
const placeName = (p: string) => (p === '@board' ? 'This machine' : p);

const WHERE: Record<string, string> = {
  review: 'waiting for review', blocked: 'blocked', backlog: 'in the backlog',
  approval: 'waiting for approval', otherProjects: "in projects the heartbeat isn't working",
};

/** "When nothing was ready, the work was: 3 waiting for review, 1 blocked." */
function whereLine(json: string | null): string | null {
  if (!json) return null;
  try {
    const where = JSON.parse(json)?.where as Record<string, number> | undefined;
    const parts = Object.entries(where ?? {}).filter(([, n]) => n > 0).map(([k, n]) => `${n} ${WHERE[k] ?? k}`);
    return parts.length ? parts.join(', ') : 'no open tickets at all';
  } catch { return null; }
}

/** What one machine's free berths are doing right now, in a line. */
function nowLine(p: PlaceNow): string {
  const free = p.slots - p.busy;
  const head = `${p.busy} of ${p.slots} working`;
  if (free <= 0 || !p.reason) return head;
  let why = LABEL[p.reason] ?? p.reason;
  if (p.reason === 'resource' && p.detail) {
    const held = Object.keys(p.detail).filter(k => k.includes(':'));
    if (held.length) why = `Waiting for ${held.join(', ')}`;
  }
  return `${head} · ${free} free: ${why}`;
}

function Bar({ b }: { b: Bucket }) {
  const segs = FAMILIES.map(f => ({
    f,
    minutes: f.key === 'busy' ? b.busy : f.reasons.reduce((s, r) => s + (b.idle[r] ?? 0), 0),
  })).filter(s => s.minutes > 0);
  return (
    <div className="flex h-3 w-full gap-[2px]" role="img"
      aria-label={segs.map(s => `${s.f.label} ${hours(s.minutes)}`).join(', ')}>
      {segs.map((s, i) => (
        <div
          key={s.f.key}
          className={`${s.f.swatch} h-full ${i === 0 ? 'rounded-l-[4px]' : ''} ${i === segs.length - 1 ? 'rounded-r-[4px]' : ''}`}
          style={{ flexGrow: s.minutes, flexBasis: 0, minWidth: 2 }}
          title={`${s.f.label}: ${hours(s.minutes)} (${pct(s.minutes, b.slotMinutes)})`}
        />
      ))}
    </div>
  );
}

export function CapacityStrip({ days }: { days: number }) {
  const [data, setData] = useState<Capacity | null>(null);

  useEffect(() => {
    let live = true;
    const load = () => fetch(`/api/metrics/capacity?days=${Math.min(days, 30)}`)
      .then(r => (r.ok ? r.json() : null)).then(d => { if (live && d) setData(d); }).catch(() => {});
    load();
    const t = setInterval(load, 60_000);
    return () => { live = false; clearInterval(t); };
  }, [days]);

  // A server without the route says nothing rather than something wrong.
  if (!data) return null;
  const { summary, now, offDuty } = data;
  const total = summary.total;
  const offMinutes = offDuty.reduce((s, r) => s + (total.idle[r] ?? 0), 0);
  const onDuty = total.slotMinutes - offMinutes;
  const where = whereLine(data.lastNothingReady);

  // Every reason that has any time, biggest first, for the legend-as-table - off duty last.
  const rows = Object.entries(total.idle).filter(([, m]) => m > 0)
    .sort((a, b) => Number(offDuty.includes(a[0])) - Number(offDuty.includes(b[0])) || b[1] - a[1]);

  return (
    <section className="mb-8">
      <SectionRule title="Capacity" meta={`why berths were free · last ${Math.min(days, 30)} days`} />

      {summary.samples === 0 ? (
        <p className="text-sm text-gray-500">
          Sampling has just started. A sample is taken every minute; the first numbers appear shortly.
        </p>
      ) : (
        <div className="border border-hair bg-panel p-4" style={{ clipPath: chamfer(10) }}>
          <div className="flex flex-wrap items-baseline gap-x-6 gap-y-1">
            <div>
              <span className="text-2xl font-semibold text-gray-100">{pct(total.busy, onDuty)}</span>
              <span className="ml-2 text-xs text-gray-500">busy while on duty</span>
            </div>
            <div className="text-xs text-gray-500">
              {hours(total.busy)} working of {hours(onDuty)} slot time on duty
              {offMinutes > 0 && ` · ${hours(offMinutes)} off duty not counted`}
            </div>
          </div>

          <div className="mt-4 space-y-4">
            {summary.places.map(p => {
              const live = now.find(n => n.place === p.place);
              return (
                <div key={p.place} className="min-w-0">
                  <div className="mb-1.5 flex flex-wrap items-baseline gap-x-2">
                    <span className="text-sm font-medium text-gray-200">{placeName(p.place)}</span>
                    <span className="text-[11px] text-gray-500">{p.slots} slots</span>
                    {live && <span className="min-w-0 break-words text-[11px] text-gray-400">Now: {nowLine(live)}</span>}
                  </div>
                  <Bar b={p.total} />
                </div>
              );
            })}
          </div>

          <ul className="mt-4 grid grid-cols-1 gap-x-6 gap-y-1 sm:grid-cols-2">
            <li className="flex items-center gap-2 text-xs">
              <span className="h-2.5 w-2.5 shrink-0 rounded-[2px] bg-emerald-500" aria-hidden />
              <span className="min-w-0 flex-1 text-gray-300">Working</span>
              <span className="text-gray-400" style={{ fontFamily: MONO }}>{hours(total.busy)}</span>
              <span className="w-10 text-right text-gray-500" style={{ fontFamily: MONO }}>{pct(total.busy, onDuty)}</span>
            </li>
            {rows.map(([reason, m]) => (
              <li key={reason} className="flex items-center gap-2 text-xs">
                <span className={`h-2.5 w-2.5 shrink-0 rounded-[2px] ${familyOf(reason).swatch}`} aria-hidden />
                <span className="min-w-0 flex-1 text-gray-300">{LABEL[reason] ?? reason}</span>
                <span className="text-gray-400" style={{ fontFamily: MONO }}>{hours(m)}</span>
                {/* A share of on-duty time, like the headline; off-duty time is not part of that whole. */}
                <span className="w-10 text-right text-gray-500" style={{ fontFamily: MONO }}>
                  {offDuty.includes(reason) ? '' : pct(m, onDuty)}
                </span>
              </li>
            ))}
          </ul>

          {where && (
            <p className="mt-3 text-[11px] text-gray-500">
              The last time nothing was ready, the open work was {where}.
            </p>
          )}
        </div>
      )}
    </section>
  );
}
