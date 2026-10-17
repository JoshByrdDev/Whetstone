import { useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { SectionRule } from './ui';
import { BODY, CAPS, DISPLAY, MONO, chamfer } from './orbit';
import { track } from '../theme';

/** The pieces the Steward page's views share: its small label, its sense of "ago", a folding section. */

/** The small tracked caps this app labels things with. */
export const label = (colour = 'var(--w-t500)'): CSSProperties => ({
  font: `600 9px ${DISPLAY}`, letterSpacing: track(0.3), color: colour, textTransform: CAPS,
});

export function ago(iso?: string | null): string {
  if (!iso) return 'never';
  const mins = Math.max(0, Math.round((Date.now() - new Date(iso).getTime()) / 60000));
  if (mins < 1) return 'just now';
  if (mins < 60) return `${mins}m ago`;
  if (mins < 60 * 24) return `${Math.round(mins / 60)}h ago`;
  return `${Math.round(mins / (60 * 24))}d ago`;
}

/** Something a person last chose on this page, kept per machine so it opens as they left it. */
const PREFIX = 'whetstone.steward.';

export function remembered(id: string): string | null {
  try { return localStorage.getItem(PREFIX + id); }
  catch { return null; }   // private mode, blocked storage: the page just opens on its default
}

export function remember(id: string, value: string) {
  try { localStorage.setItem(PREFIX + id, value); } catch { /* still stands for this visit */ }
}

/**
 * One section of the page: its heading, and a body that folds away under it.
 *
 * The heading is the control, whole, because it is the only thing left to aim at when the section
 * is shut. `open` is where the section starts for someone who has never touched it: what needs a
 * person, and what says how things stand, start open; the record, which is there to be looked
 * things up in, starts shut and still shows its count.
 */
export function Fold({ id, open: startsOpen = false, title, colour, count, meta, right, children }: {
  id: string; open?: boolean; title: string; colour?: string; count?: number; meta?: string;
  right?: ReactNode; children: ReactNode;
}) {
  const [open, setOpen] = useState(() => {
    const v = remembered(`fold.${id}`);
    return v === null ? startsOpen : v === '1';
  });
  const toggle = () => {
    const next = !open;
    setOpen(next);
    remember(`fold.${id}`, next ? '1' : '0');
  };
  return (
    <>
      <button
        onClick={toggle}
        aria-expanded={open}
        title={open ? 'Hide this section' : 'Show this section'}
        className="orbit-tap block w-full text-left"
      >
        <SectionRule
          title={title} colour={colour} count={count} meta={meta}
          right={(
            <>
              {right}
              <span style={{ font: `700 10px ${MONO}`, color: 'var(--w-t500)', width: 10, textAlign: 'center' }}>
                {open ? '-' : '+'}
              </span>
            </>
          )}
        />
      </button>
      {/* Keyed on the state so the body arrives each time it is opened, not only the first. */}
      {open
        ? <div key="open" className="orbit-swap">{children}</div>
        : <div style={{ height: 12 }} />}
    </>
  );
}

/** One answer to choose among: a question's option, or one of a decision's. The chosen one is `aria-current`. */
export function OptionRow({ on, onClick, title, detail, tag }: {
  on: boolean; onClick: () => void; title: ReactNode; detail?: ReactNode; tag?: string;
}) {
  return (
    <button
      onClick={onClick}
      aria-current={on ? 'true' : undefined}
      className="orbit-pick text-left"
      style={{
        clipPath: chamfer(8),
        padding: '8px 12px',
        background: on ? 'rgb(var(--w-accent-rgb) / .13)' : 'rgb(var(--w-line-rgb) / .04)',
        border: `1px solid ${on ? 'rgb(var(--w-accent-rgb) / .5)' : 'rgb(var(--w-line-rgb) / .14)'}`,
      }}
    >
      <div className="flex items-baseline gap-2 flex-wrap">
        <span className="min-w-0" style={{ font: `600 12.5px ${BODY}`, color: on ? 'var(--w-accent-text-lit)' : 'var(--w-t200)' }}>{title}</span>
        {tag && <span style={label('var(--w-accent-text)')}>{tag}</span>}
      </div>
      {detail && <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t500)', marginTop: 2 }}>{detail}</div>}
    </button>
  );
}

export function Quiet({ children }: { children: ReactNode }) {
  return <div className="mb-8" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t600)', padding: '4px 0 0 15px' }}>{children}</div>;
}
