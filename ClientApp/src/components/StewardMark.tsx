import { CAPS, DISPLAY, chamfer } from './orbit';
import { track } from '../theme';

/**
 * How a steward is marked wherever it shows up beside the agents.
 *
 * An agent is a numbered bot in its slot's colour, because there are three of them and the number
 * is what tells them apart. A steward is not a fourth: it works no ticket and stands in for the
 * owner, so it is named in a word, and the word wears the accent - the colour the app already
 * keeps for what is the owner's. Anything that would otherwise draw it as "agent 4" draws this.
 */

/**
 * The steward's rail and mark in a feed. Off the text ramp, not a hue: the agents' colours are a
 * theme's to pick, and any hue chosen here is one a theme can hand to an agent - the accent itself
 * sits a shade from one of them. No colour at all is the one answer no agent can share.
 */
export const STEWARD_HUE = 'var(--w-t300)';

/** What a row of the steward's fills itself with, so its stretch of a feed reads as a block. */
export const STEWARD_WASH = 'rgb(var(--w-line-rgb) / .07)';

export function StewardTag({ className = '' }: { className?: string }) {
  return (
    <span
      className={`inline-flex items-center flex-shrink-0 ${className}`}
      style={{
        clipPath: chamfer(4),
        padding: '2px 7px 1px',
        font: `700 8.5px ${DISPLAY}`, letterSpacing: track(0.24), textTransform: CAPS, lineHeight: 1.3,
        color: 'var(--w-accent-text)',
        background: 'rgb(var(--w-accent-rgb) / .14)',
        border: '1px solid rgb(var(--w-accent-rgb) / .45)',
      }}
    >
      Steward
    </span>
  );
}
