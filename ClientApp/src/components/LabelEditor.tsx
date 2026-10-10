import { useEffect, useMemo, useRef, useState } from 'react';
import type { LabelRef, LabelUsage } from '../types/tickets';
import * as labelsApi from '../api/labels';
import { BODY } from './orbit';
import { usePop, useHeld } from '../hooks/usePop';

/**
 * A ticket's labels: the chips it wears, and one control for adding another.
 *
 * The add control is deliberately a single input rather than a "pick one" list beside a "make one"
 * field. Typing filters what you already use and Enter takes whichever is highlighted; typing
 * something new and pressing Enter creates it and puts it on in the same keystroke. Which of the two
 * happened is the server's question to answer (it matches by name, case-insensitively), so the
 * person filing never has to know whether the word they just typed was already in their vocabulary.
 *
 * Nothing here reaches the pipeline. Labels are your own filing — no phase reads one, no skill is
 * told about them, and the API keeps them off the shape workers receive (see `api/labels.ts`).
 */
export function LabelEditor({ ticketId, disabled, onChanged }: {
  ticketId: number;
  disabled?: boolean;
  /** Fired after any change, so the board's row chips can catch up. */
  onChanged?: () => void;
}) {
  const [labels, setLabels] = useState<LabelRef[] | null>(null);
  const [vocabulary, setVocabulary] = useState<LabelUsage[]>([]);
  const [adding, setAdding] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    labelsApi.getTicketLabels(ticketId)
      .then(l => { if (live) setLabels(l); })
      .catch(e => { if (live) setError(e instanceof Error ? e.message : 'Failed to load labels'); });
    return () => { live = false; };
  }, [ticketId]);

  // The vocabulary is only wanted while the picker is open, and it goes stale the moment a label is
  // created or garbage-collected — so it is fetched on opening rather than held for the drawer's life.
  useEffect(() => {
    if (!adding) return;
    let live = true;
    labelsApi.getLabels().then(r => { if (live) setVocabulary(r.labels); }).catch(() => {});
    return () => { live = false; };
  }, [adding, labels]);

  const run = async (fn: () => Promise<LabelRef[]>) => {
    setBusy(true); setError(null);
    try { setLabels(await fn()); onChanged?.(); }
    catch (e) { setError(e instanceof Error ? e.message : 'Action failed'); }
    finally { setBusy(false); }
  };

  const locked = disabled || busy;

  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex flex-wrap items-center gap-1.5">
        {labels?.map(l => (
          <span
            key={l.id}
            className="group inline-flex items-center gap-1 rounded border border-hair bg-white/[.04] pl-2 pr-1 py-[3px] text-[11.5px] leading-none text-gray-300"
          >
            {l.name}
            <button
              type="button"
              disabled={locked}
              title={`Remove "${l.name}" from this ticket`}
              onClick={() => run(() => labelsApi.removeTicketLabel(ticketId, l.id))}
              className="text-gray-600 hover:text-red-400 disabled:hover:text-gray-600 text-[13px] leading-none px-0.5 transition-colors"
            >
              ×
            </button>
          </span>
        ))}

        {adding ? (
          <LabelInput
            vocabulary={vocabulary}
            already={labels ?? []}
            disabled={locked}
            onPick={async name => { await run(() => labelsApi.addTicketLabel(ticketId, name)); }}
            onClose={() => setAdding(false)}
          />
        ) : (
          <button
            type="button"
            disabled={locked}
            onClick={() => setAdding(true)}
            className="inline-flex items-center gap-1 rounded border border-dashed border-hair px-2 py-[3px] text-[11.5px] leading-none text-gray-500 hover:text-gray-300 hover:border-gray-600 disabled:opacity-50 transition-colors"
          >
            + Label
          </button>
        )}

        {labels?.length === 0 && !adding && (
          <span className="text-[11px] text-gray-600">No labels.</span>
        )}
      </div>

      {error && <div className="text-[11px] text-red-400">{error}</div>}
    </div>
  );
}

/**
 * The combobox. Open list of what you already use, filtered as you type, with the typed word itself
 * offered as the last row when it is not already in there — so "create" is never a separate gesture,
 * just the bottom of the same list.
 */
