import { useCallback, useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { usePop, useHeld } from '../hooks/usePop';
import { checkoutTicket, getTicketRepos } from '../api/git';
import type { CheckoutResult, RepoTouch } from '../api/git';
import type { ProjectRepo } from '../types/tickets';
import { PrModal } from './PrModal';
import { MultiRepoPrModal } from './MultiRepoPrModal';
import { CheckinModal } from './CheckinModal';
import { deepen } from './orbit';

/**
 * The two git moves a person makes on a ticket without leaving the board: put my checkout on this
 * branch, and raise its PR.
 *
 * Rendered only when the ticket has a branch — before speculation there is nothing to check out and
 * nothing to raise, and a permanently disabled pair of buttons on every backlog row is worse than
 * no buttons at all.
 *
 * Checkout answers inline rather than in a dialog. Its two interesting outcomes are "done, and by
 * the way the agent also holds this branch" and "no, you have uncommitted work" — both are a
 * sentence, and neither is worth a modal.
 */
export function TicketGitActions({ ticketId, branch, size = 'card', track = 'Delivery', repoType = 'git', merge = false, repos }: {
  ticketId: number;
  branch?: string | null;
  /** "row" is the compact in-flight row; "card" is the expanded waiting-on-you card. */
  size?: 'row' | 'card';
  /**
   * On the Review track the branch belongs to someone else, so the PR button is not offered: this
   * dashboard does not raise, rebase or force-push other people's work. Hiding it is the courtesy;
   * the actual guarantee is the server refusing all three (see GitEndpoints.RefuseWriteOnReview),
   * because a button nobody can see is not a rule.
   *
   * Checkout stays. Putting your own clone on their branch to read it is the thing a reviewer
   * actually wants, and it writes to nothing but your own working directory.
   */
  track?: 'Delivery' | 'Review';
  /**
   * "tfvc" means the hub is also a TFVC workspace, and that changes both buttons. Checkout goes: it
   * would put the hub itself on the ticket's branch, and TFVC would see the whole branch as pending
   * edits (the server refuses it too). The PR button becomes check-in, which merges into the hub
   * without committing and waits for a human to check the result in from Team Explorer.
   */
  repoType?: 'git' | 'tfvc';
  /** The project merges into its base rather than raising PRs; the same dialog then ends in a merge. */
  merge?: boolean;
  /**
   * A project of several repos (docs/multi-repo-plan.md). Checkout puts each writable repo's own
   * checkout on the branch, the PR button opens one section per repo, and a badge says how many of
   * them the ticket has changed. Absent for a project of one, which is everything above unchanged.
   */
  repos?: ProjectRepo[] | null;
}) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<CheckoutResult | null>(null);
  const [failure, setFailure] = useState<string | null>(null);
  const [each, setEach] = useState<RepoCheckout[] | null>(null);
  const [prOpen, setPrOpen] = useState(false);
  const [touch, setTouch] = useState<RepoTouch[] | null>(null);
  const anchor = useRef<HTMLDivElement>(null);
  const several = !!repos && repos.length > 0;

  // Local reads, once per ticket and again when its dialog closes - not on every board poll, which
  // would be every repo's git, for every ticket of the project, every few seconds.
  const readTouch = useCallback(() => {
    if (!several || !branch) return;
    getTicketRepos(ticketId).then(setTouch).catch(() => setTouch(null));
  }, [several, branch, ticketId]);
  useEffect(() => { readTouch(); }, [readTouch]);

  if (!branch) return null;

  const pad = size === 'row' ? 'px-1 py-0.5' : 'px-2 py-1.5';
  const icon = size === 'row' ? 13 : 14;

  async function checkout(e: React.MouseEvent) {
    e.stopPropagation();
    setBusy(true); setResult(null); setFailure(null); setEach(null);
    try {
      if (several) {
        // One repo after another, each its own refusal: uncommitted work in one stops only that one.
        const out: RepoCheckout[] = [];
        for (const r of repos!.filter(x => !x.readOnly)) {
          try { out.push({ dir: r.dir, result: await checkoutTicket(ticketId, r.dir) }); }
          catch (err) { out.push({ dir: r.dir, failure: err instanceof Error ? err.message : 'Checkout failed.' }); }
        }
        setEach(out);
      }
      else setResult(await checkoutTicket(ticketId));
    } catch (err) {
      setFailure(err instanceof Error ? err.message : 'Checkout failed.');
    } finally {
      setBusy(false);
    }
  }

  const tfvc = repoType === 'tfvc';
  const changed = touch?.filter(t => t.touched) ?? [];

  return (
    <div ref={anchor} className="flex items-center gap-0.5 relative">
      {several && touch && (
        <span
          className="px-1 text-[10px] font-mono text-gray-500 whitespace-nowrap"
          title={touch.map(t => t.readOnly ? `${t.dir}: read-only`
            : `${t.dir}: ${t.touched ? `+${t.ahead} / -${t.behind}${t.dirty ? `, ${t.dirty} uncommitted` : ''}` : 'not changed'}`).join('\n')}
        >
          {changed.length}/{touch.filter(t => !t.readOnly).length} repos
        </span>
      )}
      {!tfvc && (
        <button
          onClick={checkout}
          disabled={busy}
          title={`Check out ${branch} in your own repo`}
          className={`${pad} rounded text-gray-500 hover:text-sky-300 hover:bg-gray-700 disabled:opacity-40 transition-colors`}
        >
          <BranchIcon size={icon} spin={busy} />
        </button>
      )}
      {track !== 'Review' && (
        <button
          onClick={e => { e.stopPropagation(); setPrOpen(true); }}
          title={tfvc ? `Check ${branch} in to TFVC` : merge ? `Merge ${branch} into its base` : `Raise a pull request for ${branch}`}
          className={`${pad} rounded text-gray-500 hover:text-emerald-300 hover:bg-gray-700 transition-colors`}
        >
          {tfvc ? <CheckinIcon size={icon} /> : <PrIcon size={icon} />}
        </button>
      )}

      {/* Always rendered, so that the note leaving is something you can watch: it is `open` that
          goes false, and the note itself holds the last few frames. */}
      <CheckoutNote
        open={!!(result || failure || each)}
        anchor={anchor}
        result={result}
        failure={failure}
        each={each}
        onClose={() => { setResult(null); setFailure(null); setEach(null); }}
      />

      {prOpen && (tfvc
        ? <CheckinModal ticketId={ticketId} onClose={() => setPrOpen(false)} />
        : several
          ? <MultiRepoPrModal ticketId={ticketId} onClose={() => { setPrOpen(false); readTouch(); }} />
          : <PrModal ticketId={ticketId} onClose={() => setPrOpen(false)} />)}
    </div>
  );
}

