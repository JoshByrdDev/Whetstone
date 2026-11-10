import { useState, useEffect, useCallback, useRef } from 'react';
import { usePop } from '../hooks/usePop';
import { ConfirmStopModal } from './ConfirmStopModal';

interface HeartbeatStatus {
  running: boolean;
  scriptExists: boolean;
  /** Why heartbeat.ps1 refused the last start (a missing agent CLI, usually); null while running. */
  startupError: string | null;
  stopRequested: boolean;
  /** Agents still finishing their phase under a pending stop — 0 when nothing is being waited on. */
  draining: number;
  /** A restart-after-drain in hand (see BoardRestart on the server); null when there is none. */
  restart: BoardRestart | null;
}

interface BoardRestart {
  phase: 'draining' | 'restarting' | 'starting' | 'resuming' | 'failed';
  resume: boolean;
  error: string | null;
}

const RESTART_SAYS: Record<BoardRestart['phase'], string> = {
  draining: 'waiting for the running agents to finish',
  restarting: 'stopping Whetstone',
  starting: 'starting Whetstone',
  resuming: 'starting the heartbeat again',
  failed: 'failed',
};

const restartIcon = (
  <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
    <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h5M20 20v-5h-5M5.1 15a7 7 0 0011.9 2.6L20 15M4 9l3-2.6A7 7 0 0118.9 9" />
  </svg>
);

/**
 * Icon-only heartbeat controls for the app bar. Same endpoints as the full
 * HeartbeatControl on the Heartbeat tab, condensed to icon buttons: a single
 * start button when stopped; hard-stop + stop-after-cycle when running, and beside them a menu
 * with restart-after-drain (POST /api/heartbeat/restart-after-drain).
 */
export function HeaderHeartbeatToggle() {
  const [status, setStatus] = useState<HeartbeatStatus | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [menuOpen, setMenuOpen] = useState(false);
  const [confirmStop, setConfirmStop] = useState(false);
  const menu = usePop(menuOpen);
  const menuRef = useRef<HTMLDivElement>(null);

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

  useEffect(() => {
    if (!menuOpen) return;
    const close = (e: MouseEvent) => {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) setMenuOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [menuOpen]);

  if (!status || !status.scriptExists) return null;

  const isRunning = status.running;
  // A refusal from the script itself counts as an error here too: it is why the button is back.
  const problem = error ?? (!isRunning && status.startupError ? `Refused to start: ${status.startupError}` : null);

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

  const restart = status.restart;
  if (restart && restart.phase !== 'draining' && restart.phase !== 'failed') {
    return (
      <span
        title={`Restarting Whetstone: ${RESTART_SAYS[restart.phase]}`}
        className={`${btnBase} border-amber-500/60 text-amber-400 bg-amber-500/10`}
      >
        {spinner}
      </span>
    );
  }

  // A failed restart says so until it is dismissed: the board came back (or this would not be
  // showing), but something after that did not happen.
  const failedRestart = restart?.phase === 'failed' && (
    <button
      onClick={() => call('/api/heartbeat/cancel-stop', 'Could not clear the failed restart')}
      disabled={loading}
      title={`Restart failed: ${restart.error ?? 'see data/logs/restart-board.log'} (click to dismiss)`}
      className={`orbit-tap ${btnBase} border-red-500/60 text-red-400 hover:bg-red-500/10`}
    >
      {restartIcon}
    </button>
  );

  if (!isRunning) {
    if (failedRestart) return <div className="flex items-center gap-1.5">{failedRestart}</div>;
    return (
      <button
        onClick={() => call('/api/heartbeat/start', 'Failed to start heartbeat')}
        disabled={loading}
        title={problem ?? 'Start heartbeat'}
        className={`${btnBase} ${
          problem
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
        onClick={() => setConfirmStop(true)}
        disabled={loading}
        title={error ?? 'Stop heartbeat and every worker now'}
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
            ? `Cancel pending stop${restart?.phase === 'draining' ? ' and restart' : ''}${
                status.draining > 0
                  ? ` (${status.draining} agent${status.draining === 1 ? '' : 's'} still finishing)`
                  : ''
              }`
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
      {failedRestart}
      {/* More ways to stop. One today: stop, restart the board, and start again. */}
      <div className="relative" ref={menuRef}>
        <button
          onClick={() => setMenuOpen(v => !v)}
          disabled={loading}
          aria-haspopup="menu"
          aria-expanded={menuOpen}
          title={restart?.phase === 'draining'
            ? `Restart pending: ${RESTART_SAYS.draining}`
            : 'More ways to stop'}
          className={`orbit-tap flex items-center justify-center w-4 h-7 rounded-md border transition-colors disabled:opacity-50 ${
            restart?.phase === 'draining'
              ? 'border-amber-500/60 text-amber-400 bg-amber-500/10'
              : 'border-hair text-gray-500 hover:text-gray-200 hover:border-gray-500'
          }`}
        >
          <svg className="w-2.5 h-2.5" fill="currentColor" viewBox="0 0 24 24">
            <path d="M6 9l6 6 6-6z" />
          </svg>
        </button>
        {menu.show && (
          <div
            role="menu"
            className="orbit-pop absolute right-0 z-30 mt-1 w-64 max-w-[calc(100vw-2rem)] rounded-md border border-hair bg-panel/[0.98] backdrop-blur-sm py-1 shadow-lg shadow-black/60"
            data-fx={menu.fx}
          >
            {restart?.phase === 'draining' ? (
              <button
                role="menuitem"
                onClick={() => { setMenuOpen(false); call('/api/heartbeat/cancel-stop', 'Could not cancel the restart'); }}
                className="orbit-pick block w-full px-3 py-2 text-left text-[12px] text-gray-200 hover:bg-gray-800"
              >
                Cancel the restart and the pending stop
                <span className="block text-[11px] text-gray-500">
                  {status.draining > 0
                    ? `${status.draining} agent${status.draining === 1 ? '' : 's'} still finishing; the heartbeat keeps going.`
                    : 'The heartbeat keeps going.'}
                </span>
              </button>
            ) : (
              <button
                role="menuitem"
                onClick={() => { setMenuOpen(false); call('/api/heartbeat/restart-after-drain', 'Could not start the restart'); }}
                className="orbit-pick flex w-full items-start gap-2 px-3 py-2 text-left text-[12px] text-gray-200 hover:bg-gray-800"
              >
                <span className="mt-0.5 text-amber-400">{restartIcon}</span>
                <span className="min-w-0">
                  Stop, restart Whetstone, resume
                  <span className="block text-[11px] text-gray-500">
                    Sends out no new work, waits for the running agents to finish, restarts the board,
                    then starts the heartbeat again.
                  </span>
                </span>
              </button>
            )}
          </div>
        )}
      </div>
      {confirmStop && (
        <ConfirmStopModal
          onConfirm={() => call('/api/heartbeat/stop', 'Failed to stop heartbeat')}
          onClose={() => setConfirmStop(false)}
        />
      )}
    </div>
  );
}
