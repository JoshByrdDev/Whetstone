import type { TierName } from './tokens';

/**
 * The states a Recon coverage cell, or an area, can be in (docs/recon-tab-plan.md, "Look and
 * phone"), named here so no page picks a colour for one. Each borrows a tier - so a theme recolours
 * it with everything else - and each also carries a mark and a word, because colour alone says
 * nothing to a reader who cannot tell two hues apart, or to one on a phone in the sun.
 *
 * - `gap`: nobody has scouted it. The backlog's quiet grey: nothing is wrong, nothing is there.
 * - `fresh`: scouted, and its area's code has not changed since. The done tier.
 * - `changed`: its area's code changed since it was scouted - the thing worth acting on, so the
 *   active amber.
 * - `unknown`: scouted on a commit that is not on the base, so what changed since cannot be said.
 *   Never shown as fresh. The gate's violet: it is waiting on someone to look.
 * - `cooling`: an area whose last three discoveries found nothing approved. The queue's blue.
 */
export type ReconState = 'gap' | 'fresh' | 'changed' | 'unknown' | 'cooling';

export interface ReconStateToken {
  tier: TierName;
  /** Drawn before the word; text, so it scales and reads out with it. */
  mark: string;
  word: string;
}

export const RECON_STATE: Record<ReconState, ReconStateToken> = {
  gap:     { tier: 'backlog', mark: '○', word: 'gap' },
  fresh:   { tier: 'done',    mark: '●', word: 'fresh' },
  changed: { tier: 'active',  mark: '◆', word: 'changed' },
  unknown: { tier: 'gate',    mark: '?', word: 'unknown' },
  cooling: { tier: 'queue',   mark: '▽', word: 'cooling' },
};

/** A state's token, with anything the marts might add later falling back to `unknown` rather than to nothing. */
export const reconState = (s: string | null | undefined): ReconStateToken =>
  RECON_STATE[(s ?? 'unknown') as ReconState] ?? RECON_STATE.unknown;
