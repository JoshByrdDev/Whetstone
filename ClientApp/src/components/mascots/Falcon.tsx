import { useId } from 'react';
import type { CSSProperties } from 'react';
// Imported from the leaf modules rather than from `../../theme`, and that is load-bearing: the
// mountain theme names this file, the registry names the theme, and the barrel names the registry.
// Going through the barrel here would close that ring. `mascot` and `mix` sit below the registry
// and import nothing that leads back up.
import type { Mascot, MascotProps, Mood } from '../../theme/mascot';
import { deepen, lighten } from '../../theme/mix';

/**
 * The falcon: a bird of prey, either hunting or sitting.
 *
 * Beach Mountain's mascot, and the third answer to the question the slot exists for — a robot
 * recoloured turquoise is still a robot, and there are no robots on a hillside. Same job as the bot
 * and the familiar, same six moods, same box.
 *
 * The idea it is built on is neither of the other two. A bot is a lit shell with dark eyes cut into
 * it and a familiar is a dark hood with lit eyes looking out; a bird has no face worth drawing at
 * this size at all. What it has is a hunched raptor's *outline* — heavy shoulders, a hooked beak, a
 * tail down and back — and that outline is the same in all six moods, exactly as the bot's shell is.
 *
 * It was two outlines once: wings out and beating for the three moods where something is happening,
 * wings in and perched for the three where nothing is. The split read well written down and badly
 * on the board. A bird seen head-on with its wings swept forward is an arrowhead at any size, so the
 * one mascot you actually watch — the agent hovering over the row it is working — was a paper plane,
 * while the berths in the header, whose agents are usually at rest, were the bird. Two drawings for
 * one creature is one too many when they are eighteen inches apart on the same screen, and the one
 * that had to go is the one that stopped looking like a falcon.
 *
 * So mood is carried the way every other mascot in the app carries it: hue, light, motion, and the
 * small marks in the corner. Turquoise and lit and bobbing is working; grey and still is standing
 * by; deepened with a z over it is asleep. The eye opens and shuts with whether anyone is home.
 *
 * It was a swift first, drawn as clip-path polygons because that is what the other mascots are, and
 * the polygons are why it stopped being a bird. A clip path is one closed outline with no holes in
 * it, so the two wings and the body had to be three spikes off a shared base — and three spikes off
 * a shared base, at twelve pixels, is not a silhouette. It is the letter w. Drawn as SVG the wings
 * can join the body properly, and once they can, the bird is allowed to be a particular one: swept
 * back to points in the air rather than held square like a gull's, and perched, carrying the hooked
 * beak and the heavy shoulders that are the two things that say raptor at any size at all.
 *
 * Same contract as `Bot.tsx` and `Familiar.tsx`, and read one of those first if you are writing a
 * sixth: every dimension is a fraction of `size` — here by way of a 100-unit viewBox, which is the
 * same promise made once instead of at twenty separate measurements — and every colour is a token
 * or derived from one.
 */

/** Perched, side-on and hunched: tail down and back, then the body, then the head over it. */
const TAIL = 'M34 68 20 94 48 80z';
const BODY = 'M44 30c14 0 24 13 24 30 0 15-9 27-22 27-14 0-24-11-24-26 0-18 9-31 22-31z';
/** The hooked beak, and most of what makes a perched bird read as a raptor rather than a pigeon. */
const BEAK = 'M56 17 76 23 57 31 64 23z';
/** The folded wing, laid over the body's near side. A shade of the pelt, never a second colour. */
const WING = 'M55 42c9 7 12 19 10 30-4 6-10 9-16 10 8-10 11-28 6-40z';

interface MoodSpec {
  /** The one colour everything else is derived from. */
  hue: string;
  /** Whether the bird throws light. Only the two moods that are *doing* something do. */
  glow?: boolean;
  anim?: string;
  zzz?: boolean;
  moon?: boolean;
  bang?: boolean;
  dim?: boolean;
  /** The eye. Closed when it is not looking at anything. */
  eye: 'open' | 'shut';
}

