import { Chip } from '../ui';
import { Section, useJson, TH, TD, NUM } from '../metrics/parts';
import { chamfer } from '../orbit';
import { useNarrow } from '../../hooks/useNarrow';
import { Notice, money, shortDay } from './parts';
import type { ScoutAsk, YieldRead, YieldRow } from './types';

/**
 * Yield (docs/recon-tab-plan.md, "The page" 3): where looking has paid, by area, by direction
 * lineage - a direction and every rerun of it counted together - and by where the direction came
 * from: the owner's own words, a drafted one taken as written or edited, a rerun. That last is the
 * drafting experiment's scoreboard. Read whole from
 * `mart_recon_yield` for one window; a cohort by when each discovery was filed. Cost shows only
 * where every discovery behind the row is priced, as the mart says.
 *
 * A direction's row has Scout again: a rerun suggestion by hand, filed as `rerun:<its latest discovery>`.
 */

const WINDOWS = [{ d: 7, label: '7d' }, { d: 30, label: '30d' }, { d: 90, label: '90d' }, { d: 0, label: 'All' }];

function rejectedWords(r: YieldRow): string {
  const parts = [
    r.rejectedGeneric ? `${r.rejectedGeneric} generic` : '',
    r.rejectedAlreadyDecided ? `${r.rejectedAlreadyDecided} already decided` : '',
    r.rejectedWrong ? `${r.rejectedWrong} wrong` : '',
    r.rejectedNotNow ? `${r.rejectedNotNow} not now` : '',
  ].filter(Boolean);
  return parts.length ? parts.join(', ') : '';
}

const cost = (r: YieldRow) => (r.costComplete ? money(r.costUsd) : 'not priced');

/** Ticket.DirectionSource, said as the owner would. */
const SOURCE_WORD: Record<string, string> = {
  owner: 'You',
  draft: 'A draft, as written',
  'draft-edited': 'A draft you edited',
  rerun: 'A rerun, word for word',
};

export function Yield({ project, days, setDays, onScout, bump }: {
  project: string; days: number; setDays: (d: number) => void; bump: number;
  /** Null when a discovery cannot be filed here: the direction rows then carry no Scout again. */
  onScout: ((ask: ScoutAsk) => void) | null;
}) {
  const { data, error } = useJson<YieldRead>(`/api/recon/${encodeURIComponent(project)}/yield?days=${days}`, bump);

  const again = (r: YieldRow) => onScout?.({
    key: `rerun:${r.latestDiscoveryId}`,
    title: `Scout again: ${r.label ?? `#${r.cutKey}`}`,
    direction: r.latestDirection ?? '',
    note: `From Recon: scouting again the direction last run as #${r.latestDiscoveryId}.`,
  });

  return (
    <Section title="Yield" right={
      <div className="flex flex-wrap gap-1.5">
        {WINDOWS.map(w => <Chip key={w.d} on={days === w.d} onClick={() => setDays(w.d)}>{w.label}</Chip>)}
      </div>
    }>
      {error && <Notice warn>Could not read the yield: {error}</Notice>}
      {!error && !data && <Notice>Loading…</Notice>}
      {data && (
        <div key={days} className="orbit-swap space-y-4">
          {data.areas.length === 0 && data.directions.length === 0 ? (
            <Notice>No discovery of this project was filed {days ? `in the last ${days} days` : 'yet'}, so nothing has paid or not paid.</Notice>
          ) : (
            <>
              <Cut title="By area" rows={data.areas} />
              <Cut title="By direction" rows={data.directions} onAgain={onScout ? again : undefined} direction />
              {(data.sources ?? []).length > 0 && <Cut title="By who wrote the direction" rows={data.sources} head="Written by" labels={SOURCE_WORD} />}
            </>
          )}
        </div>
      )}
    </Section>
  );
}

