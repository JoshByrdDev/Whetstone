import type { ReactNode } from 'react';
import { reconState } from '../../theme';
import { TIER, MONO, chamfer } from '../orbit';
import type { ReconCell } from './types';

/** A day as the reasons say it: "3 Oct". */
export function shortDay(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

export const sha7 = (s: string | null | undefined) => (s ? s.slice(0, 7) : '—');

/** A width bucket in words. Phone and desk are split at NARROW_PX, as the marts split them. */
export const widthWord = (w: string) => (w === 'phone' ? 'on a phone' : w === 'desk' ? 'at a desk' : 'at an unknown width');

/** A width bucket as a column heading. */
export const widthShort = (w: string) => (w === 'phone' ? 'phone' : w === 'desk' ? 'desk' : w);

/** "as admin on a phone"; "anyone at a desk" for a project with no roles. */
export function cellPhrase(c: ReconCell): string {
  const who = c.role === 'any role' ? 'anyone' : c.role === 'other' ? 'a role not in the list' : `as ${c.role}`;
  return `${who} ${widthWord(c.width)}`;
}

export function secondsWord(s: number | null | undefined): string {
  if (!s) return '0s';
  if (s < 90) return `${Math.round(s)}s`;
  const m = s / 60;
  return m < 90 ? `${Math.round(m)}m` : `${(m / 60).toFixed(1)}h`;
}

export const money = (usd: number | null | undefined) =>
  usd === null || usd === undefined ? '—' : usd < 10 ? `$${usd.toFixed(2)}` : `$${Math.round(usd)}`;

/** A cell or area state as its mark and its word, in its tier's colour - never colour alone. */
export function StateMark({ state, word }: { state: string; word?: string }) {
  const t = reconState(state);
  return (
    <span className="inline-flex items-center gap-1 whitespace-nowrap" style={{ color: TIER[t.tier].c }}>
      <span aria-hidden>{t.mark}</span>
      <span>{word ?? t.word}</span>
    </span>
  );
}

/** A small chamfered well: a reason, a fact. Wraps rather than widening its row. */
export function Fact({ children }: { children: ReactNode }) {
  return (
    <span className="inline-block max-w-full border border-hair bg-sunk px-2 py-0.5 text-[11px] leading-snug text-gray-400"
      style={{ clipPath: chamfer(5), overflowWrap: 'anywhere' }}>
      {children}
    </span>
  );
}

/** Text the eye reads as code: a sha, a pattern. */
export const mono = { fontFamily: MONO } as const;

/** A notice that is the section's whole answer in some state: said in words, with what to do next. */
export function Notice({ children, warn }: { children: ReactNode; warn?: boolean }) {
  return (
    <div className={`min-w-0 border border-hair bg-panel px-4 py-3 text-xs leading-relaxed ${warn ? 'text-red-300' : 'text-gray-400'}`}
      style={{ clipPath: chamfer(10), overflowWrap: 'anywhere' }}>
      {children}
    </div>
  );
}

/** A link-like control inside running text. */
export function TextButton({ onClick, children }: { onClick: () => void; children: ReactNode }) {
  return <button className="orbit-tap text-amber-300 underline" onClick={onClick}>{children}</button>;
}
