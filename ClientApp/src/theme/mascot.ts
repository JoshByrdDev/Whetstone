import type { ReactNode } from 'react';
import type { DashboardState } from '../types/events';
import { deepen, lighten } from './mix';

/**
 * The mascot slot: the one piece of the app whose *drawing* a theme can replace, not just recolour.
 *
 * Everything else a theme owns is a value — a colour, a face, a clip path — because everything else
 * is a rectangle that can be repainted. The mascot is not: it is a little creature, and a creature
 * is geometry. A robot recoloured brown is still a robot, so a theme that could only change tokens
 * would always be the sci-fi board wearing a costume.
 *
 * So a theme may hand over its own artwork. It is a slot with a default rather than a required
 * field: leave it unset and you get the built-in bot, which is entirely token-driven and already
 * wears whatever palette the theme sets. Fill it in when the creature itself is wrong for the look.
 *
 * What the slot does *not* include is deliberate. The wrapper in `AgentMascot.tsx` keeps the box,
 * the agent numeral, and the rule about when a mascot wears its agent's identity hue. Those are the
 * mascot doing its job — saying which agent this is and what it is doing — and a theme that could
 * drop them would be a re-skin that removed information.
 */

/** What the fleet is doing. The mascot's entire job is to make these six distinguishable. */
export type Mood = DashboardState['status'];

/**
 * One agent's colours, derived from its slot hue.
 *
 * A theme sets `color.agents` and these fall out of it, so a mascot never picks a shade — it asks
 * for the role it wants and gets a colour that is in step with the other two agents whatever the
 * theme did to them.
 */
export interface MascotPalette {
  /** The signature colour at full strength. Numerals, beams, anything that has to read at 12px. */
  hex: string;
  /** A highlight: an antenna bulb, a lit visor, a plume. */
  soft: string;
  /** An outline: the hue taken toward the floor. */
  edge: string;
  /** Dark enough to read as an eye against `body`. */
  eye: string;
  /** A graded fill for the creature's shell. */
  body: string;
  /** The hue in channels — `var(--w-agent-1-rgb)` — for keyframes that mix their own alpha. */
  rgb: string;
}

/**
 * The palette for agent slot `n` (1-based), at the ratios the original bot's shades were sitting at.
 *
 * The ratios are the part worth keeping: an edge at 48% of the hue and an eye at 23% read as an
 * edge and an eye whether the hue is amber or oxblood, which a pair of hand-picked hexes would not.
 */
export const agentPalette = (n: number): MascotPalette => {
  const hex = `var(--w-agent-${n})`;
  return {
    hex,
    soft: lighten(hex, 62),
    edge: deepen(hex, 48),
    eye: deepen(hex, 23),
    body: `radial-gradient(120% 120% at 50% 22%,${hex},${deepen(hex, 55)})`,
    rgb: `var(--w-agent-${n}-rgb)`,
  };
};

export interface MascotProps {
  /** What to show. All six must be tellable apart at a glance — this is a status readout. */
  mood: Mood;
  /**
   * Draw to exactly this square, in px.
   *
   * It is called at 12, 18, 20 and 40. Twelve is the hard case: at that size the mascot is a
   * coloured blob and nothing else survives, so put the mood in the silhouette and the fill rather
   * than in a detail that vanishes. Scale every dimension off `size`; nothing may be a fixed px.
   */
  size: number;
  /**
   * The agent's colours, when this mascot is standing for a particular agent *and* that agent is
   * working. Null the rest of the time — a resting agent has no ticket, so what it is doing matters
   * more than which one it is, and tinting every mood would turn six status colours into eighteen.
   *
   * With a palette, wear it. Without one, use the tier tokens: `var(--w-active)` for working,
   * `var(--w-gate)` for waiting, `var(--w-blocked)` for trouble, `var(--w-backlog)` for at rest.
   */
  palette: MascotPalette | null;
}

/**
 * A theme's mascot.
 *
 * Return artwork that fills a `size` square and positions itself absolutely inside the wrapper's
 * relative box. Overflowing is fine and expected — the bot's antenna and its "!" both hang outside
 * the square. Dim yourself if a mood calls for it; the agent numeral sits outside your artwork and
 * stays legible either way.
 */
export type Mascot = (props: MascotProps) => ReactNode;
