import type { CSSProperties, ReactNode } from 'react';
import { chamfer, fade, CAPS, DISPLAY, MONO, BODY } from './orbit';
import { track } from '../theme';

/**
 * The pieces every tab is built from.
 *
 * The board was redesigned first and the rest of the app followed it, so these are the board's own
 * parts pulled out rather than a new vocabulary invented alongside it: the same chamfer, the same
 * display type, the same accent on the same void. A page that reaches for anything else here is a
 * page that will drift, which is exactly what having one file to change is meant to prevent.
 *
 * Nothing in here names a colour any more. Every value is a theme token, so these parts are also
 * the reason a re-skin does not have to touch a single page: change what `--w-accent` is and the
 * primary button, the live chip, the section rule and the title's tick marks all follow at once.
 *
 * Everything is transparent by default. There is one backdrop behind the whole app, and a surface
 * that paints itself opaque punches a hole in it.
 */

/** The one accent. Primary actions, the live tab, anything the eye should land on first. */
export const AMBER = 'var(--w-accent)';
/** Text ramp, coolest to brightest. */
export const DIM = 'var(--w-t600)';
export const MUTED = 'var(--w-t400)';
export const BRIGHT = 'var(--w-t100)';


/**
 * A page's title block: eyebrow, name, and the three tick marks that say the name is a readout.
 *
 * Every tab wears one, so switching tabs changes the word without moving the furniture — the title
 * lands in the same place at the same size whichever tab you came from.
 */
export function PageHead({ eyebrow, title, sub, left, right }: {
  eyebrow?: string;
  title: string;
  /** One line under the title. Counts and state, not description. */
  sub?: ReactNode;
  /** Controls that belong to the title — filters, pickers. Sits beside it on the baseline. */
  left?: ReactNode;
  /** Controls that act on the page. Pushed to the far edge. */
  right?: ReactNode;
}) {
  return (
    <div className="relative flex flex-wrap items-end justify-between gap-6" style={{ paddingBottom: 14 }}>
      <div className="flex items-end gap-6 flex-wrap">
        <div>
          <div style={{ font: `600 9px ${DISPLAY}`, letterSpacing: track(0.45), color: 'var(--w-t500)', textTransform: CAPS }}>
            {eyebrow ?? 'Whetstone'}
          </div>
          <div className="flex items-baseline gap-3.5" style={{ marginTop: 1 }}>
            <div
              style={{
                font: `700 36px/1 ${DISPLAY}`, letterSpacing: track(0.15), color: 'var(--w-t100)',
                textTransform: CAPS, textShadow: '0 0 44px rgb(var(--w-line-rgb) / .35)',
              }}
            >
              {title}
            </div>
            <div className="flex items-center gap-[5px]">
              <span style={{ width: 2, height: 14, background: AMBER }} />
              <span style={{ width: 2, height: 9, background: 'rgb(var(--w-accent-rgb) / .5)' }} />
              <span style={{ width: 2, height: 5, background: 'rgb(var(--w-accent-rgb) / .28)' }} />
            </div>
          </div>
          {sub && <div style={{ font: `400 10.5px ${BODY}`, color: DIM, marginTop: 5 }}>{sub}</div>}
        </div>
        {left}
      </div>
      {right && <div className="flex items-center gap-3 flex-wrap" style={{ paddingBottom: 4 }}>{right}</div>}
    </div>
  );
}

/**
 * A selector that is either on or off. Project filters, day windows, view switches.
 *
 * On is the accent; off is a hairline. The pair has to survive being read at a glance from across
 * the room, which is why the difference is hue and not weight.
 */
export function Chip({ on, onClick, title, disabled, children }: {
  on: boolean; onClick: () => void; title?: string; disabled?: boolean; children: ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      title={title}
      disabled={disabled}
      className="orbit-btn"
      style={{
        clipPath: chamfer(9),
        // Resting colours as custom properties, never as `background`/`border` — see .orbit-btn.
        ['--rbg' as string]: on ? 'rgb(var(--w-accent-rgb) / .16)' : 'rgb(var(--w-line-rgb) / .04)',
        ['--rbw' as string]: `1px solid ${on ? 'rgb(var(--w-accent-rgb) / .45)' : 'rgb(var(--w-line-rgb) / .14)'}`,
        padding: '5px 14px',
        font: `600 10px ${DISPLAY}`, letterSpacing: track(0.2),
        color: on ? 'var(--w-accent-text)' : MUTED, textTransform: CAPS,
        opacity: disabled ? 0.4 : 1,
        ['--hbg' as string]: on ? 'rgb(var(--w-accent-rgb) / .24)' : 'rgb(var(--w-line-rgb) / .12)',
        ['--htxt' as string]: on ? 'var(--w-accent-text-lit)' : BRIGHT,
        ['--hbc' as string]: on ? 'rgb(var(--w-accent-rgb) / .6)' : 'rgb(var(--w-line-rgb) / .3)',
        ['--hsh' as string]: 'none',
      } as CSSProperties}
    >
      {children}
    </button>
  );
}

/**
 * The page's one primary action, wearing the accent and a sheen that crosses it every few seconds.
 *
 * Deliberately singular: a page with two of these has two primary actions, which means it has none.
 * Everything else on the page is a {@link GhostButton}.
 */
