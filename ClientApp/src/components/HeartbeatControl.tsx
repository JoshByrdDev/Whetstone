import { useState, useEffect, useCallback } from 'react';

interface HeartbeatStatus {
  running: boolean;
  scriptExists: boolean;
  /** Why heartbeat.ps1 refused the last start (a missing agent CLI, usually); null while running. */
  startupError: string | null;
  stopRequested: boolean;
  /** Agents still finishing their phase under a pending stop — 0 when nothing is being waited on. */
  draining: number;
}

export function HeartbeatControl() {
  const [status, setStatus] = useState<HeartbeatStatus | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetchStatus = useCallback(async () => {
    try {
      const res = await fetch('/api/heartbeat/status');
      if (!res.ok) throw new Error('Failed to check status');
      const data = await res.json();
      setStatus(data);
      setError(null);
    } catch {
      setError('Cannot reach server');
    }
  }, []);

  useEffect(() => {
    fetchStatus();
    const interval = setInterval(fetchStatus, 5000);
    return () => clearInterval(interval);
  }, [fetchStatus]);

  const handleStart = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch('/api/heartbeat/start', { method: 'POST' });
      const data = await res.json();
      if (!res.ok) setError(data.message);
      await fetchStatus();
    } catch {
      setError('Failed to start heartbeat');
    } finally {
      setLoading(false);
    }
  };

  const handleStop = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await fetch('/api/heartbeat/stop', { method: 'POST' });
      const data = await res.json();
      if (!res.ok) setError(data.message);
      await fetchStatus();
    } catch {
      setError('Failed to stop heartbeat');
    } finally {
      setLoading(false);
    }
  };

  const handleStopAfterCycle = async () => {
    setError(null);
    const endpoint = status?.stopRequested
      ? '/api/heartbeat/cancel-stop'
      : '/api/heartbeat/stop-after-cycle';
    try {
      const res = await fetch(endpoint, { method: 'POST' });
      if (!res.ok) {
        const data = await res.json();
        setError(data.message);
      }
      await fetchStatus();
    } catch {
      setError('Request failed');
    }
  };

  if (!status) return null;

  const isRunning = status.running;

  return (
    <div className="flex flex-wrap items-center gap-3">
      {!status.scriptExists ? (
        <span className="text-xs text-red-400">heartbeat.ps1 not found</span>
      ) : (
        <>
          <button
            onClick={isRunning ? handleStop : handleStart}
            disabled={loading}
            className={`btn-act flex items-center gap-2 px-4 py-2 disabled:opacity-50 ${
              isRunning ? 'bg-red-500 text-void' : 'bg-emerald-500 text-void'
            }`}
          >
            {loading ? (
              <svg className="w-3.5 h-3.5 animate-spin" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
              </svg>
            ) : isRunning ? (
              <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24">
                <rect x="6" y="4" width="4" height="16" rx="1" />
                <rect x="14" y="4" width="4" height="16" rx="1" />
              </svg>
            ) : (
              <svg className="w-3.5 h-3.5" fill="currentColor" viewBox="0 0 24 24">
                <path d="M8 5v14l11-7z" />
              </svg>
            )}
            {loading ? 'Working...' : isRunning ? 'Stop Heartbeat' : 'Start Heartbeat'}
          </button>
          {isRunning && (
            <button
              onClick={handleStopAfterCycle}
              className={`btn-act flex items-center gap-2 px-4 py-2 ${
                status.stopRequested ? 'bg-amber-500 text-void' : 'bg-gray-700 text-gray-300'
              }`}
            >
              <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z" />
              </svg>
              {status.stopRequested ? 'Cancel Pending Stop' : 'Stop After Cycle'}
            </button>
          )}
          {status.stopRequested && isRunning && (
            <span className="flex items-center gap-1.5 text-xs text-amber-400">
              <span className="inline-block h-2 w-2 rounded-full bg-amber-400 animate-pulse" />
              {status.draining > 0
                ? `Stopping - ${status.draining} agent${status.draining === 1 ? '' : 's'} finishing`
                : 'Stopping after current cycle'}
            </span>
          )}
          {/* Also when a stop is pending on a heartbeat that is NOT running: the drain indicator
              above needs liveness, so without this the status pill disappeared entirely and the
              row read as though the dashboard had nothing to say about the heartbeat at all. */}
          {(!status.stopRequested || !isRunning) && (
            <span className={`flex items-center gap-1.5 text-xs ${isRunning ? 'text-emerald-400' : 'text-gray-500'}`}>
              <span className={`inline-block h-2 w-2 rounded-full ${isRunning ? 'bg-emerald-400' : 'bg-gray-600'}`} />
              {isRunning ? 'Heartbeat Active' : 'Heartbeat Stopped'}
            </span>
          )}
        </>
      )}
      {error && <span className="text-xs text-red-400">{error}</span>}
      {/* The script's own refusal. It exits before it can tell anyone else, so without this a start
          that was refused looks exactly like a start that worked and then stopped. */}
      {!error && !isRunning && status.startupError && (
        <span className="min-w-0 break-words text-xs text-red-400">
          Refused to start: {status.startupError} (System → Setup check)
        </span>
      )}
    </div>
  );
}
