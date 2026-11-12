import { useCallback, useEffect, useState } from 'react';
import * as api from '../api/tickets';
import type { PrototypeCaptures as Captures } from '../api/tickets';
import { useShotLightbox } from './ShotLightbox';

/**
 * The board's screenshots of the prototype and design links in a ticket - what the fleet reads in
 * place of a link it may not open - and the links it could not capture, with why. Renders nothing
 * for a project that captures no links, or a ticket that has none.
 */
export function PrototypeCaptures({ ticketId }: { ticketId: number }) {
  const [data, setData] = useState<Captures | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    api.getPrototypeCaptures(ticketId).then(setData).catch(() => setData(null));
  }, [ticketId]);
  useEffect(load, [load]);

  const shots = (data?.captures ?? [])
    .filter(c => c.imageUrl)
    .map((c, i) => ({ order: i, caption: `${c.title ? c.title + ' - ' : ''}${c.url}`, imageUrl: c.imageUrl! }));
  const { open, lightbox } = useShotLightbox(shots);

  if (!data?.enabled || data.captures.length === 0) return null;
  const failed = data.captures.filter(c => !c.imageUrl);

  const again = async () => {
    setBusy(true); setError(null);
    try { setData(await api.capturePrototypes(ticketId)); }
    catch (e) { setError((e as Error).message); }
    finally { setBusy(false); }
  };

  return (
    <div>
      <div className="flex items-center gap-2 mb-2.5">
        <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
          Prototype screenshots
          <span className="normal-case tracking-normal text-gray-600"> · taken by the board, read by every phase</span>
        </div>
        <button
          type="button"
          disabled={busy}
          onClick={again}
          className="orbit-tap ml-auto text-[11px] px-2 py-0.5 rounded border border-hair text-gray-400 hover:text-gray-200 disabled:opacity-40"
        >
          {busy ? 'Capturing…' : 'Capture again'}
        </button>
      </div>
      {shots.length > 0 && (
        <div className="grid grid-cols-1 sm:grid-cols-3 gap-3">
          {data.captures.filter(c => c.imageUrl).map((c, i) => (
            <figure key={c.url} className="flex flex-col gap-1.5 min-w-0">
              <button
                type="button"
                onClick={() => open(i)}
                title="Open full screenshot"
                className="block rounded-lg border border-hair bg-black/40 overflow-hidden hover:border-gray-600 transition-colors cursor-zoom-in"
              >
                <img src={c.imageUrl!} alt={c.title || c.url} loading="lazy" className="w-full h-32 object-cover object-top" />
              </button>
              <figcaption className="text-[12px] leading-snug text-gray-400 min-w-0">
                <span className="block truncate" title={c.url}>{c.linkText || c.title || c.url}</span>
                <span className="block text-gray-600">in {c.foundIn}</span>
              </figcaption>
            </figure>
          ))}
        </div>
      )}
      {failed.length > 0 && (
        <ul className="mt-2.5 rounded-lg border border-amber-900/50 bg-amber-950/10 px-3 py-2 text-[12.5px] leading-snug text-amber-200/90 space-y-1">
          {failed.map(c => (
            <li key={c.url} className="min-w-0">
              <span className="font-mono break-all">{c.url}</span>
              <span className="text-amber-100/70"> (in {c.foundIn}): {c.error}</span>
            </li>
          ))}
          <li className="text-amber-100/60">
            Nobody on the fleet can see these, so a run that needs them blocks and asks for screenshots.
          </li>
        </ul>
      )}
      {data.unavailable && <p className="mt-1.5 text-[11.5px] text-gray-500">This machine cannot capture: {data.unavailable}</p>}
      {error && <p className="mt-1.5 text-[11.5px] text-rose-300">{error}</p>}
      {lightbox}
    </div>
  );
}
