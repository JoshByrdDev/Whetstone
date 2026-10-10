import type { CSSProperties } from 'react';
// Imported from the leaf modules rather than from `../../theme`, and that is load-bearing: the
// medieval theme names this file, the registry names the theme, and the barrel names the registry.
// Going through the barrel here would close that ring. `mascot` and `mix` sit below the registry
// and import nothing that leads back up.
import type { Mascot, MascotProps, Mood } from '../../theme/mascot';
import { deepen } from '../../theme/mix';

/**
 * The familiar: a hooded figure with two lit eyes and nothing else.
 *
 * The medieval theme's mascot, and the reason the mascot slot exists — every other difference
 * between the two themes is a value, but a robot recoloured brown is still a robot, and a keep does
 * not have robots in it. Same job as the bot, same six moods, same box: what changed is the
 * creature, which is the one thing a token cannot change.
 *
 * The idea it is built on is the inverse of the bot's. A bot is a lit shell with dark eyes cut into
 * it; a familiar is a dark hood with lit eyes looking out of it. That inversion is what carries the
 * whole look — the theme's light comes from torches, so the thing that glows is the eyes, and the
 * body is the shadow they are glowing out of.
 *
 * Same contract as `Bot.tsx`, and read that one first if you are writing a third: every dimension
 * is a fraction of `size`, every colour is a token or derived from one, and the moods have to be
 * tellable apart at the 12px a board row draws this at.
 */

type EyeStyle = 'open' | 'closed' | 'dot' | 'x' | 'worried';

interface MoodSpec {
  cowl: string;
  border: string;
  eye: string;
  eyes: EyeStyle;
  /** Whether the eyes cast light. Only the two moods that are *doing* something do. */
  glow?: boolean;
  anim?: string;
  zzz?: boolean;
  moon?: boolean;
  bang?: boolean;
  dim?: boolean;
}

/**
 * A whole hood built out of one colour.
 *
 * The three shades are the hue taken down toward the floor by different amounts — the cloth catching
 * the light at the top, the fold it falls into, and the edge. Deriving them rather than picking them
 * is what lets the same figure wear an agent's identity hue without anyone repicking three browns.
 */
const cowl = (c: string) => ({
  cowl: `linear-gradient(165deg,${deepen(c, 38)},${deepen(c, 9)})`,
  border: deepen(c, 52),
});

const MOODS: Record<Mood, MoodSpec> = {
  // Working: the accent at full strength, and the only mood whose eyes throw light.
  running:     { ...cowl('var(--w-active)'), eye: 'var(--w-active)', eyes: 'open', glow: true, anim: 'anim-agentglow' },
  // Asleep: the same figure with the lamp out. Recognisably the one that was working.
  sleeping:    { ...cowl(deepen('var(--w-active)', 72)), eye: deepen('var(--w-active)', 46), eyes: 'closed', anim: 'anim-agentbreathe', zzz: true },
  // Off-hours is a gate in disguise — waiting on the clock rather than on you.
  'off-hours': { ...cowl('var(--w-gate)'), eye: deepen('var(--w-gate)', 62), eyes: 'closed', moon: true },
  idle:        { ...cowl('var(--w-backlog)'), eye: deepen('var(--w-backlog)', 62), eyes: 'dot' },
  stopped:     { ...cowl(deepen('var(--w-backlog)', 45)), eye: deepen('var(--w-backlog)', 40), eyes: 'x', dim: true },
  error:       { ...cowl('var(--w-blocked)'), eye: 'var(--w-blocked)', eyes: 'worried', glow: true, anim: 'anim-agentalert' },
};

