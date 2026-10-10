import { useState } from 'react';
import type { ReactNode } from 'react';
import type { QaShot, ReviewComment, ReviewSeverity, ReviewVerdict, TicketDetail } from '../types/tickets';
import * as api from '../api/tickets';
import { Markdown } from './LearningBrief';
import { useShotLightbox } from './ShotLightbox';

/**
 * The peer-review drawer: the branch under review, what a run staged against it, and the one action
 * that sends any of it anywhere.
 *
 * The shape follows the one asymmetry this whole track is built on. Everything a run wrote is a
 * DRAFT — it lives on the local ticket and reaches nobody until the person reading this decides it
 * should. So the panel is a curation surface first and a report second: every comment can be
 * reworded, downgraded, or thrown away, and "Copy for the PR" is the only door out.
 */

const SEVERITY_ORDER: ReviewSeverity[] = ['Blocking', 'Question', 'Nit', 'Praise'];

const SEVERITY_STYLE: Record<ReviewSeverity, { label: string; chip: string; dot: string }> = {
  Blocking: { label: 'Blocking', chip: 'text-rose-300 border-rose-800/70 bg-rose-950/30', dot: 'bg-rose-400' },
  Question: { label: 'Question', chip: 'text-amber-300 border-amber-800/70 bg-amber-950/30', dot: 'bg-amber-400' },
  Nit: { label: 'Nit', chip: 'text-sky-300 border-sky-800/70 bg-sky-950/30', dot: 'bg-sky-400' },
  Praise: { label: 'Praise', chip: 'text-emerald-300 border-emerald-800/70 bg-emerald-950/30', dot: 'bg-emerald-400' },
};

const VERDICT_LABEL: Record<ReviewVerdict, string> = {
  Approve: 'Approve',
  ApproveWithComments: 'Approve with comments',
  RequestChanges: 'Request changes',
  CannotTell: 'Cannot tell',
};

function Label({ children }: { children: ReactNode }) {
  return (
    <div className="text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500 mb-2.5">
      {children}
    </div>
  );
}

function short(sha?: string | null) {
  return sha ? sha.slice(0, 8) : null;
}

