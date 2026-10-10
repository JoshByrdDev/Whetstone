import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { createPortal } from 'react-dom';
import { useHeartbeatPriorities } from '../hooks/useHeartbeatPriorities';
import { PRIORITIES, PRIORITY_COLOR, PRIORITY_HINT, PRIORITY_LEVEL, PRIORITY_MARK } from '../types/tickets';
import type { Priority } from '../types/tickets';
import { BODY, CAPS, DISPLAY, MONO, chamfer } from './orbit';
import { track } from '../theme';
import { usePop } from '../hooks/usePop';

/**
 * Two skins on one switch: which priority bands the heartbeat may claim tickets from.
 *
 * The sibling of {@link HeartbeatProjectFilter} in every respect — same pages, same empty-means-all
 * rule, same last-one-standing guard, all of it shared through `useHeartbeatAllowSet`. It sits
 * beside the project switch because the two answer the same question about different axes: not
 * "what does the board show", but "what may the fleet touch".
 *
 * Marks and colours are the board's own priority vocabulary ({@link PRIORITY_MARK}), so a band
 * switched off here is recognisably the same thing as the `▲ HIGH` chip on a row up there. The one
 * thing it must not be mistaken for is the ticket's own priority control: that says where in the
 * queue a ticket sits, this says which queues are open at all.
 */

/** Roomy version, for the Heartbeat page, under the project switch. */
export function HeartbeatPriorityFilter() {
  const { selected, toggle, saving, error, loaded, allSelected } = useHeartbeatPriorities();

  if (!loaded) return null;

  return (
    <div className="flex items-center gap-2 flex-wrap text-xs">
      <span className="text-gray-500 uppercase tracking-wider font-semibold">Work at</span>
      {PRIORITIES.map(p => {
        const on = selected.has(p);
        return (
          <button
            key={p}
            onClick={() => toggle(p)}
            title={bandHint(p, on, selected.size)}
            className={`px-2.5 py-1 rounded border transition-colors ${
              on ? 'bg-white/[.07]' : 'bg-gray-800 border-hair text-gray-500 hover:text-gray-300'
            }`}
            style={on ? { borderColor: PRIORITY_COLOR[p], color: PRIORITY_COLOR[p] } : undefined}
          >
            <span style={{ fontSize: 9 }}>{PRIORITY_MARK[p]}</span>{' '}{p}
          </button>
        );
      })}
      <span className="text-gray-600">
        {allSelected ? 'every priority' : `${selected.size} of ${PRIORITIES.length}`}
        {' · applies next cycle'}{saving ? ' · saving…' : ''}
      </span>
      {error && <span className="text-rose-300">{error}</span>}
    </div>
  );
}

const PANEL_W = 244;
const PANEL_GAP = 5;

/**
 * Narrow version, for the Board's manifest line: the bands actually on duty, and nothing else.
 *
 * The five-chip row this replaced spent four fifths of its width saying what the fleet is NOT
 * doing. On a line whose whole job is a one-glance summary of the sector that is backwards — the
 * off bands were the loudest thing about a control whose resting state is "all of them", and the
 * one lit chip that mattered had to be picked out of four dim ones.
 *
 * So the trigger states only the positive: `ALL BANDS` when nothing is narrowed, otherwise the
 * marks of the bands that are on. The off bands are not hidden so much as demoted to where you go
 * when you actually want to change something — a panel one click away, which has the room to name
 * each band, number it, and say what standing one down does.
 *
 * Opened by click, and only by click. This decides what the fleet works while nobody is watching,
 * and the manifest line is a thing the eye crosses on its way to the board — a panel that let
 * itself out every time the pointer passed over would be in the way far more often than it was
 * wanted, and it stays open across several toggles, which is a poor fit for a control you dismiss
 * by looking away.
 */
