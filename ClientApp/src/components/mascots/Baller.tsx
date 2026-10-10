import { useId } from 'react';
import type { CSSProperties } from 'react';
// Imported from the leaf modules rather than from `../../theme`, and that is load-bearing: the
// basketball theme names this file, the registry names the theme, and the barrel names the
// registry. Going through the barrel here would close that ring. `mascot` and `mix` sit below the
// registry and import nothing that leads back up.
import type { Mascot, MascotProps, Mood } from '../../theme/mascot';
import { deepen, lighten } from '../../theme/mix';

/**
 * The baller: a player and a ball, and the ball is the part that talks.
 *
 * Basketball's mascot, and the fifth answer to the question the slot exists for. The mockup's
 * berths draw a figure rather than a creature — head, jersey, legs, leaning — so this is a person,
 * which none of the other four are.
 *
 * The idea it is built on is the one none of them could use. A bot is a lit shell with dark eyes
 * cut into it, a familiar is a dark hood with lit eyes looking out, a falcon has a posture and no
 * face, a bear has ears that flatten. All four put the readout *in the body*. A player's body is
 * the same body whatever is happening — what changes is where the ball is: over the head on a shot,
 * on the hip while waiting, on the floor when nobody is playing. That is a change of eight pixels
 * in a twelve pixel box, which is the largest signal any of these five mascots has, and it survives
 * the 12px a board row draws this at better than a limb ever would.
 *
 * The first cut of this was two rounded boxes — a circle for a head and one tapered shape for
 * everything below it — and what that draws is the generic account avatar every website has in its
 * top right corner. A person is not a torso; a person is a head, shoulders, two arms and two legs,
 * and the reason to draw them separately is that the gaps between them are what say *person*.
 * Drawn as SVG they can be separate: the shooting arm goes up under the ball, the off arm balances,
 * and the legs split, so the figure reads as somebody mid-shot rather than as a bust.
 *
 * The ball keeps the court's own orange in every mood, including the moods where the player is red
 * or grey and including the moods where the player is wearing an agent's identity hue. That is on
 * purpose and it is the only place in this file a colour is not derived from `hue`: the player is
 * whose turn it is and what is happening to them, and the ball is the same ball all evening.
 *
 * Same contract as `Bot.tsx`, `Familiar.tsx`, `Falcon.tsx` and `Bruin.tsx`, and read one of those
 * first if you are writing a sixth: every dimension is a fraction of `size` — here by way of a
 * 100-unit viewBox — every colour is a token or derived from one, and the six moods have to be
 * tellable apart at a glance.
 */

/** Where the ball is, which is the whole readout. */
type Ball = 'up' | 'hip' | 'floor';

interface MoodSpec {
  /** The one colour the player is derived from. Never the ball — see above. */
  hue: string;
  ball: Ball;
  /** Whether the player throws light. Only the two moods that are *doing* something do. */
  glow?: boolean;
  /** The wrapper's animation — the bob and the glow the whole app shares. */
  anim?: string;
  /** The player's own lean, which is this theme's keyframe and not the wrapper's. */
  lean?: boolean;
  /** Knees bent and shoulders down: the two moods where nobody is standing up. */
  crouch?: boolean;
  zzz?: boolean;
  moon?: boolean;
  bang?: boolean;
  dim?: boolean;
}

const MOODS: Record<Mood, MoodSpec> = {
  // Working: the accent at full strength, ball over the head mid-shot, leaning, and the only mood
  // that lights.
  running:     { hue: 'var(--w-active)', ball: 'up', glow: true, anim: 'anim-agentglow', lean: true },
  // Trouble: the same shot, still up, still lit — and shaking. Alarm is in the motion, not in a
  // second pose, because a pose that only appears when something breaks is a pose nobody learns.
  error:       { hue: 'var(--w-blocked)', ball: 'up', glow: true, anim: 'anim-agentalert', bang: true },
  // Off-hours is a gate in disguise — waiting on the clock rather than on you. On their feet,
  // holding the ball, not shooting it.
  'off-hours': { hue: 'var(--w-gate)', ball: 'hip', moon: true },
  // Asleep: down on the floor with the ball beside them, breathing. Recognisably the player that
  // was shooting.
  sleeping:    { hue: deepen('var(--w-active)', 68), ball: 'floor', crouch: true, anim: 'anim-agentbreathe', zzz: true },
  idle:        { hue: 'var(--w-backlog)', ball: 'hip' },
  stopped:     { hue: deepen('var(--w-backlog)', 45), ball: 'floor', crouch: true, dim: true },
};

/**
 * The figure, standing: shoulders, a jersey cut off at the shorts, and two legs with daylight
 * between them.
 *
 * One path rather than four, because the outline is continuous — a body is not assembled from
 * parts, and four shapes butted together show four seams the moment a gradient crosses them. The
 * notch up the middle at the bottom is the only gap in it, and it is the whole difference between
 * a person and a bust.
 */
const STAND =
  'M40 32c11 0 18 7 20 17l3 16-8 2-3-11-1 18 4 26h-9l-5-21-5 21h-9l3-26-1-18-3 11-8-2 3-16' +
  'c2-10 9-17 19-17z';