function LabelInput({ vocabulary, already, disabled, onPick, onClose }: {
  vocabulary: LabelUsage[];
  already: LabelRef[];
  disabled?: boolean;
  onPick: (name: string) => Promise<void>;
  onClose: () => void;
}) {
  const [text, setText] = useState('');
  const [cursor, setCursor] = useState(0);
  const boxRef = useRef<HTMLSpanElement>(null);

  const typed = text.trim().replace(/\s+/g, ' ');
  const worn = useMemo(() => new Set(already.map(l => l.name.toLowerCase())), [already]);

  // What is on offer: labels you use that this ticket is not already wearing, narrowed by what you
  // have typed. The typed word joins the bottom of the list unless it is already up there, which is
  // what makes creating and choosing the same keystroke.
  const suggestions = useMemo(() => {
    const q = typed.toLowerCase();
    const matches = vocabulary
      .filter(l => !worn.has(l.name.toLowerCase()))
      .filter(l => !q || l.name.toLowerCase().includes(q))
      .slice(0, 8);
    const exact = matches.some(l => l.name.toLowerCase() === q) || worn.has(q);
    return typed && !exact
      ? [...matches, { id: -1, name: typed, count: 0 }]
      : matches;
  }, [vocabulary, worn, typed]);

  useEffect(() => { setCursor(0); }, [typed]);

  // Clicking anywhere else is "I am done adding" — the same way the chips' own controls behave, and
  // the reason there is no Cancel button competing with the list for attention.
  useEffect(() => {
    const away = (e: MouseEvent) => {
      if (boxRef.current && !boxRef.current.contains(e.target as Node)) onClose();
    };
    document.addEventListener('mousedown', away);
    return () => document.removeEventListener('mousedown', away);
  }, [onClose]);

  const take = async (name: string) => {
    if (!name) return;
    await onPick(name);
    // Stay open: filing usually happens in twos and threes, and re-opening the picker between each
    // one is the whole friction this control exists to remove.
    setText('');
  };

  // The list is open precisely while it has something to say, so it holds its last contents on the
  // way out - a list that empties as it fades is an empty box appearing, not a list leaving.
  const listOpen = suggestions.length > 0;
  const pop = usePop(listOpen);
  const shown = useHeld(suggestions, listOpen);

  const highlighted = suggestions[cursor];

  return (
    <span ref={boxRef} className="relative inline-flex">
      <input
        autoFocus
        value={text}
        disabled={disabled}
        placeholder="Type or pick a label…"
        onChange={e => setText(e.target.value)}
        onKeyDown={e => {
          if (e.key === 'Enter') {
            e.preventDefault();
            take(highlighted?.name ?? typed);
          } else if (e.key === 'Escape') {
            e.preventDefault();
            onClose();
          } else if (e.key === 'ArrowDown') {
            e.preventDefault();
            setCursor(c => Math.min(c + 1, suggestions.length - 1));
          } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setCursor(c => Math.max(c - 1, 0));
          } else if (e.key === 'Backspace' && !text) {
            onClose();
          }
        }}
        className="w-48 rounded border border-gray-600 bg-panel px-2 py-[3px] text-[11.5px] leading-none text-gray-200 placeholder-gray-600 focus:outline-none focus:border-gray-500"
      />
      {pop.show && (
        <span
          className="orbit-pop absolute left-0 top-full z-30 mt-1 w-56 overflow-hidden rounded-lg py-1"
          data-fx={pop.fx}
          // Near-opaque rather than the glass every panel here wears, and the same surface the
          // board's own move menu uses. A list that floats over another control has to be read
          // against that control: at panel alpha the priority buttons underneath showed through
          // the label names.
          style={{
            background: 'rgb(var(--w-panel-lit-rgb) / .97)',
            border: '1px solid rgb(var(--w-line-rgb) / .22)',
            boxShadow: '0 18px 44px -18px rgba(0,0,0,.95)',
          }}
        >
          {shown.map((s, i) => (
            <button
              key={s.id === -1 ? `new:${s.name}` : s.id}
              type="button"
              // Mouse down rather than click: the blur that a click fires first would close the
              // picker out from under the button being clicked.
              onMouseDown={e => { e.preventDefault(); take(s.name); }}
              onMouseEnter={() => setCursor(i)}
              className={`flex w-full items-center gap-2 px-2.5 py-1 text-left text-[11.5px] ${
                i === cursor ? 'bg-gray-800 text-gray-100' : 'text-gray-400'
              }`}
            >
              <span className="flex-1 truncate">{s.name}</span>
              {s.id === -1
                ? <span className="text-[10px] text-emerald-400/80">new</span>
                : <span className="text-[10px] text-gray-600">{s.count}</span>}
            </button>
          ))}
        </span>
      )}
    </span>
  );
}

/**
 * A ticket's labels as they appear on a board row — read-only, and quiet enough not to compete with
 * the phase chip beside them. Truncated rather than wrapped: the row is one glance, and a ticket
 * carrying nine labels must not be twice the height of one carrying none.
 */
export function LabelChips({ labels, max = 4 }: { labels: LabelRef[]; max?: number }) {
  if (labels.length === 0) return null;
  const shown = labels.slice(0, max);
  const hidden = labels.length - shown.length;
  return (
    <span
      className="flex items-center gap-1 min-w-0"
      title={labels.map(l => l.name).join(', ')}
    >
      {shown.map(l => (
        <span
          key={l.id}
          style={{
            font: `500 9.5px ${BODY}`,
            color: 'var(--w-t500)',
            background: 'rgb(var(--w-line-rgb) / .07)',
            border: '1px solid rgb(var(--w-line-rgb) / .12)',
            padding: '1px 5px',
            borderRadius: 3,
            whiteSpace: 'nowrap',
            overflow: 'hidden',
            textOverflow: 'ellipsis',
            maxWidth: 110,
          }}
        >
          {l.name}
        </span>
      ))}
      {hidden > 0 && (
        <span style={{ font: `500 9.5px ${BODY}`, color: 'var(--w-t600)', whiteSpace: 'nowrap' }}>
          +{hidden}
        </span>
      )}
    </span>
  );
}
