import { useMemo } from 'react';
import { Section, useJson } from '../metrics/parts';
import { chamfer } from '../orbit';
import { useNarrow } from '../../hooks/useNarrow';
import { Fact, Notice, StateMark, cellPhrase, mono, secondsWord, sha7, shortDay, widthShort } from './parts';
import type { AreaRead, CoverageCell, ReconProject } from './types';

/**
 * Coverage (docs/recon-tab-plan.md, "The page" 2): a row per area, a column per role x width, each
 * cell saying when it was last scouted, whether its area's code has changed since, and its yield.
 * An empty cell is a gap. `(unmapped)` and `other` are there so the config can be fixed, and say so.
 *
 * Picking an area opens its drill-down: beside the grid at a desk (an `orbit-flank` grid, two tracks
 * either way so the width can move), in place of the grid on a phone, where there is no room beside
 * anything. On a phone the grid itself folds to a card per area with its cells as a wrap of chips.
 */

const UNMAPPED = '(unmapped)';

interface Column { role: string; width: string; roleOrdinal: number; widthOrdinal: number; inConfig: boolean }
interface AreaRow { area: string; ordinal: number; hasCode: boolean; cooling: boolean; cells: Map<string, CoverageCell> }

export const colKey = (role: string, width: string) => `${role}\u0000${width}`;

export function Coverage({ project, cells, error, area, onArea, onOpenTicket, bump }: {
  project: ReconProject; cells: CoverageCell[] | null; error: string | null;
  area: string | null; onArea: (a: string | null) => void; onOpenTicket: (id: number) => void; bump: number;
}) {
  const narrow = useNarrow();
  const { columns, rows } = useMemo(() => shape(cells ?? []), [cells]);
  const noRoles = project.roles.length === 0;

  const grid = (
    <div className="min-w-0 space-y-2">
      {noRoles && (
        <p className="text-[11px] text-gray-500">
          One <i>any role</i> column: gaps by role need <span style={mono}>discovery.roles</span> in this project&apos;s config.
        </p>
      )}
      {narrow
        ? <AreaCards rows={rows} columns={columns} area={area} onArea={onArea} />
        : <Grid rows={rows} columns={columns} area={area} onArea={onArea} />}
      <p className="text-[11px] text-gray-600">
        <StateMark state="gap" /> nobody has scouted it · <StateMark state="fresh" /> nothing changed since ·{' '}
        <StateMark state="changed" /> its code changed since · <StateMark state="unknown" /> scouted off the base ·{' '}
        <StateMark state="cooling" /> its last three discoveries had nothing approved. Yield is approved / shown.
      </p>
    </div>
  );

  return (
    <Section title="Coverage" meta={rows.length ? `${rows.length} area${rows.length === 1 ? '' : 's'} · ${columns.length} column${columns.length === 1 ? '' : 's'}` : undefined}>
      {error && <Notice warn>Could not read the coverage: {error}</Notice>}
      {!error && !cells && <Notice>Loading…</Notice>}
      {cells && rows.length === 0 && <Notice>Recon has no coverage rows for this project yet. Refresh to build them.</Notice>}
      {cells && rows.length > 0 && (narrow ? (
        // A phone: the drill-down takes the grid's place, with a way back.
        <div key={area ?? 'grid'} className="orbit-swap min-w-0">
          {area ? <AreaDrill project={project.key} area={area} onClose={() => onArea(null)} onOpenTicket={onOpenTicket} bump={bump} /> : grid}
        </div>
      ) : (
        <div className={`orbit-flank grid min-w-0 items-start ${area ? 'grid-cols-[minmax(0,1fr)_24rem] gap-4' : 'grid-cols-[minmax(0,1fr)_0rem] gap-0'}`}>
          {grid}
          <div className="min-w-0 overflow-hidden">
            {area && (
              <div key={area} className="orbit-swap">
                <AreaDrill project={project.key} area={area} onClose={() => onArea(null)} onOpenTicket={onOpenTicket} bump={bump} />
              </div>
            )}
          </div>
        </div>
      ))}
    </Section>
  );
}