/**
 * The same figure sitting on the floor: head down, shoulders down, knees up in front of the chest.
 *
 * Two thirds the height rather than a bent joint, for the reason the stance is one path — at 12px a
 * knee is one pixel of nothing, and a figure that is visibly *short* is unmistakable where a figure
 * that is visibly *bent* is a smudge. The knees are the two bumps at the front; they are only
 * legible from about 22px, and they are not what the pose is resting on.
 */
const CROUCH =
  'M40 52c12 0 20 7 22 16l2 9-8 2-3-8v9l7 20H20l7-20v-9l-3 8-8-2 2-9c2-9 10-16 22-16z';
/** The shooting arm, up and out from the shoulder, with the hand under where the ball is. */
const ARM_UP = 'M55 40 71 25l8 8-16 15z';

/** The head, as a circle: centre and radius per pose, because a crouch drops it. */
const HEAD = { cx: 40, r: 12.5 };

export const Baller: Mascot = ({ mood, size: s, palette }: MascotProps) => {
  const m = MOODS[mood];
  // A palette only arrives when this player stands for an agent that is actually working, so there
  // is no second rule here about when to wear it — see MascotProps.
  const hue = palette?.hex ?? m.hue;

  // A gradient is referenced by id, and three agents' worth of these are on screen at once — so the
  // id has to be per-instance or two players end up wearing one player's kit. See `Falcon.tsx`.
  const gid = 'w-baller-' + useId().replace(/:/g, '');
  const kit = `url(#${gid})`;

  // Lit over the shoulder and dropping toward the floor at the hem, which is what stops a filled
  // silhouette reading as a sticker. Derived rather than picked, so the same figure can wear an
  // agent's identity hue without anyone repicking a pair of oranges.
  const lit = lighten(hue, 76);
  const floor = deepen(hue, 60);

  // Three positions and one size. `up` is over the shooting hand, which is where a shot leaves
  // from; `hip` and `floor` are out to the side, which is where a ball is when it is being held or
  // ignored. All in viewBox units, so a 12px row and a 40px berth get the same player.
  const ball =
    m.ball === 'up' ? { cx: 80, cy: 18 }
      : m.ball === 'hip' ? { cx: 74, cy: 56 }
        : { cx: 79, cy: 85 };

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
        style={{ position: 'absolute', inset: 0, overflow: 'visible' }}
      >
        <defs>
          <linearGradient id={gid} x1=".2" y1="0" x2=".85" y2="1">
            <stop offset="0" style={{ stopColor: lit }} />
            <stop offset=".55" style={{ stopColor: hue }} />
            <stop offset="1" style={{ stopColor: floor }} />
          </linearGradient>
          {/* The ball is lit from over the shooter's left, like everything else on this court. */}
          <radialGradient id={`${gid}-b`} cx=".34" cy=".26" r=".9">
            <stop offset="0" style={{ stopColor: 'var(--w-accent-lit)' }} />
            <stop offset=".46" style={{ stopColor: 'var(--w-accent)' }} />
            <stop offset="1" style={{ stopColor: 'var(--w-accent-deep)' }} />
          </radialGradient>
        </defs>

        {/* Head, body and shooting arm lean together, so the shoulders stay under the head.
            `bb-lean` is the basketball theme's own keyframe, injected with the theme — which is safe
            because this player is only ever drawn under it. `fill-box` is what makes the origin
            mean the player's feet rather than the corner of the viewport. */}
        <g
          style={{
            transformBox: 'fill-box', transformOrigin: '50% 92%',
            ...(m.lean ? { animation: 'bb-lean 3.4s ease-in-out infinite' } : {}),
            // Below about 16px the blur is wider than the player it is around, so it stops being
            // light and starts being a smudge.
            ...(m.glow && s >= 16 ? { filter: `drop-shadow(0 0 ${s * 0.14}px ${hue})` } : {}),
          }}
        >
          <circle cx={HEAD.cx} cy={m.crouch ? 39 : 17} r={HEAD.r} fill={kit} />
          <path d={m.crouch ? CROUCH : STAND} fill={kit} />
          {/* The arm is only drawn for the shot. A player holding the ball on their hip has it
              tucked against the body, and an arm drawn out to meet it there is an arm pointing at
              nothing. */}
          {m.ball === 'up' && <path d={ARM_UP} fill={kit} />}
        </g>

        {/* The ball sits outside the lean, and deliberately: a ball that rocked with the shoulders
            would be glued to the player, and a ball that holds still while they move is a ball that
            has left their hands. */}
        <circle
          cx={ball.cx} cy={ball.cy} r="15" fill={`url(#${gid}-b)`}
          style={m.glow && s >= 16 ? { filter: `drop-shadow(0 0 ${s * 0.1}px var(--w-accent))` } : undefined}
        />
        {/* The seam. Drawn only where there is room for it — under about 20px it is a dark pixel in
            the middle of a ball, which is worse than no seam at all. */}
        {s >= 20 && (
          <path
            d={`M${ball.cx - 15} ${ball.cy}h30M${ball.cx} ${ball.cy - 15}v30`}
            stroke="var(--w-accent-deep)" strokeWidth="2" opacity=".7" fill="none"
          />
        )}
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
