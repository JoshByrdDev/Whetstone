import type { Priority } from '../types/tickets';
import { PRIORITIES, PRIORITY_HINT, PRIORITY_LEVEL, PRIORITY_MARK, PRIORITY_COLOR } from '../types/tickets';

/**
 * The five priority bands as one row of segments. Shared by the create modal (local state until
 * submit) and the ticket drawer (persists on change) — the same arrangement TicketOptions uses.
 *
 * All five are drawn at once rather than hidden behind a `<select>` on purpose. Priority is a
 * position in a line, and a control that shows one value at a time cannot say where in the line you
 * are: picking "High" tells you nothing about what it is high compared to. The hint underneath is
 * for the same reason — every band's meaning is a claim about what happens to the OTHER tickets.
 */
export function PriorityPicker({ value, disabled, onChange }: {
  value: Priority;
  disabled?: boolean;
  onChange: (p: Priority) => void;
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <div className="flex gap-1">
        {PRIORITIES.map(p => {
          const on = p === value;
          return (
            <button
              key={p}
              type="button"
              disabled={disabled}
              onClick={() => onChange(p)}
              title={PRIORITY_HINT[p]}
              className={`flex-1 min-w-0 rounded border px-1.5 py-1.5 transition-colors disabled:cursor-default ${
                on
                  ? 'border-transparent bg-white/[.07]'
                  : 'border-hair bg-transparent hover:bg-white/[.03]'
              }`}
              style={on ? { borderColor: PRIORITY_COLOR[p], color: PRIORITY_COLOR[p] } : undefined}
            >
              <span className={`block text-[11px] leading-none ${on ? '' : 'text-gray-600'}`}>
                {PRIORITY_MARK[p]}
              </span>
              <span className={`block mt-1 text-[10.5px] leading-none truncate ${on ? 'font-semibold' : 'text-gray-500'}`}>
                {p}
              </span>
            </button>
          );
        })}
      </div>
      {/* Which one is selected is already obvious; what it costs the rest of the board is not. */}
      <div className="text-[11px] leading-snug text-gray-600">
        <span className="font-mono text-gray-500">P{PRIORITY_LEVEL[value]}</span> · {PRIORITY_HINT[value]}
      </div>
    </div>
  );
}