/** A row's or a cell's hit area: a button where picking opens something, plain text where nothing does. */
function Hit({ pick, current, label, className, style, children }: {
  pick?: () => void; current?: boolean; label?: string; className: string; style?: React.CSSProperties; children: React.ReactNode;
}) {
  if (!pick) return <div className={className.replace(/orbit-(pick|tap)\s*/, '')} style={style} aria-label={label}>{children}</div>;
  return (
    <button className={className} style={style} onClick={pick} aria-label={label} aria-current={current ? 'true' : undefined}>
      {children}
    </button>
  );
}

/** The mart's cells as rows and columns, in config order; the extras the config did not name last. */
export function shape(cells: CoverageCell[]): { columns: Column[]; rows: AreaRow[] } {
  const cols = new Map<string, Column>();
  const rows = new Map<string, AreaRow>();
  for (const c of cells) {
    const k = colKey(c.role, c.width);
    const col = cols.get(k);
    if (!col) cols.set(k, { role: c.role, width: c.width, roleOrdinal: c.roleOrdinal, widthOrdinal: c.widthOrdinal, inConfig: c.inConfig });
    else if (c.inConfig) col.inConfig = true;
    let r = rows.get(c.area);
    if (!r) { r = { area: c.area, ordinal: c.areaOrdinal, hasCode: c.hasCode, cooling: c.cooling, cells: new Map() }; rows.set(c.area, r); }
    r.cells.set(k, c);
  }
  const columns = [...cols.values()].sort((a, b) =>
    Number(b.inConfig) - Number(a.inConfig) || a.roleOrdinal - b.roleOrdinal || a.widthOrdinal - b.widthOrdinal || a.role.localeCompare(b.role));
  const areaRows = [...rows.values()].sort((a, b) =>
    Number(a.area === UNMAPPED) - Number(b.area === UNMAPPED) || a.ordinal - b.ordinal || a.area.localeCompare(b.area));
  return { columns, rows: areaRows };
}

function AreaName({ row }: { row: AreaRow }) {
  return (
    <span className="min-w-0">
      <span className="text-gray-100" style={{ overflowWrap: 'anywhere' }}>{row.area}</span>
      {row.cooling && <span className="ml-2 text-[11px]"><StateMark state="cooling" /></span>}
      {row.area === UNMAPPED
        ? <span className="block text-[11px] text-gray-500">visits whose URL matches no area&apos;s pages - fix the patterns</span>
        : !row.hasCode && <span className="block text-[11px] text-gray-500">no code paths, so never stale</span>}
    </span>
  );
}

function CellBody({ c }: { c: CoverageCell | undefined }) {
  if (!c || c.state === 'gap') return <StateMark state="gap" />;
  return (
    <span className="block min-w-0 leading-snug">
      <StateMark state={c.state} />
      <span className="block text-gray-500">{shortDay(c.lastVisitedAt)}{c.state === 'changed' ? ` · ${c.commitsSince} commit${c.commitsSince === 1 ? '' : 's'} since` : ''}</span>
      <span className="block text-gray-500">{c.approved} / {c.shown}</span>
    </span>
  );
}

