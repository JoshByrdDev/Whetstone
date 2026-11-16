import type { BugOriginRow } from './types';
import { MONO } from '../orbit';
import { Empty, NUM, Section, SegBar, Sheet, Stat, Swatch, TD, TH } from './parts';

/**
 * Where bugs came from (docs/intake-plan.md, section 6): every bug ticket by its origin - a
 * production report intake filed, or something found in-house by the owner, a run, a steward, or a
 * Done ticket's problem coming back - per week it was filed. The question this exists to answer is
 * "how many bugs came from production this month, against how many were found in-house".
 */

/** Each kind of origin, in the order a bar reads: production first, then in-house. */
const KINDS: { key: string; label: string; swatch: string; match: (r: BugOriginRow) => boolean }[] = [
  { key: 'sentry', label: 'Error reports', swatch: 'bg-red-500', match: r => r.origin === 'intake' && r.channel === 'sentry' },
  { key: 'alarm', label: 'Alarms', swatch: 'bg-red-300', match: r => r.origin === 'intake' && r.channel !== 'sentry' },
  { key: 'owner', label: 'The owner', swatch: 'bg-sky-500', match: r => r.origin === 'owner' },
  { key: 'found-in-run', label: 'Found in a run', swatch: 'bg-emerald-500', match: r => r.origin === 'found-in-run' },
  { key: 'steward', label: 'A steward', swatch: 'bg-violet-500', match: r => r.origin === 'steward' },
  { key: 'regression', label: 'Came back', swatch: 'bg-amber-500', match: r => r.origin === 'regression' },
];

const sum = (rows: BugOriginRow[], f: (r: BugOriginRow) => number) => rows.reduce((n, r) => n + f(r), 0);
const hours = (h: number | null | undefined) => (h == null ? '—' : h < 48 ? `${h.toFixed(1)}h` : `${(h / 24).toFixed(1)}d`);

export function BugOrigins({ rows }: { rows: BugOriginRow[] | undefined }) {
  if (!rows) return null;
  const all = rows.filter(r => r.week === 'all');
  const weeks = [...new Set(rows.filter(r => r.week !== 'all').map(r => r.week))].sort().reverse();
  const prod = all.filter(r => r.origin === 'intake');
  const home = all.filter(r => r.origin !== 'intake');
  const prodN = sum(prod, r => r.filed);
  const homeN = sum(home, r => r.filed);
  const max = Math.max(1, ...weeks.map(w => sum(rows.filter(r => r.week === w), r => r.filed)));
  // A median over several rows is not theirs to combine: shown only where one row is the answer.
  const prodMedian = prod.length === 1 ? prod[0].medianHoursToDone : null;

  return (
    <Section title="Where bugs came from" meta="every bug ticket · by when it was filed">
      {all.length === 0 ? <Empty>No bug ticket was filed in this window.</Empty> : (
        <>
          <div className="mb-3 flex flex-wrap gap-3">
            <Stat label="From production" value={String(prodN)} muted={!prodN}
              context={prodN ? `${sum(prod.filter(r => r.channel === 'sentry'), r => r.filed)} error reports · ${sum(prod.filter(r => r.channel !== 'sentry'), r => r.filed)} alarms or storms` : 'filed by intake'} />
            <Stat label="Found in-house" value={String(homeN)} muted={!homeN}
              context={homeN && prodN + homeN ? `${Math.round((100 * homeN) / (prodN + homeN))}% of bugs` : 'the owner, a run, a steward'} />
            <Stat label="Regressions" value={String(sum(all, r => r.regressions))} muted={!sum(all, r => r.regressions)}
              context="a Done ticket's problem came back" />
            <Stat label="Production to Done" value={hours(prodMedian)} muted={prodMedian == null}
              context={prod.length > 1 ? 'see each channel below' : 'median, filed to Done'} />
          </div>
          <Sheet pad={false}>
            <table className="w-full table-fixed text-xs">
              <thead>
                <tr className="border-b border-hair text-gray-500">
                  <th className={`${TH} w-[6.5rem] text-left`}>Week of</th>
                  <th className={`${TH} text-left`}>Filed</th>
                  <th className={`${TH} ${NUM} w-12`}>Prod</th>
                  <th className={`${TH} ${NUM} w-12`}>Home</th>
                  <th className={`${TH} ${NUM} w-14 max-sm:hidden`}>Done</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-hairsoft">
                {weeks.map(w => {
                  const wr = rows.filter(r => r.week === w);
                  const total = sum(wr, r => r.filed);
                  return (
                    <tr key={w}>
                      <td className={`${TD} text-gray-400`} style={{ fontFamily: MONO }}>{w}</td>
                      <td className={TD}>
                        <SegBar width={total / max} parts={KINDS.map(k => {
                          const n = sum(wr.filter(k.match), r => r.filed);
                          return { key: k.key, label: k.label, swatch: k.swatch, value: n, title: String(n) };
                        })} />
                      </td>
                      <td className={`${TD} ${NUM} text-gray-100`} style={{ fontFamily: MONO }}>{sum(wr.filter(r => r.origin === 'intake'), r => r.filed)}</td>
                      <td className={`${TD} ${NUM} text-gray-300`} style={{ fontFamily: MONO }}>{sum(wr.filter(r => r.origin !== 'intake'), r => r.filed)}</td>
                      <td className={`${TD} ${NUM} text-gray-500 max-sm:hidden`} style={{ fontFamily: MONO }}>{sum(wr, r => r.done)}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
            <ul className="flex flex-wrap gap-x-4 gap-y-1 border-t border-hair px-3 py-2 text-[10.5px] text-gray-500">
              {KINDS.map(k => {
                const rs = all.filter(k.match);
                const n = sum(rs, r => r.filed);
                return (
                  <li key={k.key} className="flex items-center gap-1.5">
                    <Swatch swatch={k.swatch} />
                    <span>{k.label}</span>
                    <span className="text-gray-300" style={{ fontFamily: MONO }}>{n}</span>
                    {rs.length === 1 && rs[0].medianHoursToDone != null && <span title="median, filed to Done">· {hours(rs[0].medianHoursToDone)}</span>}
                  </li>
                );
              })}
            </ul>
          </Sheet>
        </>
      )}
    </Section>
  );
}
