import { useEffect } from 'react';
import type { DashboardState } from '../types/events';
import { currentTheme, useTheme } from '../theme';

const SUFFIX = ' — Whetstone';

/** Short wall-clock time for the wake target, e.g. "1:23 PM". */
export function formatWakeTime(when: Date): string {
  return when.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
}

/**
 * The dot in the tab, per status.
 *
 * Read off the theme object rather than through a CSS variable, and this is the one place in the
 * app where that distinction matters: the favicon is a data URI the browser loads as its own
 * document, with no access to the page's custom properties. A `var()` in here resolves to nothing
 * and paints an invisible circle.
 */
const faviconColour = (status: string): string => {
  const c = currentTheme().color;
  switch (status) {
    case 'running': return c.tier.done;
    case 'sleeping': return c.tier.active;
    case 'error': return c.tier.blocked;
    case 'stopped': return c.tier.backlog;
    default: return c.text[500]; // idle, off-hours: present but not saying anything
  }
};

function buildFavicon(color: string): string {
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><circle cx="16" cy="16" r="14" fill="${color}"/></svg>`;
  return `data:image/svg+xml,${encodeURIComponent(svg)}`;
}

function getStatusText(state: DashboardState, wakeAt?: Date | null): string {
  if (state.status === 'running') {
    // A tab title has room for one thing. With several agents out, the count IS that thing -
    // naming one ticket while two others are also being worked is the more misleading answer.
    if (state.agents.length > 1) return `${state.agents.length} agents working`;
    if (state.ticketId && state.phase) return `${state.ticketId} ${state.phase}`;
    if (state.ticketId) return `${state.ticketId} Running`;
    return 'Running';
  }
  if (state.status === 'sleeping') return wakeAt ? `Sleeping until ${formatWakeTime(wakeAt)}` : 'Sleeping';
  if (state.status === 'off-hours') return 'Off-Hours';
  if (state.status === 'error') return 'Error';
  if (state.status === 'stopped') return 'Stopped';
  return 'Idle';
}

export function useTabStatus(state: DashboardState, wakeAt?: Date | null) {
  // Only so the favicon is redrawn when the theme changes; the title does not care.
  const theme = useTheme();
  useEffect(() => {
    document.title = getStatusText(state, wakeAt) + SUFFIX;

    const color = faviconColour(state.status);
    let link = document.querySelector<HTMLLinkElement>('link[rel="icon"]');
    if (!link) {
      link = document.createElement('link');
      link.rel = 'icon';
      document.head.appendChild(link);
    }
    link.href = buildFavicon(color);
  }, [state.status, state.ticketId, state.phase, state.agents.length, wakeAt?.getTime(), theme]);
}