function Cut({ title, rows, onAgain, direction, head: headWord, labels }: {
  title: string; rows: YieldRow[]; onAgain?: (r: YieldRow) => void; direction?: boolean;
  head?: string; labels?: Record<string, string>;
}) {
  const narrow = useNarrow();
  if (rows.length === 0) return null;
  const head = headWord ?? (direction ? 'Direction' : 'Area');
  const named = (r: YieldRow): YieldRow => (labels ? { ...r, label: labels[r.cutKey] ?? r.label } : r);
  return (
    <div className="min-w-0">
      <div className="mb-1.5 text-[11px] uppercase tracking-wide text-gray-500">{title}</div>
      {narrow ? (
        <div className="space-y-2">
          {rows.map(r => (
            <div key={r.cutKey} className="min-w-0 border border-hair bg-panel px-3 py-2 text-xs" style={{ clipPath: chamfer(10) }}>
              <Label r={named(r)} direction={!!direction} />
              <div className="mt-1 text-gray-400">
                {r.discoveries} discover{r.discoveries === 1 ? 'y' : 'ies'} · {r.approved} approved of {r.shown} shown
                {r.newToMe ? ` · ${r.newToMe} new to me` : ''}{r.filed ? ` · ${r.filed} filed` : ''}
              </div>
              <div className="text-gray-500">
                {r.rejected ? `${r.rejected} rejected (${rejectedWords(r)})` : 'none rejected'} · {cost(r)}
              </div>
              {onAgain && r.latestDiscoveryId != null && (
                <AgainButton onClick={() => onAgain(r)} />
              )}
            </div>
          ))}
        </div>
      ) : (
        <div className="min-w-0 overflow-x-auto border border-hair bg-panel" style={{ clipPath: chamfer(10) }}>
          <table className="w-full text-left text-xs">
            <thead className="text-[11px] text-gray-500">
              <tr className="border-b border-hair">
                <th className={TH}>{head}</th>
                <th className={`${TH} ${NUM}`}>Discoveries</th>
                <th className={`${TH} ${NUM}`}>Shown</th>
                <th className={`${TH} ${NUM}`}>Approved</th>
                <th className={TH}>Rejected</th>
                <th className={`${TH} ${NUM}`}>New to me</th>
                <th className={`${TH} ${NUM}`}>Filed</th>
                <th className={`${TH} ${NUM}`}>Cost</th>
                {onAgain && <th className={TH} />}
              </tr>
            </thead>
            <tbody>
              {rows.map(r => (
                <tr key={r.cutKey} className="border-b border-hair last:border-b-0 align-top text-gray-300">
                  <td className={`${TD} min-w-[12rem]`}><Label r={named(r)} direction={!!direction} /></td>
                  <td className={`${TD} ${NUM}`}>{r.discoveries}</td>
                  <td className={`${TD} ${NUM}`}>{r.shown}</td>
                  <td className={`${TD} ${NUM}`}>{r.approved}</td>
                  <td className={`${TD} text-gray-400`}>{r.rejected}{r.rejected ? <span className="block text-[11px] text-gray-500">{rejectedWords(r)}</span> : null}</td>
                  <td className={`${TD} ${NUM}`}>{r.newToMe}</td>
                  <td className={`${TD} ${NUM}`}>{r.filed}</td>
                  <td className={`${TD} ${NUM} text-gray-400`}>{cost(r)}</td>
                  {onAgain && <td className={TD}>{r.latestDiscoveryId != null && <AgainButton onClick={() => onAgain(r)} />}</td>}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

function Label({ r, direction }: { r: YieldRow; direction: boolean }) {
  return (
    <span className="block min-w-0">
      <span className="text-gray-100" style={{ overflowWrap: 'anywhere' }}>{r.label ?? r.cutKey}</span>
      {direction && r.latestDirection && r.latestDirection !== r.label && (
        <span className="block text-[11px] text-gray-500" style={{ overflowWrap: 'anywhere' }}>{r.latestDirection}</span>
      )}
      {direction && r.latestAt && <span className="block text-[11px] text-gray-600">last run {shortDay(r.latestAt)} as #{r.latestDiscoveryId}</span>}
    </span>
  );
}

function AgainButton({ onClick }: { onClick: () => void }) {
  return (
    <button className="orbit-tap mt-1 whitespace-nowrap border border-hair bg-sunk px-2 py-0.5 text-[11px] text-gray-300"
      style={{ clipPath: chamfer(5) }} onClick={onClick}>
      Scout again
    </button>
  );
}
