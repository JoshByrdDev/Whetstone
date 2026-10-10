import type { CSSProperties } from 'react';
import { useHeartbeatProjects } from '../hooks/useHeartbeatProjects';
import type { ProjectInfo } from '../types/tickets';
import { CAPS, DISPLAY, chamfer } from './orbit';
import { track } from '../theme';

/**
 * Two skins on one switch: which projects the heartbeat may claim tickets from.
 *
 * The rule and the round trip live in {@link useHeartbeatProjects}; these only draw it. The
 * Heartbeat page gets the roomy version, the Board a strip narrow enough for its manifest line —
 * because the moment you notice the fleet is on the wrong client is usually the moment you are
 * looking at the board, and a setting you have to change tabs to reach is one you change late.
 */

/** Roomy version, for the Heartbeat page, under the start/stop controls. */
export function HeartbeatProjectFilter() {
  const { projects, selected, toggle, saving, error, loaded, allSelected } = useHeartbeatProjects();

  // Nothing to choose between with 0-1 projects.
  if (!loaded || projects.length <= 1) return null;

  return (
    <div className="flex items-center gap-2 flex-wrap text-xs">
      <span className="text-gray-500 uppercase tracking-wider font-semibold">Work on</span>
      {projects.map(p => {
        const on = selected.has(p.key);
        return (
          <button
            key={p.key}
            onClick={() => toggle(p.key)}
            title={dutyHint(p, on, selected.size)}
            className={`px-2.5 py-1 rounded border transition-colors ${
              on
                ? 'bg-emerald-500/15 border-emerald-500/40 text-emerald-200'
                : 'bg-gray-800 border-hair text-gray-500 hover:text-gray-300'
            }`}
          >
            {on ? '✓ ' : ''}{p.displayName}
          </button>
        );
      })}
      <span className="text-gray-600">
        {allSelected ? 'all projects (new ones auto-included)' : `${selected.size} of ${projects.length}`}
        {' · applies next cycle'}{saving ? ' · saving…' : ''}
      </span>
      {error && <span className="text-rose-300">{error}</span>}
    </div>
  );
}

/**
 * Narrow version, for the Board's manifest line.
 *
 * Deliberately not the chips in the page head: those choose what the board SHOWS, these choose what
 * the fleet may TOUCH. What separates them is the label and the line they sit on, not the hue —
 * several themes aim the working tier and the accent at the same colour on purpose, so an on-duty
 * chip here and a selected filter chip up there do look alike. The state worth reading at a glance
 * is the other one anyway: a stood-down project goes dim, and the count says how many are left.
 */
export function HeartbeatProjectStrip({ projects: supplied }: { projects: ProjectInfo[] }) {
  const { projects, selected, toggle, saving, error, loaded, allSelected } = useHeartbeatProjects(supplied);

  if (!loaded || projects.length <= 1) return null;

  return (
    <div className="flex items-center gap-1.5">
      <span style={{
        font: `600 9.5px ${DISPLAY}`, letterSpacing: track(.22),
        textTransform: CAPS, color: 'var(--w-t600)',
      }}>
        Work on
      </span>

      {projects.map(p => {
        const on = selected.has(p.key);
        return (
          <button
            key={p.key}
            onClick={() => toggle(p.key)}
            title={dutyHint(p, on, selected.size)}
            className="orbit-btn"
            style={{
              clipPath: chamfer(7),
              padding: '3px 9px',
              font: `700 9px ${DISPLAY}`, letterSpacing: track(.22), textTransform: CAPS,
              color: on ? 'var(--w-active-200)' : 'var(--w-t600)',
              // Resting colours as custom properties, never as `background`/`border` - see .orbit-btn.
              ['--rbg' as string]: on ? 'rgb(var(--w-active-rgb) / .14)' : 'rgb(var(--w-line-rgb) / .04)',
              ['--rbw' as string]: `1px solid ${on ? 'rgb(var(--w-active-rgb) / .4)' : 'rgb(var(--w-line-rgb) / .14)'}`,
              ['--hbg' as string]: on ? 'rgb(var(--w-active-rgb) / .22)' : 'rgb(var(--w-line-rgb) / .12)',
              ['--hbc' as string]: on ? 'rgb(var(--w-active-rgb) / .65)' : 'rgb(var(--w-line-rgb) / .3)',
              ['--htxt' as string]: on ? 'var(--w-active-200)' : 'var(--w-t400)',
              ['--hsh' as string]: 'none',
            } as CSSProperties}
          >
            {p.key}
          </button>
        );
      })}

      <span style={{ color: 'var(--w-t600)' }}>
        {allSelected ? 'all' : `${selected.size} of ${projects.length}`}
        {saving ? ' · saving…' : ' · next cycle'}
      </span>
      {error && <span style={{ color: 'var(--w-blocked-300)' }}>{error}</span>}
    </div>
  );
}

/**
 * What the button will do, and — for the last project standing — why it will not. A control that
 * silently ignores a click is a control the owner assumes is broken.
 */
function dutyHint(p: ProjectInfo, on: boolean, selectedCount: number): string {
  if (!on) return `${p.displayName}: off duty. Click to let the heartbeat claim its tickets.`;
  if (selectedCount <= 1) return `${p.displayName} is the only project on duty. Use Stop to pause the heartbeat entirely.`;
  return `${p.displayName}: on duty. Click to stand it down - the heartbeat stops claiming its tickets from the next cycle.`;
}