function Grid({ rows, columns, area, onArea }: { rows: AreaRow[]; columns: Column[]; area: string | null; onArea?: (a: string | null) => void }) {
  // Scrolls inside itself when a project has many roles: the page never does.
  return (
    <div className="min-w-0 overflow-x-auto border border-hair bg-panel" style={{ clipPath: chamfer(10) }}>
      <table className="w-full text-left text-[11px]">
        <thead className="text-gray-500">
          <tr className="border-b border-hair">
            <th className="px-3 py-2 font-medium">Area</th>
            {columns.map(col => (
              <th key={colKey(col.role, col.width)} className="px-3 py-2 font-medium">
                <span className={col.inConfig ? 'text-gray-300' : 'text-gray-500'}>{col.role}</span>
                <span className="block font-normal">{widthShort(col.width)}{col.inConfig ? '' : ' · not in config'}</span>
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {rows.map(r => (
            <tr key={r.area} className="border-b border-hair last:border-b-0 align-top">
              <td className="min-w-[9rem] px-1 py-1">
                <Hit pick={onArea && (() => onArea(area === r.area ? null : r.area))} className="orbit-pick w-full px-2 py-1.5 text-left text-xs"
                  current={area === r.area}>
                  <AreaName row={r} />
                </Hit>
              </td>
              {columns.map(col => {
                const c = r.cells.get(colKey(col.role, col.width));
                return (
                  <td key={colKey(col.role, col.width)} className="px-1 py-1">
                    <Hit pick={onArea && (() => onArea(r.area))} className="orbit-tap w-full px-2 py-1.5 text-left"
                      label={`${r.area} ${cellPhrase(col)}: ${c?.state ?? 'gap'}`}>
                      <CellBody c={c} />
                    </Hit>
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function AreaCards({ rows, columns, area, onArea }: { rows: AreaRow[]; columns: Column[]; area: string | null; onArea?: (a: string | null) => void }) {
  return (
    <div className="space-y-2">
      {rows.map(r => (
        <div key={r.area} className="min-w-0 border border-hair bg-panel" style={{ clipPath: chamfer(10) }}>
          <Hit pick={onArea && (() => onArea(r.area))} className="orbit-pick w-full px-3 py-2 text-left text-xs" current={area === r.area}>
            <AreaName row={r} />
          </Hit>
          <div className="flex min-w-0 flex-wrap gap-1.5 px-3 pb-3">
            {columns.map(col => {
              const c = r.cells.get(colKey(col.role, col.width));
              if (!c && !col.inConfig) return null;
              return (
                <Hit key={colKey(col.role, col.width)} pick={onArea && (() => onArea(r.area))}
                  className="orbit-tap min-w-0 border border-hair bg-sunk px-2 py-1 text-left text-[11px]" style={{ clipPath: chamfer(5) }}>
                  <span className="block text-gray-300">{col.role} · {widthShort(col.width)}</span>
                  <CellBody c={c} />
                </Hit>
              );
            })}
          </div>
        </div>
      ))}
    </div>
  );
}

/** One area: its route patterns, the discoveries that went there, and its commits since it was last scouted. */
function AreaDrill({ project, area, onClose, onOpenTicket, bump }: {
  project: string; area: string; onClose: () => void; onOpenTicket: (id: number) => void; bump: number;
}) {
  const { data, error } = useJson<AreaRead>(`/api/recon/${encodeURIComponent(project)}/areas/${encodeURIComponent(area)}`, bump);
  const sinceWord = data?.commits.some(c => c.since === 'recent') ? 'in the last 90 days (never scouted)'
    : data?.commits.some(c => c.since === 'by_date') ? 'since it was last scouted, by date (its last visit is off the base)'
    : 'since it was last scouted';

  return (
    <div className="min-w-0 space-y-4 border border-hair bg-panel p-4 text-xs" style={{ clipPath: chamfer(10) }}>
      <div className="flex min-w-0 items-start justify-between gap-3">
        <div className="min-w-0">
          <div className="text-sm text-gray-100" style={{ overflowWrap: 'anywhere' }}>{area}</div>
          {data?.config && (
            <>
              {data.config.about
                ? <p className="mt-1 text-gray-300" style={{ overflowWrap: 'anywhere' }}>{data.config.about}</p>
                : <p className="mt-1 text-gray-500">No <span style={mono}>about</span> yet: a sentence on who comes here and why is what a drafted direction is written from.</p>}
              {(data.config.roles ?? []).length > 0 && (
                <div className="mt-1 text-[11px] text-gray-400">Used as {data.config.roles.join(', ')}</div>
              )}
              <div className="mt-1 text-[11px] text-gray-500" style={{ ...mono, overflowWrap: 'anywhere' }}>
                pages {data.config.pages.join(', ') || '—'} · code {data.config.code.join(', ') || 'none'}
              </div>
            </>
          )}
        </div>
        <button className="orbit-tap flex-none text-[11px] text-gray-400 underline" onClick={onClose}>Close</button>
      </div>
      {error && <Notice warn>Could not read {area}: {error}</Notice>}
      {!error && !data && <p className="text-gray-500">Loading…</p>}
      {data && (
        <>
          <Block title="Route patterns visited">
            {data.patterns.length === 0 ? <p className="text-gray-500">No visit has landed here yet.</p> : (
              <ul className="space-y-1.5">
                {data.patterns.map(p => (
                  <li key={`${p.routePattern}|${p.role}|${p.width}`} className="min-w-0">
                    <span className="text-gray-200" style={{ ...mono, overflowWrap: 'anywhere' }}>{p.routePattern}</span>
                    <span className="block text-gray-500">
                      {cellPhrase(p)} · {p.visits} visit{p.visits === 1 ? '' : 's'}, {secondsWord(p.seconds)} · last {shortDay(p.lastVisitedAt)}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Block>
          <Block title="Discoveries that went here">
            {data.discoveries.length === 0 ? <p className="text-gray-500">None yet.</p> : (
              <ul className="space-y-2">
                {data.discoveries.map(d => (
                  <li key={d.discoveryId} className="min-w-0">
                    <button className="orbit-tap text-amber-300 underline" onClick={() => onOpenTicket(d.discoveryId)}>#{d.discoveryId}</button>
                    <span className="ml-2 text-gray-500">{shortDay(d.createdAt)} · {d.isDone ? 'done' : d.phase}</span>
                    <p className="mt-0.5 text-gray-300" style={{ overflowWrap: 'anywhere' }}>{d.direction ?? d.title ?? ''}</p>
                    <span className="text-gray-500">
                      here: {d.approved} approved of {d.shown} shown{d.newToMe ? `, ${d.newToMe} new to me` : ''}{d.filed ? `, ${d.filed} filed` : ''}
                      {d.visits ? ` · ${d.visits} visit${d.visits === 1 ? '' : 's'}, ${secondsWord(d.seconds)}` : ''}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </Block>
          <Block title={`Commits ${sinceWord}`}>
            {!data.config?.code.length && area !== UNMAPPED ? (
              <p className="text-gray-500">This area names no code paths, so Recon cannot say what changed in it.</p>
            ) : data.commits.length === 0 ? <p className="text-gray-500">Nothing in its code has changed.</p> : (
              <ul className="space-y-1.5">
                {data.commits.map(c => (
                  <li key={`${c.repo}|${c.sha}`} className="min-w-0">
                    <span className="text-gray-400" style={mono}>{c.repo ? `${c.repo}@` : ''}{sha7(c.sha)}</span>
                    <span className="ml-2 text-gray-500">{shortDay(c.committedAt)} · {c.files} file{c.files === 1 ? '' : 's'}</span>
                    <span className="block text-gray-300" style={{ overflowWrap: 'anywhere' }}>{c.subject}</span>
                    {c.tickets.length > 0 && (
                      <span className="flex flex-wrap gap-1.5">
                        {c.tickets.map(t => <button key={t} className="orbit-tap text-amber-300 underline" onClick={() => onOpenTicket(t)}>#{t}</button>)}
                      </span>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </Block>
          {data.cells.some(c => !c.inConfig) && (
            <Fact>Some visits here were as a role or at a width the config does not list; they are shown as their own columns.</Fact>
          )}
        </>
      )}
    </div>
  );
}

function Block({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="mb-1.5 text-[11px] uppercase tracking-wide text-gray-500">{title}</div>
      {children}
    </div>
  );
}
