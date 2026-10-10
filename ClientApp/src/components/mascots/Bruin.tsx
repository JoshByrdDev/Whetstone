import { useId } from 'react';
import type { CSSProperties } from 'react';
// Imported from the leaf modules rather than from `../../theme`, and that is load-bearing: the snow
// theme names this file, the registry names the theme, and the barrel names the registry. Going
// through the barrel here would close that ring. `mascot` and `mix` sit below the registry and
// import nothing that leads back up.
import type { Mascot, MascotProps, Mood } from '../../theme/mascot';
import { deepen, lighten } from '../../theme/mix';

/**
 * The bruin: a polar bear, head and shoulders, ears up or ears flat.
 *
 * Snow's mascot, and the fourth answer to the question the slot exists for. Taken from the mockup's
 * berths, which draw one — a wide head, two round ears set out on the sides, two eyes and a muzzle,
 * nodding — rather than any of the three creatures already in the app.
 *
 * The idea it is built on is a third one. A bot is a lit shell with dark eyes cut into it, a
 * familiar is a dark hood with lit eyes looking out of it, and a falcon has no face at all — only a
 * posture. A bear has a face and *also* has ears, and the ears are the part that carries the
 * readout: up and round when something is happening, flattened back when nothing is. That is a
 * change to the outline rather than to a detail inside it, which is the only kind of change that
 * survives the 12px a board row draws this at.
 *
 * The first cut of this was six absolutely-positioned boxes with border-radii on them, and it read
 * as a soft toy: a circle for a head, a circle for a muzzle, two circles for ears. That is what
 * boxes can be. Drawn as SVG it can have the parts a bear actually has and a rounded rectangle
 * cannot — a brow over the eyes, a snout that comes forward off the face rather than sitting on it,
 * a black nose pad, shoulders under the jaw, and the pale mask around the muzzle that is the one
 * marking every polar bear photograph has in it.
 *
 * Same contract as `Bot.tsx`, `Familiar.tsx` and `Falcon.tsx`, and read one of those first if you
 * are writing a sixth: every dimension is a fraction of `size` — here by way of a 100-unit viewBox,
 * which is the same promise made once instead of at twenty separate measurements — every colour is
 * a token or derived from one, and the six moods have to be tellable apart at a glance.
 */

type EyeStyle = 'open' | 'shut' | 'wide';

interface MoodSpec {
  /** The one colour the whole animal is derived from. */
  hue: string;
  /** Ears up and round, or flattened back against the head. The mood, in the silhouette. */
  alert: boolean;
  eyes: EyeStyle;
  /** Whether the bear throws light. Only the two moods that are *doing* something do. */
  glow?: boolean;
  /** The wrapper's animation — the bob and the glow the whole app shares. */
  anim?: string;
  /** The head's own nod, which is this theme's keyframe and not the wrapper's. */
  nod?: boolean;
  zzz?: boolean;
  moon?: boolean;
  bang?: boolean;
  dim?: boolean;
}

const MOODS: Record<Mood, MoodSpec> = {
  // Working: the accent at full strength, ears up, head nodding, and the only mood that lights.
  running:     { hue: 'var(--w-active)', alert: true, eyes: 'open', glow: true, anim: 'anim-agentglow', nod: true },
  // Trouble: the same bear, ears up and eyes stretched. Alarm is in the shake, not in a new face.
  error:       { hue: 'var(--w-blocked)', alert: true, eyes: 'wide', glow: true, anim: 'anim-agentalert', bang: true },
  // Off-hours is a gate in disguise — waiting on the clock rather than on you. Awake, not working.
  'off-hours': { hue: 'var(--w-gate)', alert: true, eyes: 'open', moon: true },
  // Asleep: ears flat, eyes shut, breathing. Recognisably the bear that was working.
  sleeping:    { hue: deepen('var(--w-active)', 68), alert: false, eyes: 'shut', anim: 'anim-agentbreathe', zzz: true },
  idle:        { hue: 'var(--w-backlog)', alert: false, eyes: 'open' },
  stopped:     { hue: deepen('var(--w-backlog)', 45), alert: false, eyes: 'shut', dim: true },
};

/**
 * The head. Wide at the brow and drawn in at the jaw, which is a bear's skull and not a ball.
 *
 * The flat stretch across the top is the brow ridge. It is worth the two extra points: a head that
 * curves all the way over reads as a cub or a cartoon, and the difference between this animal and
 * a teddy is mostly what happens above the eyes.
 */
const HEAD = 'M50 9c21 0 34 14 34 34 0 15-6 27-16 34-6 4-12 6-18 6s-12-2-18-6C22 70 16 58 16 43 16 23 29 9 50 9z';
/** The muzzle: a pale mask over the lower face, forward of it rather than painted on. */
const SNOUT = 'M50 49c12 0 20 7 20 17 0 9-9 17-20 17s-20-8-20-17c0-10 8-17 20-17z';
/** The nose pad. Black in every light there is, which is why it is the one part not derived. */
const NOSE = 'M50 53c6 0 10 3.4 10 7.4 0 4-4 7.6-10 7.6s-10-3.6-10-7.6c0-4 4-7.4 10-7.4z';
/** Shoulders, under the jaw and running off the bottom of the box. Mass, so the head has a body. */
const SHOULDERS = 'M14 100c0-17 16-27 36-27s36 10 36 27z';