export function ReviewPanel({ t, onChanged }: {
  t: TicketDetail;
  onChanged: () => void;
}) {
  const review = t.review;
  const [busy, setBusy] = useState(false);
  const [copied, setCopied] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // One lightbox for the whole panel rather than one per comment, and it is handed the ticket's
  // WHOLE shot set rather than the cited one. Opening from a comment lands on that comment's shot,
  // and the arrows then walk the rest — which is the correlation this section is for: the shot the
  // finding points at, in the sequence QA took it in.
  const { open: openShot, lightbox } = useShotLightbox(t.qaShots);

  if (!review) return null;

  const comments = t.reviewComments ?? [];
  // Dismissed comments stay in the list but sit at the bottom, struck through. Kept rather than
  // deleted because a run that keeps re-raising a point you already killed is worth being able to
  // see — and because "I threw that away" is a decision, not an absence.
  const live = comments.filter(c => c.status !== 'Dismissed');
  const dismissed = comments.filter(c => c.status === 'Dismissed');
  const sent = review.submittedAt != null;

  async function run(fn: () => Promise<unknown>) {
    setBusy(true);
    setError(null);
    try {
      await fn();
      onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : 'That did not work.');
    } finally {
      setBusy(false);
    }
  }

  async function copyForPr() {
    setBusy(true);
    setError(null);
    try {
      const { markdown } = await api.getReviewMarkdown(t.id);
      await navigator.clipboard.writeText(markdown);
      setCopied(true);
      setTimeout(() => setCopied(false), 2600);
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Could not copy the review.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="space-y-4">
      <div>
        <Label>Reviewing</Label>
        <div className="rounded-lg border border-hair bg-black/20 px-3 py-2.5 space-y-1.5">
          <div className="flex items-baseline gap-2 flex-wrap">
            <code className="text-[12px] text-sky-300">{review.sourceBranch}</code>
            {review.targetBranch && (
              <span className="text-[11px] text-gray-600">→ {review.targetBranch}</span>
            )}
            {review.prAuthor && (
              <span className="text-[11px] text-gray-500">by {review.prAuthor}</span>
            )}
          </div>
          <div className="flex items-center gap-3 flex-wrap text-[11px] text-gray-600">
            {review.prUrl && (
              <a
                href={review.prUrl}
                target="_blank"
                rel="noreferrer"
                className="text-gray-400 hover:text-sky-300 transition-colors"
              >
                Open the pull request ↗
              </a>
            )}
            {short(review.reviewedAtSha) && <span>read at {short(review.reviewedAtSha)}</span>}
          </div>

          {/* The author has pushed since the pass that wrote these comments. Said once, here, rather
              than only as a badge per comment: the useful action is "review it again", and that is a
              decision about the ticket rather than about any one finding. */}
          {review.stale && (
            <p className="text-[11px] leading-snug text-amber-400/90 pt-0.5">
              The branch has moved since this was reviewed (origin is at {short(review.headSha)}).
              The line numbers below describe {short(review.reviewedAtSha)} — send this ticket back
              for another pass before relying on them.
            </p>
          )}
        </div>
      </div>

      <div>
        <Label>Verdict</Label>
        <div className="flex flex-wrap gap-1.5">
          {(Object.keys(VERDICT_LABEL) as ReviewVerdict[]).map(v => {
            const on = review.verdict === v;
            return (
              <button
                key={v}
                type="button"
                disabled={busy}
                onClick={() => run(() => api.setReviewVerdict(t.id, v))}
                className={`text-[11px] px-2 py-1 rounded border transition-colors disabled:opacity-40 ${
                  on
                    ? 'text-gray-100 border-gray-500 bg-gray-700/60'
                    : 'text-gray-500 border-hair hover:text-gray-300 hover:border-gray-600'
                }`}
              >
                {VERDICT_LABEL[v]}
              </button>
            );
          })}
        </div>
        <p className="text-[11px] text-gray-600 mt-1.5 leading-snug">
          What the run concluded, and yours to overrule. Nothing here is sent.
        </p>
      </div>

      <div>
        <Label>
          Staged feedback{live.length > 0 ? ` · ${live.length}` : ''}
        </Label>

        {live.length === 0 && dismissed.length === 0 ? (
          <p className="text-[12px] text-gray-600">
            Nothing staged. A review that found nothing still records a verdict.
          </p>
        ) : (
          <div className="space-y-1.5">
            {SEVERITY_ORDER.flatMap(sev => {
              const group = live.filter(c => c.severity === sev);
              if (group.length === 0) return [];
              return group.map(c => (
                <StagedComment key={c.id} t={t} c={c} busy={busy} sent={sent} run={run} onOpenShot={openShot} />
              ));
            })}
            {dismissed.map(c => (
              <StagedComment key={c.id} t={t} c={c} busy={busy} sent={sent} run={run} onOpenShot={openShot} />
            ))}
          </div>
        )}
      </div>

      <div className="flex items-center gap-2 flex-wrap pt-0.5">
        <button
          type="button"
          disabled={busy}
          onClick={copyForPr}
          className="text-[12px] px-3 py-1.5 rounded border border-hair text-gray-300 hover:text-emerald-300 hover:border-emerald-800 disabled:opacity-40 transition-colors"
        >
          {copied ? 'Copied — paste it into the PR' : 'Copy for the PR'}
        </button>

        {/* Recording what you did, not doing it. In v1 the sending happens in your browser; when a
            host client lands, the posting goes behind this same button and no worker gains a route
            to it. */}
        <button
          type="button"
          disabled={busy || sent}
          onClick={() => run(() => api.markReviewSubmitted(t.id))}
          title={sent ? 'Already marked as sent' : 'Mark this feedback as sent to the pull request'}
          className="text-[12px] px-3 py-1.5 rounded border border-hair text-gray-500 hover:text-gray-200 hover:border-gray-600 disabled:opacity-40 transition-colors"
        >
          {sent ? 'Sent' : 'Mark as sent'}
        </button>

        <span className="text-[11px] text-gray-600">
          {sent
            ? `Sent — waiting on ${review.prAuthor ?? 'the author'}.`
            : 'Nothing has left this machine.'}
        </span>
      </div>

      {error && <p className="text-[11px] text-rose-400">{error}</p>}

      {lightbox}
    </div>
  );
}

