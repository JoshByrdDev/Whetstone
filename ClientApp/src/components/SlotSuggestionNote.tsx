import { useState } from 'react';
import { GhostButton, DIM, MUTED, BRIGHT } from './ui';
import { BODY, MONO } from './orbit';
import type { SlotSuggestion } from '../hooks/useMachines';

/**
 * What a machine's own record suggests for one hand-set count, under the control that sets it:
 * "suggested 4 · learned, medium", a disclosure with the reasons and their numbers, and a button
 * that puts the suggestion in the field. Advisory: the owner's number stands until they change it.
 */
const BASIS: Record<SlotSuggestion['basis'], string> = {
  hardware: 'from hardware', learned: 'learned from its load', ceiling: 'capped by a warning sign', none: '',
};

export function SlotSuggestionNote({ s, current, onUse, busy }: {
  s: SlotSuggestion | undefined;
  /** The field's value now, which can be newer than the suggestion's read of the config. */
  current?: number;
  /** Puts the suggestion in the field. Absent where there is nothing to put it in. */
  onUse?: (n: number) => void;
  busy?: boolean;
}) {
  const [open, setOpen] = useState(false);
  if (!s) return null;
  const set = current != null && !Number.isNaN(current) ? current : s.set;
  const same = s.suggested === set;
  const tone = s.suggested == null || same ? DIM : s.suggested < set ? 'rgb(var(--w-gate-rgb))' : MUTED;
  return (
    <div className="min-w-0" style={{ font: `400 10.5px ${BODY}`, color: MUTED }}>
      <div className="flex items-center gap-2 flex-wrap">
        {s.suggested == null ? (
          <span style={{ color: DIM }}>No suggestion yet</span>
        ) : (
          <span>
            <span style={{ color: DIM }}>{set} set by you · </span>
            <span style={{ color: tone }}>
              suggested <span style={{ font: `700 11px ${MONO}`, color: same ? DIM : BRIGHT }}>{s.suggested}</span>
            </span>
            <span style={{ color: DIM }}> · {BASIS[s.basis]}{s.basis !== 'none' ? `, ${s.confidence} confidence` : ''}</span>
          </span>
        )}
        <button type="button" className="orbit-tap underline decoration-dotted" style={{ color: DIM }}
          aria-expanded={open} onClick={() => setOpen(o => !o)}>
          {open ? 'hide why' : 'why'}
        </button>
        {onUse && s.suggested != null && !same && (
          <GhostButton disabled={busy} onClick={() => onUse(s.suggested!)} title="Put the suggestion in this field. Your number is replaced only when you do this.">
            Use {s.suggested}
          </GhostButton>
        )}
      </div>
      {open && (
        <ul key="why" className="orbit-swap mt-1 space-y-0.5 list-disc pl-4" style={{ color: MUTED }}>
          {s.reasons.length === 0 ? <li>Nothing recorded yet.</li> : s.reasons.map((r, i) => <li key={i}>{r}</li>)}
        </ul>
      )}
    </div>
  );
}
