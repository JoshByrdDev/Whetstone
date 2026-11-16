import { useState } from 'react';
import { GhostButton } from './ui';
import { CHANNEL_LABEL, ORIGIN_LABEL, muteIntakeTicket } from '../api/intake';
import type { TicketDetail } from '../types/tickets';

const caps = 'text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500';

/**
 * Where a ticket came from, in the drawer (docs/intake-plan.md, section 6), and for one intake filed
 * the owner's "Never file this again": its issue is muted and the ticket closed. A REST route with
 * no tool behind it.
 */
export function IntakeOnTicket({ t, onChanged }: { t: TicketDetail; onChanged?: () => void }) {
  const [busy, setBusy] = useState(false);
  const [said, setSaid] = useState<string | null>(null);
  const origin = t.origin ?? 'owner';
  const intake = origin === 'intake' && t.intakeIssueId != null;

  const mute = async () => {
    if (!window.confirm('Never file this report again? Its later reports are counted on the Intake page and file nothing, and this ticket is closed.')) return;
    setBusy(true);
    try {
      const r = await muteIntakeTicket(t.id);
      setSaid(r.message);
      onChanged?.();
    } catch (e) {
      setSaid(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div>
      <div className={`${caps} mb-1.5`}>Origin</div>
      <div className="text-[13px] text-gray-300">
        {ORIGIN_LABEL[origin] ?? origin}
        {t.intakeChannel && <span className="text-gray-500"> · {CHANNEL_LABEL[t.intakeChannel] ?? t.intakeChannel}</span>}
        {t.regressionOf != null && <span className="text-gray-500"> · came back after #{t.regressionOf}</span>}
        {t.foundOn != null && origin === 'found-in-run' && <span className="text-gray-500"> · seen on #{t.foundOn}</span>}
      </div>
      {intake && t.phase !== 'Done' && (
        <div className="mt-2 flex min-w-0 flex-wrap items-center gap-2">
          <GhostButton tone="danger" disabled={busy} onClick={mute}
            title="Mute this report's issue: later reports are counted and never filed. Closes this ticket unless a run is working it.">
            Never file this again
          </GhostButton>
          {said && <span className="min-w-0 text-[11px] text-gray-500">{said}</span>}
        </div>
      )}
    </div>
  );
}