export function PrimaryButton({ onClick, disabled, title, children }: {
  onClick: () => void; disabled?: boolean; title?: string; children: ReactNode;
}) {
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="orbit-primary relative overflow-hidden flex-none"
      style={{
        clipPath: chamfer(11),
        background: disabled
          ? 'rgb(var(--w-line-rgb) / .12)'
          : 'linear-gradient(120deg,var(--w-accent-lit),var(--w-accent-deep))',
        padding: '9px 20px',
        font: `700 11px ${DISPLAY}`, letterSpacing: track(0.22),
        color: disabled ? 'var(--w-t500)' : 'var(--w-on-accent)', textTransform: CAPS,
        cursor: disabled ? 'not-allowed' : 'pointer',
      }}
    >
      {children}
      {!disabled && (
        <span
          className="anim-glint"
          style={{
            position: 'absolute', top: 0, left: 0, width: '34%', height: '100%', pointerEvents: 'none',
            // Left as light rather than as a token: a glint is a highlight on whatever the fill
            // happens to be, and a themed one would just be a second, dimmer accent.
            background: 'linear-gradient(90deg,transparent,rgba(255,255,255,.55),transparent)',
          }}
        />
      )}
    </button>
  );
}

/** Everything that is not the primary action: glass, hairline, lifts a pixel under the pointer. */
export function GhostButton({ onClick, disabled, title, tone = 'plain', children }: {
  onClick: () => void; disabled?: boolean; title?: string;
  /** `danger` for destructive moves, `go` for a confirming one. Colour only — the shape never changes. */
  tone?: 'plain' | 'danger' | 'go';
  children: ReactNode;
}) {
  // The channels, not the colour: this button mixes six different alphas out of one hue, and an
  // alpha cannot be appended to a `var()` that has already resolved to a whole colour.
  const c = tone === 'danger' ? 'var(--w-blocked-rgb)' : tone === 'go' ? 'var(--w-done-rgb)' : 'var(--w-line-rgb)';
  return (
    <button
      onClick={onClick}
      disabled={disabled}
      title={title}
      className="orbit-btn"
      style={{
        clipPath: chamfer(9),
        ['--rbg' as string]: `rgb(${c} / .08)`,
        ['--rbw' as string]: `1px solid rgb(${c} / .26)`,
        padding: '6px 14px',
        font: `600 10px ${DISPLAY}`, letterSpacing: track(0.18), textTransform: CAPS,
        color: tone === 'plain' ? MUTED : `rgb(${c})`,
        opacity: disabled ? 0.38 : 1,
        cursor: disabled ? 'not-allowed' : 'pointer',
        ['--hbg' as string]: `rgb(${c} / .18)`,
        ['--htxt' as string]: tone === 'plain' ? BRIGHT : `rgb(${c})`,
        ['--hbc' as string]: `rgb(${c} / .5)`,
        ['--hsh' as string]: 'none',
      } as CSSProperties}
    >
      {children}
    </button>
  );
}

/**
 * A chamfered sheet of glass. The default surface for anything that groups content.
 *
 * `pad` off is for panels that hold their own scrollers or tables, which need to bleed to the edge.
 */
export function Panel({ children, className = '', pad = true, tone, style }: {
  children: ReactNode;
  className?: string;
  pad?: boolean;
  /** Accent colour for the border, when the panel means something — a warning, a live run. */
  tone?: string;
  style?: CSSProperties;
}) {
  return (
    <div
      className={className}
      style={{
        clipPath: chamfer(13),
        background: 'linear-gradient(120deg,var(--w-panel-lit),var(--w-panel-deep))',
        border: `1px solid ${tone ? fade(tone, 33) : 'rgb(var(--w-line-rgb) / .12)'}`,
        boxShadow: tone ? `0 0 26px -12px ${tone}` : undefined,
        padding: pad ? '14px 16px' : undefined,
        ...style,
      }}
    >
      {children}
    </div>
  );
}

/** A heading inside a page: lit bar, tracked name, and a rule that fades into the backdrop. */
export function SectionRule({ colour = AMBER, title, count, meta, right }: {
  colour?: string; title: string; count?: number; meta?: string; right?: ReactNode;
}) {
  return (
    <div className="relative flex items-center gap-3" style={{ padding: '4px 0 8px' }}>
      <span style={{ width: 3, height: 14, background: colour, boxShadow: `0 0 12px ${colour}` }} />
      <span style={{ font: `700 10.5px ${DISPLAY}`, letterSpacing: track(0.3), color: colour, textTransform: CAPS }}>
        {title}
      </span>
      {count != null && (
        <span style={{ font: `700 10px ${MONO}`, color: 'var(--w-void)', background: colour, padding: '1px 7px' }}>
          {String(count).padStart(2, '0')}
        </span>
      )}
      {meta && (
        <span style={{ font: `400 9.5px ${DISPLAY}`, letterSpacing: track(0.18), color: 'var(--w-t600)', textTransform: CAPS }}>
          {meta}
        </span>
      )}
      <span style={{ flex: 1, height: 1, background: `linear-gradient(90deg,${fade(colour, 44)},transparent)` }} />
      {right}
    </div>
  );
}
