import { PRIORITIES } from '../types/tickets';
import type { Priority } from '../types/tickets';
import { useHeartbeatAllowSet } from './useHeartbeatAllowSet';

/**
 * Which priority bands the one global heartbeat is allowed to claim from.
 *
 * The sibling of {@link useHeartbeatProjects}, sharing its rule set through
 * {@link useHeartbeatAllowSet}: stored as `heartbeatPriorities`, empty means all five, one band
 * always stays on duty, and the claim re-reads it every cycle so a change lands at the next pickup.
 *
 * Worth being clear about what this is NOT, because the board already has a priority control and
 * they answer different questions. A ticket's own priority says where in the queue it sits — the
 * claim works the highest band that has anything in it, then the next. This says which bands the
 * fleet may touch at all: switch Normal off and its tickets stop being candidates rather than
 * merely being outranked. Nothing is blocked, nothing is moved, and they become eligible again the
 * moment the band comes back on.
 *
 * The universe is a fixed five, so unlike projects there is no "one added later" to inherit the
 * empty-list default — it earns its keep here by meaning the stored config can stay untouched
 * until somebody actually narrows the fleet.
 */
export function useHeartbeatPriorities() {
  const set = useHeartbeatAllowSet({ configKey: 'heartbeatPriorities', universe: PRIORITIES });

  return {
    ...set,
    /** Typed lookup, so callers can map over {@link PRIORITIES} without casting back to string. */
    isOn: (p: Priority) => set.selected.has(p),
  };
}