/** One staged comment: where it points, what it says, and the two decisions you can make about it. */
function StagedComment({ t, c, busy, sent, run, onOpenShot }: {
  t: TicketDetail;
  c: ReviewComment;
  busy: boolean;
  sent: boolean;
  run: (fn: () => Promise<unknown>) => void;
  onOpenShot: (i: number) => void;
}) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(c.body);
  const style = SEVERITY_STYLE[c.severity];
  const isDismissed = c.status === 'Dismissed';
  const isSubmitted = c.status === 'Submitted';
  const where = c.filePath ? (c.line != null ? `${c.filePath}:${c.line}` : c.filePath) : null;

  // The shot this finding came out of, if it came out of one. Matched on the slot the comment
  // recorded rather than on its position in the array, because the two only agree while the set is
  // complete — and QA rewrites the whole set on every run.
  const shotIndex = c.qaShotOrder == null ? -1 : t.qaShots.findIndex(s => s.order === c.qaShotOrder);
  const shot = shotIndex >= 0 ? t.qaShots[shotIndex] : null;

  return (
    <div
      className={`rounded-lg border px-3 py-2 transition-colors ${
        isDismissed ? 'border-hair bg-black/10 opacity-45' : 'border-hair bg-black/20'
      }`}
    >
      <div className="flex items-start gap-2 flex-wrap">
        <span className={`text-[10px] px-1.5 py-0.5 rounded border font-semibold ${style.chip}`}>
          {style.label}
        </span>
        {where && (
          <code className="text-[11px] text-gray-400 min-w-0 break-all">{where}</code>
        )}
        {c.author === 'Human' && <span className="text-[10px] text-gray-600">yours</span>}
        {/* The file this points at changed after the comment was written, so its line number is a
            claim about code that has moved. Kept and flagged rather than dropped: the reasoning is
            the expensive half and it is usually still right. */}
        {c.stale && !isSubmitted && (
          <span className="text-[10px] px-1.5 py-0.5 rounded border border-amber-900/70 text-amber-400/90">
            line may have moved
          </span>
        )}
        {isSubmitted && <span className="text-[10px] text-emerald-500/80">sent</span>}

        <span className="flex-1" />

        {!sent && !isSubmitted && (
          <span className="flex items-center gap-1.5 text-[11px]">
            {!editing && (
              <button
                type="button"
                disabled={busy}
                onClick={() => { setDraft(c.body); setEditing(true); }}
                className="text-gray-600 hover:text-gray-300 disabled:opacity-40 transition-colors"
              >
                edit
              </button>
            )}
            <button
              type="button"
              disabled={busy}
              onClick={() => run(() => api.updateReviewComment(t.id, c.id, {
                status: isDismissed ? 'Draft' : 'Dismissed',
              }))}
              className="text-gray-600 hover:text-gray-300 disabled:opacity-40 transition-colors"
            >
              {isDismissed ? 'restore' : 'dismiss'}
            </button>
            <button
              type="button"
              disabled={busy}
              onClick={() => run(() => api.deleteReviewComment(t.id, c.id))}
              title="Delete this comment for good"
              className="text-gray-700 hover:text-rose-400 disabled:opacity-40 transition-colors"
            >
              ×
            </button>
          </span>
        )}
      </div>

      {editing ? (
        <div className="mt-1.5 space-y-1.5">
          <textarea
            value={draft}
            autoFocus
            rows={4}
            onChange={e => setDraft(e.target.value)}
            className="w-full text-[12px] bg-black/40 border border-hair rounded px-2 py-1.5 text-gray-200 focus:outline-none focus:border-gray-600"
          />
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={busy || !draft.trim()}
              onClick={() => { run(() => api.updateReviewComment(t.id, c.id, { body: draft.trim() })); setEditing(false); }}
              className="text-[11px] px-2 py-1 rounded border border-hair text-gray-300 hover:border-gray-600 disabled:opacity-40 transition-colors"
            >
              Save
            </button>
            <button
              type="button"
              onClick={() => setEditing(false)}
              className="text-[11px] text-gray-600 hover:text-gray-400 transition-colors"
            >
              Cancel
            </button>
            <span className="flex-1" />
            {/* Re-grading is the edit people make most: a run that marked everything Blocking is
                exactly what this panel exists to fix, and doing it needs no dialog. */}
            <span className="flex items-center gap-1">
              {SEVERITY_ORDER.map(s => (
                <button
                  key={s}
                  type="button"
                  disabled={busy || s === c.severity}
                  onClick={() => run(() => api.updateReviewComment(t.id, c.id, { severity: s }))}
                  title={`Mark as ${SEVERITY_STYLE[s].label}`}
                  className={`w-2.5 h-2.5 rounded-full transition-opacity ${SEVERITY_STYLE[s].dot} ${
                    s === c.severity ? 'opacity-100' : 'opacity-30 hover:opacity-70'
                  }`}
                />
              ))}
            </span>
          </div>
        </div>
      ) : (
        <div className={`mt-1 text-[12px] leading-snug ${isDismissed ? 'line-through text-gray-600' : 'text-gray-300'}`}>
          <Markdown text={c.body} />
        </div>
      )}

      {/* The evidence, under the claim. A comment that came out of a drive-test is the one kind
          this panel cannot argue with, and until now the reader had to scroll to the QA Shots
          section and work out by hand which of six screenshots the finding meant. The full set
          still lives down there — this is the same image, said next to the sentence about it. */}
      {shot && <CitedShot shot={shot} onOpen={() => onOpenShot(shotIndex)} />}

      {/* Cited a slot that is not there any more: QA replaces the whole set each run, so a
          re-tested ticket can leave a comment pointing at a shot nobody took this time. Said out
          loud rather than drawn as nothing, because "no picture" and "the picture moved" are
          different problems and only one of them is the reader's to fix. */}
      {!shot && c.qaShotOrder != null && (
        <p className="mt-1.5 text-[11px] text-gray-600">
          Cites QA shot {c.qaShotOrder + 1}, which the current set no longer has.
        </p>
      )}
    </div>
  );
}

