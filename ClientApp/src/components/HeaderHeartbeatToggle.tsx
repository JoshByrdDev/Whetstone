import { useState, useEffect, useCallback } from 'react';

interface HeartbeatStatus {
  running: boolean;
  scriptExists: boolean;
  stopRequested: boolean;
  /** Agents still finishing their phase under a pending stop — 0 when nothing is being waited on. */
  draining: number;
}

/**
 * Icon-only heartbeat controls for the app bar. Same endpoints as the full
 * HeartbeatControl on the Heartbeat tab, condensed to icon buttons: a single
 * start button when stopped; hard-stop + stop-after-cycle when running.
 */
export function HeaderHeartbeatToggle() {
  const [status, setStatus] = useState<HeartbeatStatus | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetchStatus = useCallback(async () => {
    try {
      const res = await fetch('/api/heartbeat/status');
      if (!res.ok) throw new Error();
      setStatus(await res.json());
    } catch {
      setStatus(null);
    }
  }, []);

  useEffect(() => {
    fetchStatus();
    const interval = setInterval(fetchStatus, 5000);
    return () => clearInterval(interval);
  }, [fetchStatus]);

  if (!status || !status.scriptExists) return null;

  const isRunning = status.running;

  const call = async (endpoint: string, failMsg: string) => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch(endpoint, { method: 'POST' });
      if (!res.ok) {
        const data = await res.json().catch(() => null);
        setError(data?.message ?? failMsg);
      }
      await fetchStatus();
    } catch {
      setError(failMsg);
    } finally {
      setLoading(false);
    }
  };

  const btnBase =
    'flex items-center justify-center w-7 h-7 rounded-md border transition-colors disabled:opacity-50';
  const spinner = (
    <svg className="w-3.5 h-3.5 animate-spin" fill="none" viewBox="0 0 24 24">
      <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
      <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
    </svg>
  );

  if (!isRunning) {
    return (
      <button
        onClick={() => call('/api/heartbeat/start', 'Failed to start heartbeat')}
        disabled={loading}
        title={error ?? 'Start heartbeat'}
        className={`${btnBase} ${
          error
            ? 'border-red-500/60 text-red-400 hover:bg-red-500/10'
            : 'border-hair text-emerald-400 hover:bg-emerald-500/10 hover:border-emerald-500/60'
        }`}
      >
        {loading ? spinner : (
          <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24">
            <path d="M8 5v14l11-7z" />
          </svg>
        )}
      </button>
    );
  }

  return (
    <div className="flex items-center gap-1.5">
      <button
        onClick={() => call('/api/heartbeat/stop', 'Failed to stop heartbeat')}
        disabled={loading}
        title={error ?? 'Stop heartbeat now'}
        className={`${btnBase} ${
          error
            ? 'border-red-500/60 text-red-400 hover:bg-red-500/10'
            : 'border-hair text-red-400 hover:bg-red-500/10 hover:border-red-500/60'
        }`}
      >
        {loading ? spinner : (
          <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 24 24">
            <rect x="5" y="5" width="14" height="14" rx="2" />
          </svg>
        )}
      </button>
      <button
        onClick={() =>
          call(
            status.stopRequested ? '/api/heartbeat/cancel-stop' : '/api/heartbeat/stop-after-cycle',
            'Request failed',
          )
        }
        disabled={loading}
        title={
          status.stopRequested
            ? status.draining > 0
              ? `Cancel pending stop (${status.draining} agent${status.draining === 1 ? '' : 's'} still finishing)`
              : 'Cancel pending stop'
            : 'Stop after current cycle'
        }
        className={`${btnBase} ${
          status.stopRequested
            ? 'border-amber-500/60 text-amber-400 bg-amber-500/10 hover:bg-amber-500/20'
            : 'border-hair text-gray-400 hover:text-amber-400 hover:bg-amber-500/10 hover:border-amber-500/60'
        }`}
      >
        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
        </svg>
      </button>
    </div>
  );
}