const MOODS: Record<Mood, MoodSpec> = {
  /*
   * Working: the accent at full strength, lit, and lifting on the app's own bob.
   *
   * `anim-agentbob` rather than the `anim-agentglow` every other mascot's working mood uses, and
   * the difference is the halo. `agent-glow` animates a `box-shadow`, which is light cast by the
   * element's *box* — right for the bot, whose box is its body, and wrong here, where the box is a
   * transparent square with a bird inside it. It drew a glowing rectangle around the falcon. The
   * light this bird throws is the `drop-shadow` below instead, which follows the wing.
   */
  running:     { hue: 'var(--w-active)', glow: true, anim: 'anim-agentbob', eye: 'open' },
  // Trouble: the same bird, rocking. Alarm is in the motion and the mark, not in a face.
  error:       { hue: 'var(--w-blocked)', glow: true, anim: 'anim-agentalert', bang: true, eye: 'open' },
  // Off-hours is a gate in disguise — waiting on the clock rather than on you. Down, but awake.
  'off-hours': { hue: 'var(--w-gate)', moon: true, eye: 'open' },
  // Asleep: breathing, eyes shut. Recognisably the bird that was working.
  sleeping:    { hue: deepen('var(--w-active)', 68), anim: 'anim-agentbreathe', zzz: true, eye: 'shut' },
  idle:        { hue: 'var(--w-backlog)', eye: 'open' },
  stopped:     { hue: deepen('var(--w-backlog)', 45), dim: true, eye: 'shut' },
};

export const Falcon: Mascot = ({ mood, size: s, palette }: MascotProps) => {
  const m = MOODS[mood];
  // A palette only arrives when this bird stands for an agent that is actually working, so there is
  // no second rule here about when to wear it — see MascotProps.
  const hue = palette?.hex ?? m.hue;

  /*
   * A gradient is referenced by id, and three agents' worth of these are on screen at once — so the
   * id has to be per-instance or two birds end up wearing one bird's colour. `useId` is
   * per-instance and stable across renders, which is what a definition being pointed at needs. The
   * colons React puts in it are legal in an id and are not legal in the `url(#...)` that points at
   * one, so they come out.
   */
  const gid = 'w-falcon-' + useId().replace(/:/g, '');
  const fill = `url(#${gid})`;

  // Lit along the leading edge and dropping toward the floor behind it, which is what stops a
  // filled silhouette reading as a sticker. Derived rather than picked, so the same bird can wear
  // an agent's identity hue without anyone repicking a pair of blues.
  const lit = lighten(hue, 82);
  const floor = deepen(hue, 66);

  const glow: CSSProperties =
    // Below about 16px the blur is wider than the wing it is around, so it stops being light and
    // starts being a smudge.
    m.glow && s >= 16 ? { filter: `drop-shadow(0 0 ${s * 0.14}px ${hue})` } : {};

  return (
    <div
      className={m.anim ?? ''}
      style={{
        position: 'relative', width: s, height: s,
        opacity: m.dim ? 0.7 : 1,
        // The glow keyframes read this; without it they stay on the accent for every agent.
        ...(palette ? ({ ['--agent-rgb']: palette.rgb } as CSSProperties) : {}),
      }}
    >
      <svg
        width={s} height={s} viewBox="0 0 100 100" aria-hidden
        // Overflow is expected rather than tolerated: a wingtip and a tail both reach the edge of
        // the box, and clipping either is how a bird loses the part that made it one.
        style={{ position: 'absolute', inset: 0, overflow: 'visible', ...glow }}
      >
        <defs>
          {/* Across the frame from the upper left: the light is coming from where the sky is,
              which on a hillside at midday is the whole of it. */}
          <linearGradient id={gid} x1=".15" y1="0" x2=".9" y2="1">
            <stop offset="0" style={{ stopColor: lit }} />
            <stop offset=".55" style={{ stopColor: hue }} />
            <stop offset="1" style={{ stopColor: floor }} />
          </linearGradient>
        </defs>

        <path d={TAIL} fill={fill} />
        <path d={BODY} fill={fill} />
        <circle cx="42" cy="24" r="16" fill={fill} />
        <path d={BEAK} fill={lighten(hue, 88)} />
        <path d={WING} fill={floor} opacity=".5" />
        {/* The eye is in viewBox units, so it scales with everything else — and three and a half
            of a hundred is, at 12px, a third of a pixel of anti-aliasing and not much more. That
            is the right trade: an eye says which way the bird is facing, and at 12px the beak is
            already saying it louder. */}
        {m.eye === 'shut'
          ? <rect x="43" y="20" width="9" height="3" rx="1.5" fill="var(--w-void)" />
          : <circle cx="47" cy="21" r="3.6" fill="var(--w-void)" />}
      </svg>

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