export function HeartbeatPriorityStrip() {
  const { selected, toggle, saving, error, loaded, allSelected } = useHeartbeatPriorities();
  const [open, setOpen] = useState(false);
  // Stays mounted a little past its own close, so the panel is seen to go rather than blink out.
  const { show, fx } = usePop(open);
  const [pos, setPos] = useState<{ left: number; top: number; flip: boolean } | null>(null);
  const wrap = useRef<HTMLDivElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  // Escape, and a press anywhere that is neither the trigger nor the panel. The trigger has to be
  // excluded rather than merely unhandled: this runs on the capture phase, so closing here on a
  // trigger press would land before its own click, and the toggle would then reopen what the press
  // was trying to shut. Clicking a band does NOT close - the whole point is picking several.
  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') setOpen(false); };
    const onDown = (e: PointerEvent) => {
      const t = e.target as Node;
      if (!wrap.current?.contains(t) && !panel.current?.contains(t)) setOpen(false);
    };
    window.addEventListener('keydown', onKey);
    window.addEventListener('pointerdown', onDown, true);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('pointerdown', onDown, true);
    };
  }, [open]);

  // Anchored in fixed coordinates and flipped above when there is no room below: the manifest line
  // sits near the top of a page that scrolls, and the board body scrolls horizontally under it.
  const place = useCallback(() => {
    const box = wrap.current?.getBoundingClientRect();
    if (!box) return;
    const h = panel.current?.offsetHeight ?? 0;
    const below = box.bottom + PANEL_GAP;
    const flip = h > 0 && below + h > window.innerHeight - 8;
    setPos({
      left: Math.max(8, Math.min(box.left, window.innerWidth - PANEL_W - 8)),
      top: flip ? Math.max(8, box.top - PANEL_GAP - h) : below,
      // Which way it opened, so the panel can arrive from the edge it is hanging off rather than
      // always from above - see `.orbit-pop`.
      flip,
    });
  }, []);

  useLayoutEffect(() => {
    if (!open) return;
    place();
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [open, place]);

  if (!loaded) return null;

  const on = PRIORITIES.filter(p => selected.has(p));

  return (
    <div ref={wrap} className="flex items-center gap-1.5">
      <span style={{
        font: `600 9.5px ${DISPLAY}`, letterSpacing: track(.22),
        textTransform: CAPS, color: 'var(--w-t600)',
      }}>
        Work at
      </span>

      <button
        onClick={() => setOpen(o => !o)}
        aria-expanded={open}
        aria-haspopup="true"
        title={triggerHint(selected.size, allSelected)}
        className="orbit-btn flex items-center gap-1.5"
        style={{
          clipPath: chamfer(7),
          padding: '3px 8px',
          font: `700 9px ${DISPLAY}`, letterSpacing: track(.22), textTransform: CAPS,
          color: allSelected ? 'var(--w-t500)' : 'var(--w-t400)',
          // Nothing here is worth selecting, and a menu that highlights its own label blue as you
          // click through three bands looks broken in a way the fix is one line away from.
          userSelect: 'none',
          // Resting colours as custom properties, never as `background`/`border` - see .orbit-btn.
          // The error border is the one thing that has to be legible without hovering: a save that
          // did not land means the fleet is running the previous set, and the panel saying so is
          // behind the very hover the owner has no reason to perform.
          ['--rbg' as string]: 'rgb(var(--w-line-rgb) / .06)',
          ['--rbw' as string]: `1px solid ${error ? 'var(--w-blocked)' : 'rgb(var(--w-line-rgb) / .16)'}`,
          ['--hbg' as string]: 'rgb(var(--w-line-rgb) / .14)',
          ['--hbc' as string]: error ? 'var(--w-blocked)' : 'rgb(var(--w-line-rgb) / .34)',
          ['--htxt' as string]: 'var(--w-t300)',
          ['--hsh' as string]: 'none',
        } as CSSProperties}
      >
        {allSelected
          ? <span>All priorities</span>
          : on.map(p => (
              <span key={p} style={{ color: PRIORITY_COLOR[p] }}>
                <span style={{ fontSize: 7.5, letterSpacing: 0 }}>{PRIORITY_MARK[p]}</span>{' '}P{PRIORITY_LEVEL[p]}
              </span>
            ))}
        <Chevron open={open} />
      </button>

      {/* Never only a colour: a red border on a chip this small is a smudge, and the one state the
          owner must not miss is the one where the switch and the orchestrator disagree. */}
      {error && <span style={{ color: 'var(--w-blocked-300)' }}>not saved</span>}
      {saving && !error && <span style={{ color: 'var(--w-t600)' }}>saving…</span>}

      {show && createPortal(
        <div
          ref={panel}
          className="orbit-pop"
          data-fx={fx}
          data-side={pos?.flip ? "above" : undefined}
          style={{
            position: 'fixed', left: pos?.left ?? -9999, top: pos?.top ?? -9999,
            width: PANEL_W, zIndex: 50,
            clipPath: chamfer(10),
            background: 'rgb(var(--w-panel-lit-rgb) / .97)',
            border: '1px solid rgb(var(--w-line-rgb) / .22)',
            boxShadow: '0 18px 44px -18px rgba(0,0,0,.95)',
            padding: '7px 0 0',
          }}
        >
          <div style={{
            padding: '0 11px 6px',
            font: `600 8.5px ${DISPLAY}`, letterSpacing: track(.24),
            textTransform: CAPS, color: 'var(--w-t600)',
          }}>
            Priorities the fleet may claim
          </div>

          {PRIORITIES.map(p => {
            const isOn = selected.has(p);
            const stuck = isOn && selected.size <= 1;
            return (
              <button
                key={p}
                onClick={() => toggle(p)}
                title={bandHint(p, isOn, selected.size)}
                className="orbit-btn flex w-full items-center gap-2 text-left"
                style={{
                  padding: '5px 11px',
                  font: `600 10px ${DISPLAY}`, letterSpacing: track(.16), textTransform: CAPS,
                  color: isOn ? PRIORITY_COLOR[p] : 'var(--w-t600)',
                  cursor: stuck ? 'default' : 'pointer',
                  userSelect: 'none',
                  ['--rbg' as string]: 'transparent',
                  ['--rbw' as string]: '1px solid transparent',
                  ['--hbg' as string]: 'rgb(var(--w-line-rgb) / .1)',
                  ['--hbc' as string]: 'transparent',
                  ['--htxt' as string]: isOn ? PRIORITY_COLOR[p] : 'var(--w-t400)',
                  ['--hsh' as string]: 'none',
                } as CSSProperties}
              >
                {/* A fixed tick column rather than one that appears and disappears: names that
                    shift sideways as you switch bands are names you have to re-find every click. */}
                <span style={{ width: 8, fontSize: 9, opacity: isOn ? 1 : 0 }}>✓</span>
                <span style={{ width: 15, fontSize: 8, letterSpacing: 0 }}>{PRIORITY_MARK[p]}</span>
                <span style={{ flex: 1 }}>{p}</span>
                <span style={{ font: `500 9px ${MONO}`, letterSpacing: 0, opacity: .55 }}>
                  P{PRIORITY_LEVEL[p]}
                </span>
              </button>
            );
          })}

          {/* The room the strip never had to say what this actually does — and the passed-over
              rather than blocked distinction is the one worth spending it on. */}
          <div style={{
            borderTop: '1px solid rgb(var(--w-line-rgb) / .12)',
            marginTop: 5, padding: '6px 11px 7px',
            font: `400 9.5px ${BODY}`, lineHeight: 1.5, color: 'var(--w-t600)',
          }}>
            {error ? <span style={{ color: 'var(--w-blocked-300)' }}>
                       Not saved — the fleet is still claiming from the previous set.
                     </span>
             : saving ? 'Saving…'
             : allSelected ? 'Every priority on duty. Switch one off and its tickets are passed over — never blocked — from the next cycle.'
             : `${selected.size} of ${PRIORITIES.length} on duty. The rest are passed over, not blocked — claimable again the moment you switch them back on.`}
          </div>
        </div>,
        document.body,
      )}
    </div>
  );
}