export const Bruin: Mascot = ({ mood, size: s, palette }: MascotProps) => {
  const m = MOODS[mood];
  // A palette only arrives when this bear stands for an agent that is actually working, so there is
  // no second rule here about when to wear it — see MascotProps.
  const hue = palette?.hex ?? m.hue;

  // A gradient is referenced by id, and three agents' worth of these are on screen at once — so the
  // id has to be per-instance or two bears end up wearing one bear's colour. See `Falcon.tsx`.
  const gid = 'w-bruin-' + useId().replace(/:/g, '');
  const pelt = `url(#${gid})`;

  // Lit over the crown and dropping toward the floor at the jaw, which is what stops a filled
  // silhouette reading as a sticker. Derived rather than picked, so the same head can wear an
  // agent's identity hue without anyone repicking a pair of blues.
  const lit = lighten(hue, 74);
  const floor = deepen(hue, 62);

  /**
   * One ear. Up and round when the bear is alert; smaller, lower and pushed outward when it is not,
   * which is what a bear's ear does and what makes the two states different shapes rather than the
   * same shape twice. The inner ear is a shade of the pelt and never a second colour — at 12px it
   * is two pixels, and two pixels of a different hue read as damage.
   */
  const ear = (x: number) => {
    const cx = m.alert ? x : x + (x < 50 ? -3 : 3);
    const cy = m.alert ? 24 : 34;
    const r = m.alert ? 13 : 9.5;
    return (
      <>
        <circle cx={cx} cy={cy} r={r} fill={pelt} opacity={m.alert ? 1 : 0.85} />
        <circle cx={cx} cy={cy} r={r * 0.5} fill={floor} opacity=".6" />
      </>
    );
  };

  // Wide when startled and shut to a line when asleep, and never below a pixel and a half of the
  // box: at 12px an eye drawn to scale rounds away, and the eyes are half of what makes this read
  // as an animal rather than as a lump.
  const eyeR = m.eyes === 'wide' ? 6 : 5.2;

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
        style={{
          position: 'absolute', inset: 0, overflow: 'visible',
          // Below about 16px the blur is wider than the head it is around, so it stops being light
          // and starts being a smudge.
          ...(m.glow && s >= 16 ? { filter: `drop-shadow(0 0 ${s * 0.14}px ${hue})` } : {}),
        }}
      >
        <defs>
          <linearGradient id={gid} x1=".2" y1="0" x2=".8" y2="1">
            <stop offset="0" style={{ stopColor: lit }} />
            <stop offset=".55" style={{ stopColor: hue }} />
            <stop offset="1" style={{ stopColor: floor }} />
          </linearGradient>
        </defs>

        {/* The shoulders sit outside the nod, and deliberately: a bear nods its head at you, and a
            bear whose shoulders swing with its ears is a bear on a spring. */}
        <path d={SHOULDERS} fill={floor} opacity=".85" />

        {/* Everything else nods together, so the ears and the muzzle stay on the head. `sn-nod` is
            the snow theme's own keyframe, injected with the theme — which is safe because this bear
            is only ever drawn under it. `fill-box` is what makes the origin mean the base of the
            skull rather than the corner of the viewport. */}
        <g
          style={{
            transformBox: 'fill-box', transformOrigin: '50% 82%',
            ...(m.nod ? { animation: 'sn-nod 3.4s ease-in-out infinite' } : {}),
          }}
        >
          {ear(21)}
          {ear(79)}
          <path d={HEAD} fill={pelt} />
          <path d={SNOUT} fill={lit} />
          <path d={NOSE} fill="var(--w-void)" />
          {(m.eyes === 'shut' ? [33, 67] : []).map(x => (
            <rect key={x} x={x - 5.6} y="40" width="11.2" height="3" rx="1.5" fill="var(--w-void)" />
          ))}
          {m.eyes !== 'shut' && [33, 67].map(x => (
            <g key={x}>
              <circle cx={x} cy="42" r={eyeR} fill="var(--w-void)" />
              {/* The catchlight. Three pixels at the size this is drawn for the heartbeat page,
                  nothing at all on the board, and the difference between an eye and a hole. */}
              <circle cx={x + 1.6} cy="40" r={eyeR * 0.33} fill={lit} />
            </g>
          ))}
        </g>
      </svg>

      {m.zzz && (
        <span className="anim-agentzzz" style={{ position: 'absolute', top: -s * 0.05, right: -s * 0.12, font: `700 ${s * 0.28}px var(--w-font-body)`, color: 'var(--w-active-400)' }}>z</span>
      )}
      {m.moon && (
        // Further out than the other mascots hang theirs: a bear alert enough to be waiting has
        // its ears up, and the corner this normally sits in is where the right one is.
        <span style={{ position: 'absolute', top: -s * 0.22, right: -s * 0.2, fontSize: s * 0.3 }}>🌙</span>
      )}
      {m.bang && (
        <span style={{ position: 'absolute', top: -s * 0.24, right: -s * 0.14, font: `700 ${s * 0.32}px var(--w-font-body)`, color: 'var(--w-blocked-200)' }}>!</span>
      )}
    </div>
  );
};
