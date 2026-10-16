import { useCallback, useEffect, useState } from 'react';
import { GhostButton, Panel } from './ui';
import { BODY, MONO, TIER, chamfer } from './orbit';
import { label } from './stewardParts';
import { clearStewardFlag, getStewardOwnerFlags } from '../api/steward';
import type { StewardOwnerFlag } from '../api/steward';

/**
 * The flags on one ticket's change that only its owner can let through. An answer is prose and
 * cannot lift one - the steward's merge raises every flag afresh - so letting the steward merge
 * past a flag is its own press, and holds only for the change as it stands now.
 *
 * It is shown wherever the owner is sent to find it: under the question that asks about it, on
 * the Steward page for as long as the ticket waits in review, and on the ticket itself. Answering
 * the question is not the end of it, so the press cannot live only beside the question. Draws
 * nothing when there is no flag, or when the flags cannot be read (a project with no steward
 * answers that way too).
 */
export function StewardTicketFlags({ project, ticketId, title, framed, onFlags }: {
  project: string; ticketId: number;
  /** The ticket's title, for where the flags stand on their own rather than under its question. */
  title?: string;
  /** Stand as a card of its own, the way a question does, instead of inside one. */
  framed?: boolean;
  /** Hears the flags each time they are read, so a page can count what is still held. */
  onFlags?: (flags: StewardOwnerFlag[]) => void;
}) {
  const [flags, setFlags] = useState<StewardOwnerFlag[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const [failed, setFailed] = useState<string | null>(null);

  const load = useCallback(() => {
    let gone = false;
    getStewardOwnerFlags(project, ticketId)
      .catch(() => ({ flags: [] as StewardOwnerFlag[] }))
      .then(r => { if (!gone) { setFlags(r.flags); onFlags?.(r.flags); } });
    return () => { gone = true; };
    // onFlags is a listener, not an input: a new one each render must not re-read the flags.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [project, ticketId]);
  useEffect(load, [load]);

  const clear = async (flag: string) => {
    setBusy(flag); setFailed(null);
    try { await clearStewardFlag(project, ticketId, flag); load(); }
    catch (err) { setFailed(err instanceof Error ? err.message : String(err)); }
    finally { setBusy(null); }
  };

  if (flags.length === 0) return null;
  const holding = flags.some(f => !f.cleared);
  const body = (
    <div className="flex flex-col gap-1.5" style={{ marginTop: framed ? 0 : 12 }}>
      <div style={label()}>
        {holding ? `Holding #${ticketId}'s merge for you` : `#${ticketId}: cleared by you, for the steward to merge`}
        {title && <span style={{ textTransform: 'none', letterSpacing: 0, font: `400 12px ${BODY}`, color: 'var(--w-t300)' }}> - {title}</span>}
      </div>
      {flags.map(f => (
        <div
          key={f.flag}
          className="flex items-start justify-between gap-3 flex-wrap"
          style={{ clipPath: chamfer(8), padding: '8px 12px', border: '1px solid rgb(var(--w-line-rgb) / .14)' }}
        >
          {/* A basis of its own, so on a phone the button wraps under the words rather than
              squeezing them to a word a line. */}
          <div className="min-w-0 flex-[1_1_14rem]">
            <div style={{ font: `600 12px ${MONO}`, color: 'var(--w-t200)' }}>{f.flag}</div>
            {f.say && <div style={{ font: `400 12px/1.5 ${BODY}`, color: 'var(--w-t500)', marginTop: 2 }}>{f.say}</div>}
            {f.detail && f.detail.length > 0 && (
              <div className="break-all" style={{ font: `400 11px/1.5 ${MONO}`, color: 'var(--w-t500)', marginTop: 2 }}>
                {f.detail.join(', ')}
              </div>
            )}
          </div>
          {f.cleared
            ? <span style={label('var(--w-accent-text)')}>cleared for this change</span>
            : f.clearable && (
              <GhostButton tone="go" onClick={() => clear(f.flag)} disabled={busy !== null}
                title="The steward may merge past this flag. Any rework of the ticket asks again.">
                {busy === f.flag ? 'Clearing' : 'Let the steward merge past it'}
              </GhostButton>
            )}
        </div>
      ))}
      {failed && <div style={{ font: `400 11.5px ${BODY}`, color: TIER.blocked.c }}>{failed}</div>}
    </div>
  );
  return framed ? <Panel tone={TIER.blocked.c}>{body}</Panel> : body;
}