/** One repo's checkout, on a project of several. */
interface RepoCheckout {
  dir: string;
  result?: CheckoutResult;
  failure?: string;
}

const NOTE_W = 320;

/**
 * What the checkout did, anchored under the buttons. Through a portal in fixed coordinates for the
 * same reason the phase menu is: every card clips itself for its rounded corners, and the board
 * body scrolls horizontally, so an in-flow popover gets cut off at the card's edge.
 */
function CheckoutNote({ anchor, open, result: live, failure: liveFailure, each: liveEach, onClose }: {
  anchor: React.RefObject<HTMLDivElement>;
  open: boolean;
  result: CheckoutResult | null;
  failure: string | null;
  each: RepoCheckout[] | null;
  onClose: () => void;
}) {
  const { show, fx } = usePop(open);
  // Held while it leaves, so the note fades out still saying what it said rather than emptying
  // itself first. The parent clears both the moment it is dismissed.
  const { result, failure, each } = useHeld({ result: live, failure: liveFailure, each: liveEach }, open);
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);

  useEffect(() => {
    if (!show) return;
    const place = () => {
      const box = anchor.current?.getBoundingClientRect();
      if (!box) return;
      setPos({
        left: Math.max(8, Math.min(box.right - NOTE_W, window.innerWidth - NOTE_W - 8)),
        top: box.bottom + 6,
      });
    };
    place();
    window.addEventListener('scroll', place, true);
    window.addEventListener('resize', place);
    return () => {
      window.removeEventListener('scroll', place, true);
      window.removeEventListener('resize', place);
    };
  }, [anchor, show]);

  // A success is a confirmation nobody needs to dismiss; a refusal has a list to read, so it stays.
  const bad = !!failure || result?.ok === false || !!each?.some(e => e.failure || e.result?.ok === false);
  useEffect(() => {
    if (!open || bad) return;
    const id = setTimeout(onClose, 6000);
    return () => clearTimeout(id);
  }, [open, bad, onClose]);

  if (!show) return null;

  // The tone is a tint, not a surface. The theme's 700-950 shades are translucent washes, meant to
  // sit on a panel that is already under them; this note is a portal over the board, and there is
  // nothing under it but the card it is describing. So it lays its own floor: the tier colour taken
  // most of the way to the void, which comes out opaque.
  const tier = bad ? 'var(--w-blocked)' : 'var(--w-done)';
  const edge = bad ? 'border-red-700' : 'border-emerald-700';

  return createPortal(
    <div
      style={{
        position: 'fixed', left: pos?.left ?? -9999, top: pos?.top ?? -9999, width: NOTE_W,
        background: deepen(tier, 14),
      }}
      className={`orbit-pop z-50 rounded-lg border shadow-xl px-3 py-2.5 text-xs ${edge}`}
      data-fx={fx}
      onClick={e => e.stopPropagation()}
    >
      <div className="flex items-start gap-2">
        <div className="flex-1 min-w-0 space-y-1.5">
          {failure && <div className="text-red-300">{failure}</div>}

          {each?.map(e => (
            <div key={e.dir} className="min-w-0">
              <span className="font-mono text-gray-300">{e.dir}</span>{' '}
              {e.failure ? <span className="text-red-300">{e.failure}</span>
                : e.result?.ok ? (
                  <span className="text-emerald-300">
                    on <span className="font-mono">{e.result.branch}</span>
                    {e.result.createdFromRemote && ' (created from origin)'}
                    {e.result.forced && <span className="text-amber-400"> - an agent's worktree holds it too</span>}
                  </span>
                )
                : <span className="text-red-300">{e.result?.error}</span>}
              {e.result && !e.result.ok && e.result.pending.length > 0 && (
                <div className="font-mono text-[10px] text-gray-500">{e.result.pending.length} uncommitted file(s) in it</div>
              )}
            </div>
          ))}

          {result?.ok === false && (
            <>
              <div className="text-red-300">{result.error}</div>
              {result.pending.length > 0 && (
                <div className="max-h-32 overflow-auto font-mono text-[10px] text-gray-400 space-y-0.5">
                  {result.pending.slice(0, 20).map(c => (
                    <div key={c.path} className="truncate">
                      <span className="text-amber-500">{c.status || '??'}</span> {c.path}
                    </div>
                  ))}
                  {result.pending.length > 20 && (
                    <div className="text-gray-600">+{result.pending.length - 20} more</div>
                  )}
                </div>
              )}
            </>
          )}

          {result?.ok && (
            <>
              <div className="text-emerald-300">
                You're on <span className="font-mono">{result.branch}</span>
                {result.createdFromRemote && ' (created from origin)'}.
              </div>
              {result.forced && result.sharedWith && (
                <div className="text-amber-400 leading-relaxed">
                  An agent's worktree holds this branch too — it may commit under you. Its checkout:
                  <div className="font-mono text-[10px] text-gray-500 mt-0.5 break-all">{result.sharedWith}</div>
                </div>
              )}
              <div className="font-mono text-[10px] text-gray-600 break-all">{result.repoPath}</div>
            </>
          )}
        </div>
        <button onClick={onClose} className="text-gray-600 hover:text-gray-300 flex-none leading-none">✕</button>
      </div>
    </div>,
    document.body,
  );
}

