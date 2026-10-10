import { useCallback, useEffect, useState } from 'react';
import { createPortal } from 'react-dom';
import { useVeil } from '../hooks/useVeil';

/** The shape both QA shot grids already hold — order, caption, and where the image lives. */
export interface LightboxShot {
  order: number;
  caption: string;
  imageUrl: string;
}

/**
 * A QA screenshot, opened over the page it was opened from.
 *
 * A new tab was the wrong answer for these: the shot only means anything next to the analysis that
 * cites it, and a tab switch throws that context away and makes getting back the reader's problem.
 * Here the page stays where it was, escape or a click on the void returns to it, and when a ticket
 * has more than one shot the arrows walk them without going back to the grid each time.
 *
 * The raw file is still one click away, for the case the overlay cannot serve: zooming in on a
 * detail, or saving the image.
 */
export function ShotLightbox({ shots, index, onIndex, onClose }: {
  shots: LightboxShot[];
  index: number;
  onIndex: (i: number) => void;
  onClose: () => void;
}) {
  // The drawer that opened this unmounts it on close, so the overlay sees itself out: the shot
  // fades and settles back rather than being cut away mid-read.
  const { fx, dismiss } = useVeil(onClose);
  const shot = shots[index];
  const many = shots.length > 1;
  const step = useCallback((d: number) => {
    onIndex((index + d + shots.length) % shots.length);
  }, [index, shots.length, onIndex]);

  // Escape closes, the way it closes every other overlay here; the arrows move between shots.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') dismiss();
      else if (many && e.key === 'ArrowLeft') step(-1);
      else if (many && e.key === 'ArrowRight') step(1);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss, step, many]);

  if (!shot) return null;

  // Through the body, not in place: the ticket drawer slides in on a transform, and a transformed
  // ancestor is what `position: fixed` measures itself against, so an overlay rendered inside the
  // drawer lands a drawer-width off the screen instead of over it.
  return createPortal(
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-6 sm:p-10" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/85" onClick={dismiss} />

      {/* Stops at the viewport rather than filling it: a screenshot blown past its own resolution
          is harder to read, not easier, and the margin is what says this is an overlay. */}
      <figure className="relative flex max-h-full min-w-0 flex-col items-center gap-3">
        <img
          src={shot.imageUrl}
          alt={shot.caption || `QA shot ${shot.order + 1}`}
          className="max-h-[80vh] max-w-full object-contain border border-hair bg-black/40 shadow-2xl"
        />
        {shot.caption && (
          <figcaption className="max-w-[80ch] text-center text-[13px] leading-snug text-gray-300">
            {shot.caption}
          </figcaption>
        )}
        <div className="flex items-center gap-4 text-[11px] text-gray-500">
          {many && (
            <>
              <button onClick={() => step(-1)} title="Previous shot (left arrow)" className="hover:text-gray-200">←</button>
              <span className="font-mono">{index + 1} / {shots.length}</span>
              <button onClick={() => step(1)} title="Next shot (right arrow)" className="hover:text-gray-200">→</button>
              <span className="text-gray-700">·</span>
            </>
          )}
          <a href={shot.imageUrl} target="_blank" rel="noreferrer" className="hover:text-gray-200 underline">
            Open the file
          </a>
          <span className="text-gray-700">·</span>
          <button onClick={dismiss} className="hover:text-gray-200">Close (esc)</button>
        </div>
      </figure>

      <button
        onClick={dismiss}
        title="Close"
        className="absolute right-4 top-4 text-lg text-gray-500 hover:text-gray-200"
      >
        ✕
      </button>
    </div>,
    document.body,
  );
}

/**
 * The state behind one grid of shots. Both grids want exactly this and nothing else, so it lives
 * here rather than being spelled out twice: `open(i)` from a thumbnail, `lightbox` rendered at the
 * end of the section.
 */
export function useShotLightbox(shots: LightboxShot[]) {
  const [index, setIndex] = useState<number | null>(null);
  const close = useCallback(() => setIndex(null), []);
  return {
    open: setIndex as (i: number) => void,
    lightbox: index === null ? null : (
      <ShotLightbox shots={shots} index={index} onIndex={setIndex} onClose={close} />
    ),
  };
}
