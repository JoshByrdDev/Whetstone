import { useState, useEffect, useCallback, useMemo } from 'react';

/**
 * One switch, two axes: which projects the heartbeat may claim from, and which priority bands.
 *
 * Both are stored in global config as a string array, both are re-read by the claim endpoint on
 * every cycle (so a change takes effect at the next pickup with no restart), and both share the
 * convention that makes them safe to leave alone: an empty stored list means EVERYTHING. That is
 * what lets a project added next month become eligible without anyone remembering to tick it, and
 * it is why a config file nobody has ever written works the whole board.
 *
 * Extracted rather than written twice because the interesting parts are not the fetch — they are
 * the empty-list rule, the last-one-standing rule, and the rollback, all three of which are the
 * kind of thing that goes subtly out of step between two copies and is then wrong in only one
 * place. See {@link useHeartbeatProjects} and {@link useHeartbeatPriorities}.
 */
export function useHeartbeatAllowSet({ configKey, universe }: {
  /** The global-config key holding the stored allow-list. */
  configKey: string;
  /** Every value that could be chosen, in display order. The stored list is filtered against it. */
  universe: readonly string[];
}) {
  // The stored list, not the derived selection, and null until the config has actually been read:
  // a control that renders before then would flash "all of them" at somebody who picked one.
  const [configured, setConfigured] = useState<string[] | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const cfg = await fetch('/api/config').then(r => r.json());
        if (cancelled) return;
        setConfigured(Array.isArray(cfg[configKey]) ? cfg[configKey] : []);
      } catch {
        // Leave it unloaded. The control hides itself rather than showing a default it has not read.
      }
    })();
    return () => { cancelled = true; };
  }, [configKey]);

  // Derived every render, never stored: the config and the universe can arrive independently (the
  // Board's project list loads separately), and a selection frozen when the first of them landed
  // would be wrong about the second. Filtering through `universe` also quietly drops a stored name
  // that no longer exists, so an unregistered project cannot haunt the count.
  const selected = useMemo(() => {
    if (configured === null) return new Set<string>();
    if (configured.length === 0) return new Set(universe);
    return new Set(universe.filter(v => configured.includes(v)));
  }, [configured, universe]);

  const toggle = useCallback(async (value: string) => {
    if (configured === null) return;

    const next = new Set(selected);
    if (next.has(value)) {
      // One always stays on duty. A heartbeat allowed to work nothing is a stopped heartbeat
      // wearing a running one's badge; Stop is the honest way to say that.
      if (next.size <= 1) return;
      next.delete(value);
    } else {
      next.add(value);
    }

    // All of them => store the empty list, so anything added later joins on its own.
    const store = universe.length > 0 && next.size === universe.length ? [] : [...next];

    // Moved under the finger, not after the round trip — then put back if the save did not land.
    // This setting decides what gets worked while you are not watching, so a switch that reads
    // "off" while the orchestrator reads "on" is the one failure worth undoing.
    const previous = configured;
    setConfigured(store);
    setSaving(true);
    setError(null);
    try {
      const res = await fetch('/api/config', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ [configKey]: store }),
      });
      if (!res.ok) throw new Error('save failed');
    } catch {
      setConfigured(previous);
      setError('Not saved');
    } finally {
      setSaving(false);
    }
  }, [configured, selected, universe, configKey]);

  return {
    selected,
    toggle,
    saving,
    error,
    /** The config has been read; until then there is no honest state to draw. */
    loaded: configured !== null,
    allSelected: universe.length > 0 && selected.size === universe.length,
  };
}
