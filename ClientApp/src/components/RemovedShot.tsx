/**
 * A QA shot whose picture capture retention has removed (docs/capture-retention.md): the caption
 * stays on the ticket, so this takes the picture's place and says when it went. Its image URL
 * answers with the same notice as a picture, for anything that only has the URL - a walkthrough
 * deck, the lightbox.
 */
export function RemovedShot({ removedOn, className = '', what = 'Screenshot', title = 'Removed by capture retention, some time after the ticket was done' }: {
  removedOn: string;
  className?: string;
  /** What went: a screenshot by default; a discovery's clip or recording says so. */
  what?: string;
  title?: string;
}) {
  const when = new Date(removedOn).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' });
  return (
    <div
      className={`flex items-center justify-center rounded-lg border border-dashed border-hair bg-black/20 px-3 text-center text-[11.5px] leading-snug text-gray-500 ${className}`}
      title={title}
    >
      {what} removed on {when}
    </div>
  );
}