/**
 * The QA screenshot a staged comment points at, inline with it.
 *
 * A thumbnail rather than the shot itself: at this size the image is a landmark that says WHICH
 * shot, and the caption beside it says what it shows — the reading is done in the lightbox, one
 * click away, where the rest of the set is reachable too.
 */
function CitedShot({ shot, onOpen }: { shot: QaShot; onOpen: () => void }) {
  return (
    <button
      type="button"
      onClick={onOpen}
      title="Open this QA shot"
      className="orbit-tap mt-2 flex w-full items-start gap-2.5 rounded-lg border border-hair bg-black/25 p-1.5 text-left hover:border-gray-600 cursor-zoom-in"
    >
      {/* Cropped from the top rather than fitted: a screenshot letterboxed into 50 pixels is a grey
          band with a smear in it, whereas its first rows are usually the header, tab or grid that
          tells you which screen you are looking at. */}
      <img
        src={shot.imageUrl}
        alt={shot.caption || `QA shot ${shot.order + 1}`}
        loading="lazy"
        className="h-[52px] w-[92px] flex-none rounded border border-hair bg-black/40 object-cover object-top"
      />
      <span className="min-w-0 flex-1">
        <span className="block text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)] text-gray-500">
          QA shot {shot.order + 1}
        </span>
        {/* No `block` beside the clamp: `line-clamp-2` works by setting `display: -webkit-box`, and
            Tailwind emits the display utilities after it, so the two together silently give an
            unclamped block — which here is a four-line caption taller than the shot beside it. */}
        {shot.caption && (
          <span className="mt-0.5 text-[11.5px] leading-snug text-gray-500 line-clamp-2">
            {shot.caption}
          </span>
        )}
      </span>
    </button>
  );
}
