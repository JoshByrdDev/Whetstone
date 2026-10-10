import type { CSSProperties } from 'react';
import type { Mascot, MascotProps, Mood } from '../../theme';
import { deepen, lighten } from '../../theme';

/**
 * The bot: a rounded shell with two eyes, an antenna, and a mood.
 *
 * The app's default mascot, used by any theme that does not fill the mascot slot. It lives here
 * rather than inside the space theme on purpose — it is entirely token-driven, so it wears whatever
 * palette the active theme sets, and a theme only needs its own creature when a recoloured robot is
 * the wrong *shape* for the look rather than the wrong colour.
 *
 * Read this as the worked example of the contract in `theme/mascot.ts`: every dimension is a
 * fraction of `size`, every colour is a token or something derived from one, and the six moods are
 * told apart by fill and silhouette rather than by detail — which is what keeps it legible at the
 * 12px it is drawn at inside a board row.
 */

type EyeStyle = 'open' | 'closed' | 'dot' | 'x' | 'worried';

interface MoodSpec {
  body: string;
  border: string;
  eye: string;
  eyes: EyeStyle;
  anim?: string;
  antenna?: string;
  zzz?: boolean;
  moon?: boolean;
  bang?: boolean;
  dim?: boolean;
}

/**
 * A whole bot built out of one colour.
 *
 * Every mood used to be five hand-picked hexes — a fill, the darker fill it graded into, a darker
 * border, a darker eye, a lighter antenna. Five values that only ever moved together, which is what
 * made the mascot the hardest thing in the app to re-skin: a new palette meant repicking thirty
 * shades by eye and hoping they stayed in step.
 *
 * So a mood names one token and derives the rest from it, at the ratios the original shades were
 * already sitting at. A theme changes the six tier colours and every bot restyles itself.
 */
const shell = (c: string) => ({
  body: `radial-gradient(120% 120% at 50% 22%,${c},${deepen(c, 55)})`,
  border: deepen(c, 48),
  eye: deepen(c, 23),
});

const MOODS: Record<Mood, MoodSpec> = {
  // Working: the accent, lit, with its antenna bulb up.
  running:     { ...shell('var(--w-active)'), eyes: 'open', anim: 'anim-agentglow', antenna: lighten('var(--w-active)', 62) },
  // Asleep: the same colour, banked down. Recognisably the same bot with the lights off.
  sleeping:    { ...shell(deepen('var(--w-active)', 72)), eyes: 'closed', anim: 'anim-agentbreathe', zzz: true },
  // Off-hours is a gate in disguise — the system is waiting on the clock rather than on you.
  'off-hours': { ...shell('var(--w-gate)'), eyes: 'closed', moon: true },
  idle:        { ...shell('var(--w-backlog)'), eyes: 'dot' },
  stopped:     { ...shell(deepen('var(--w-backlog)', 45)), eyes: 'x', dim: true },
  error:       { ...shell('var(--w-blocked)'), eyes: 'worried', anim: 'anim-agentalert', bang: true },
};

function Eyes({ style, color, s }: { style: EyeStyle; color: string; s: number }) {
  const dot = (extra: CSSProperties) => (
    <span style={{ position: 'absolute', background: color, ...extra }} />
  );
  const top = s * 0.4;
  const inset = s * 0.23;
  if (style === 'closed') {
    const st: CSSProperties = { top: s * 0.5, width: s * 0.18, height: Math.max(2, s * 0.05), borderRadius: 2, background: color };
    return <>{dot({ ...st, left: inset })}{dot({ ...st, right: inset })}</>;
  }
  if (style === 'dot') {
    const st: CSSProperties = { top: s * 0.4, width: s * 0.13, height: s * 0.13, borderRadius: '50%' };
    return <>{dot({ ...st, left: inset })}{dot({ ...st, right: inset })}</>;
  }
  if (style === 'x') {
    const bar = (side: 'left' | 'right', rot: number) =>
      dot({ top: s * 0.4, [side]: inset, width: s * 0.18, height: Math.max(2, s * 0.05), borderRadius: 2, transform: `rotate(${rot}deg)` });
    return <>{bar('left', 45)}{bar('left', -45)}{bar('right', 45)}{bar('right', -45)}</>;
  }
  // open / worried (worried = taller rounded rects)
  const h = style === 'worried' ? s * 0.26 : s * 0.21;
  const st: CSSProperties = { top, width: s * 0.13, height: h, borderRadius: s * 0.07, background: color };
  return <>{dot({ ...st, left: inset })}{dot({ ...st, right: inset })}</>;
}

export const Bot: Mascot = ({ mood, size: s, palette }: MascotProps) => {
  const m = MOODS[mood];
  // A palette only arrives when this bot is standing for an agent that is actually working, so
  // there is no second rule here about when to wear it — see MascotProps.
  const body = palette?.body ?? m.body;
  const border = palette?.edge ?? m.border;
  const eye = palette?.eye ?? m.eye;
  const antenna = m.antenna ? (palette?.soft ?? m.antenna) : undefined;

  return (
    <div
      className={m.anim ?? ''}
      style={{
        position: 'relative', width: s, height: s, borderRadius: s * 0.3,
        background: body, border: `1px solid ${border}`,
        opacity: m.dim ? 0.7 : 1,
        // The glow keyframes read this; without it they stay on the accent for every agent.
        ...(palette ? ({ ['--agent-rgb']: palette.rgb } as CSSProperties) : {}),
      }}
    >
      {antenna && (
        <>
          <span style={{ position: 'absolute', top: -s * 0.24, left: '50%', transform: 'translateX(-50%)', width: 2, height: s * 0.16, background: border }} />
          <span style={{ position: 'absolute', top: -s * 0.34, left: '50%', transform: 'translateX(-50%)', width: s * 0.16, height: s * 0.16, borderRadius: '50%', background: antenna, boxShadow: `0 0 ${s * 0.22}px ${antenna}` }} />
        </>
      )}
      <Eyes style={m.eyes} color={eye} s={s} />
      {m.zzz && (
        <span className="anim-agentzzz" style={{ position: 'absolute', top: -s * 0.05, right: -s * 0.12, font: `700 ${s * 0.28}px var(--w-font-body)`, color: 'var(--w-active-400)' }}>z</span>
      )}
      {m.moon && (
        <span style={{ position: 'absolute', top: -s * 0.1, right: -s * 0.08, fontSize: s * 0.3 }}>🌙</span>
      )}
      {m.bang && (
        <span style={{ position: 'absolute', top: -s * 0.22, right: -s * 0.14, font: `700 ${s * 0.32}px var(--w-font-body)`, color: 'var(--w-blocked-200)' }}>!</span>
      )}
    </div>
  );
};