function Eyes({ style, color, glow, s }: { style: EyeStyle; color: string; glow: boolean; s: number }) {
  const eye = (extra: CSSProperties) => <span style={{ position: 'absolute', background: color, ...extra }} />;
  const inset = s * 0.29;
  const top = s * 0.46;
  // Never below 1.6px: at the 12px a row draws this at, a proportional eye would round away to
  // nothing and the figure would lose the only feature it has.
  const d = Math.max(1.6, s * 0.11);
  const bar = Math.max(1.5, s * 0.045);

  if (style === 'closed') {
    const st: CSSProperties = { top: top + d * 0.4, width: s * 0.15, height: bar, borderRadius: 2 };
    return <>{eye({ ...st, left: inset })}{eye({ ...st, right: inset })}</>;
  }
  if (style === 'dot') {
    const st: CSSProperties = { top: top + d * 0.1, width: d * 0.8, height: d * 0.8, borderRadius: '50%' };
    return <>{eye({ ...st, left: inset })}{eye({ ...st, right: inset })}</>;
  }
  if (style === 'x') {
    const cross = (side: 'left' | 'right', rot: number) =>
      eye({ top: top + d * 0.35, [side]: inset - d * 0.2, width: d * 1.4, height: bar, borderRadius: 2, transform: `rotate(${rot}deg)` });
    return <>{cross('left', 45)}{cross('left', -45)}{cross('right', 45)}{cross('right', -45)}</>;
  }
  // open / worried — worried is the same eye stretched, which reads as alarm at any size.
  const st: CSSProperties = {
    top, width: d, height: style === 'worried' ? d * 1.45 : d, borderRadius: '50%',
    ...(glow ? { boxShadow: `0 0 ${s * 0.15}px ${color}` } : {}),
  };
  return <>{eye({ ...st, left: inset })}{eye({ ...st, right: inset })}</>;
}

export const Familiar: Mascot = ({ mood, size: s, palette }: MascotProps) => {
  const m = MOODS[mood];
  // A palette only arrives when this figure stands for an agent that is actually working, so there
  // is no second rule here about when to wear it — see MascotProps.
  const skin = palette ? cowl(palette.hex) : m;
  const eye = palette?.hex ?? m.eye;

  return (
    <div
      className={m.anim ?? ''}
      style={{
        position: 'relative', width: s, height: s,
        // Round over the crown, squarer at the shoulders: a cowl rather than a ball.
        borderRadius: '50% 50% 42% 42%/58% 58% 42% 42%',
        background: skin.cowl, border: `1px solid ${skin.border}`,
        opacity: m.dim ? 0.7 : 1,
        // The glow keyframes read this; without it they stay on the accent for every agent.
        ...(palette ? ({ ['--agent-rgb']: palette.rgb } as CSSProperties) : {}),
      }}
    >
      {/* The shadow under the hood. The eyes are drawn against this rather than against the cloth,
          which is what makes two dots read as a face instead of as two dots on a lump. */}
      <span
        style={{
          position: 'absolute', left: s * 0.175, top: s * 0.275, width: s * 0.65, height: s * 0.575,
          borderRadius: '50% 50% 40% 40%/60% 60% 40% 40%', background: 'var(--w-void)',
        }}
      />
      {/* Below about 16px the glow's blur radius is wider than the eye it is around, so it stops
          being light and starts being a smudge. */}
      <Eyes style={m.eyes} color={eye} glow={!!m.glow && s >= 16} s={s} />
      {m.zzz && (
        <span className="anim-agentzzz" style={{ position: 'absolute', top: -s * 0.05, right: -s * 0.12, font: `700 ${s * 0.28}px var(--w-font-body)`, color: 'var(--w-active-400)' }}>z</span>
      )}
      {m.moon && (
        <span style={{ position: 'absolute', top: -s * 0.12, right: -s * 0.1, fontSize: s * 0.3 }}>🌙</span>
      )}
      {m.bang && (
        <span style={{ position: 'absolute', top: -s * 0.24, right: -s * 0.14, font: `700 ${s * 0.32}px var(--w-font-body)`, color: 'var(--w-blocked-200)' }}>!</span>
      )}
    </div>
  );
};
