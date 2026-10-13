import { useEffect, useState } from 'react';
import { Markdown } from './Markdown';
import { getStewardEntriesForTicket } from '../api/steward';
import type { StewardEntry } from '../api/steward';

const RELEASE_WORD: Record<string, string> = {
  Migration: 'Before a release: a migration',
  Config: 'Before a release: to set up',
  Action: 'Before a release: yours to do',
  Note: 'Before a release: to know',
};

/**
 * What the project's steward decided about this ticket, and what it leaves for a release.
 *
 * These lines are written for the ticket's owner to read back and reverse, and the place somebody
 * goes to ask "why was this merged like that" is the ticket, not a list of every call on every
 * ticket. Draws nothing for a ticket no steward has touched, which is most projects' every ticket.
 */
export function StewardOnTicket({ project, ticketId }: { project: string; ticketId: number }) {
  const [rows, setRows] = useState<StewardEntry[]>([]);

  useEffect(() => {
    let gone = false;
    setRows([]);
    // Nothing to say when it cannot be read: the drawer is not the place to report on the steward.
    getStewardEntriesForTicket(project, ticketId).then(r => { if (!gone) setRows(r.filter(e => e.ticketId === ticketId)); }).catch(() => {});
    return () => { gone = true; };
  }, [project, ticketId]);

  if (rows.length === 0) return null;
  return (
    <div>
      <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500 mb-2.5">From the steward</div>
      <div className="flex flex-col gap-2">
        {[...rows].reverse().map(e => (
          <div key={e.id} className="rounded-lg border border-hair px-3 py-2.5" style={{ opacity: e.handledAt ? 0.6 : 1 }}>
            <div className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500 mb-1">
              {e.kind === 'Call' ? 'A call it made for you' : RELEASE_WORD[e.release ?? 'Note']}
              {e.handledAt ? ' - ticked off' : ''}
            </div>
            <Markdown text={e.text} />
          </div>
        ))}
      </div>
    </div>
  );
}