/** The affordance, drawn rather than typed: the band marks are already arrows. */
function Chevron({ open }: { open: boolean }) {
  return (
    <svg
      width="7" height="7" viewBox="0 0 8 8" aria-hidden
      style={{
        opacity: .5, flex: 'none',
        transform: open ? 'rotate(180deg)' : undefined,
        transition: 'transform .15s ease',
      }}
    >
      <path d="M1 2.75 L4 5.5 L7 2.75" fill="none" stroke="currentColor"
            strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  );
}

/** What the collapsed chip is hiding, for somebody who has not yet discovered it opens. */
function triggerHint(count: number, allSelected: boolean): string {
  return allSelected
    ? 'The fleet claims tickets at every priority. Click to narrow it.'
    : `The fleet claims tickets at ${count} of ${PRIORITIES.length} priorities — the rest are passed over, not blocked. Click to change.`;
}

/**
 * What the button will do, and — for the last band standing — why it will not. A control that
 * silently ignores a click is a control the owner assumes is broken.
 *
 * The off-duty wording is deliberate about what standing a band down does NOT do: those tickets are
 * passed over, not blocked, so nobody has to go and unblock them by hand afterwards.
 */
function bandHint(p: Priority, on: boolean, selectedCount: number): string {
  if (!on) return `P${PRIORITY_LEVEL[p]} ${p}: off duty — its tickets are passed over, not blocked. Click to let the fleet claim them again.`;
  if (selectedCount <= 1) return `P${PRIORITY_LEVEL[p]} ${p} is the only priority on duty. Use Stop to pause the heartbeat entirely.`;
  return `P${PRIORITY_LEVEL[p]} ${p}: on duty. ${PRIORITY_HINT[p]} Click to stand it down from the next cycle.`;
}