// ---- icons ----

function BranchIcon({ size, spin }: { size: number; spin: boolean }) {
  return (
    <svg
      width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={2} strokeLinecap="round" strokeLinejoin="round"
      className={spin ? 'animate-spin' : undefined}
    >
      <circle cx="6" cy="6" r="2.5" />
      <circle cx="6" cy="18" r="2.5" />
      <circle cx="18" cy="7" r="2.5" />
      <path d="M6 8.5v7" />
      <path d="M18 9.5c0 4-5 3.5-6.5 6" />
    </svg>
  );
}

/** A tray with an arrow going into it: handing the change over, rather than proposing it. */
function CheckinIcon({ size }: { size: number }) {
  return (
    <svg
      width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={2} strokeLinecap="round" strokeLinejoin="round"
    >
      <path d="M12 3v11" />
      <path d="m7.5 9.5 4.5 4.5 4.5-4.5" />
      <path d="M4 14v4.5A1.5 1.5 0 0 0 5.5 20h13a1.5 1.5 0 0 0 1.5-1.5V14" />
    </svg>
  );
}

function PrIcon({ size }: { size: number }) {
  return (
    <svg
      width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor"
      strokeWidth={2} strokeLinecap="round" strokeLinejoin="round"
    >
      <circle cx="6" cy="18" r="2.5" />
      <circle cx="6" cy="6" r="2.5" />
      <circle cx="18" cy="18" r="2.5" />
      <path d="M6 8.5v7" />
      <path d="M18 15.5V10a3 3 0 0 0-3-3h-3" />
      <path d="M14.5 4.5 12 7l2.5 2.5" />
    </svg>
  );
}
