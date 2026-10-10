import { useMemo, useState } from 'react';
import type { PrFile, DiffTier } from '../api/git';
import { PatchView } from './PatchView';

/**
 * The diff, ordered by what is worth reading.
 *
 * This is the reason the PR dialog earns its place. Adding one field to a domain concept touches
 * fifteen files, and fourteen of them are the same line pasted into a proto, an enum, a view model
 * and a generated client. Presented as a flat alphabetical list, the three-line edit to a SQL query —
 * the only place the ticket could actually be wrong — sits somewhere in the middle of them, looking
 * exactly like the boilerplate.
 *
 * So the list is grouped by the server's ranking. Major arrives open with its diffs already
 * expanded, because it is what the reader came for. Routine is listed but folded. Skimmable is
 * behind one click. Nothing is hidden and nothing is dropped — the ordering just stops the
 * boilerplate standing between the reviewer and the review.
 */
export function DiffReviewList({ files, truncated, pinned, onTogglePin, root, ideUrl }: {
  files: PrFile[];
  truncated: boolean;
  pinned: Set<string>;
  onTogglePin: (path: string) => void;
  /** The working tree the diff's new content is actually in — see `editorHref`. */
  root?: string | null;
  /** The project's editor URL template. Without it, no file gets an open button. */
  ideUrl?: string;
}) {
  // Alphabetical stays one click away. The ranking is a good guess, not a fact, and someone looking
  // for a file whose name they already know should not have to work out its tier first.
  const [byPath, setByPath] = useState(false);

  const groups = useMemo(() => {
    const starred = files.filter(f => pinned.has(f.path));
    const rest = files.filter(f => !pinned.has(f.path));
    const of = (t: DiffTier) => rest.filter(f => f.tier === t);
    return { starred, major: of('Major'), normal: of('Normal'), minor: of('Minor') };
  }, [files, pinned]);

  const sortedByPath = useMemo(
    () => [...files].sort((a, b) => a.path.localeCompare(b.path)),
    [files],
  );

  if (files.length === 0) return <div className="text-[11px] text-gray-600 py-3">No file changes.</div>;

  const row = (f: PrFile, open: boolean) => (
    <FileDiff
      key={f.path}
      file={f}
      defaultOpen={open}
      pinned={pinned.has(f.path)}
      onTogglePin={onTogglePin}
      href={editorHref(f, root, ideUrl)}
    />
  );

  return (
    <div className="py-2 space-y-2">
      <div className="flex items-center justify-between gap-3">
        <div className="text-[11px] text-gray-500 min-w-0 truncate">
          {groups.major.length > 0 ? (
            <>
              <b className="text-amber-300 font-semibold">{groups.major.length}</b> of {files.length} files
              look worth reading.
            </>
          ) : (
            <>Nothing scored as high-risk — {files.length} routine file{files.length === 1 ? '' : 's'}.</>
          )}
        </div>
        <button
          onClick={() => setByPath(v => !v)}
          className="flex-none text-[10px] font-semibold text-gray-500 hover:text-gray-300 px-2 py-0.5 rounded border border-hair hover:border-hair transition-colors"
        >
          {byPath ? 'Sort by significance' : 'Sort by path'}
        </button>
      </div>

      {byPath ? (
        <div className="space-y-1">{sortedByPath.map(f => row(f, false))}</div>
      ) : (
        <>
          {groups.starred.length > 0 && (
            <Group title="Starred" accent="text-yellow-300" count={groups.starred.length} defaultOpen>
              {groups.starred.map(f => row(f, true))}
            </Group>
          )}
          {groups.major.length > 0 && (
            <Group
              title="Worth reviewing"
              accent="text-amber-300"
              count={groups.major.length}
              note="logic, schema, or a query that changed rather than grew"
              defaultOpen
            >
              {groups.major.map(f => row(f, true))}
            </Group>
          )}
          {groups.normal.length > 0 && (
            <Group
              title="Routine"
              accent="text-sky-300"
              count={groups.normal.length}
              note="real edits, ordinary shape"
              defaultOpen
            >
              {groups.normal.map(f => row(f, false))}
            </Group>
          )}
          {groups.minor.length > 0 && (
            <Group
              title="Skimmable"
              accent="text-gray-400"
              count={groups.minor.length}
              note="generated, mechanical, or one more entry in a list"
            >
              {groups.minor.map(f => row(f, false))}
            </Group>
          )}
        </>
      )}

      {truncated && (
        <div className="text-[11px] text-amber-400 pt-1">
          The diff was too large to send in full, so some files show no body — always the least
          significant ones. Read those in the repo.
        </div>
      )}
    </div>
  );
}

/**
 * Where this file is on disk, and the editor URL that opens it at its first changed line.
 *
 * Reading a hunk answers whether the change is right; it does not answer what the rest of the
 * function around it does, and that is most of what a reviewer actually wants to know. The patch
 * cannot tell them, so the button hands the question to the editor rather than making them find the
 * path by hand.
 *
 * `root` is the working tree the diff was measured in — the agent's worktree for a PR, the hub once
 * a TFVC merge is staged — because that is the only place the new content exists. A deleted file has
 * no new content anywhere, so it gets no button rather than one that opens nothing.
 */
function editorHref(file: PrFile, root: string | null | undefined, ideUrl: string | undefined) {
  if (!root || !ideUrl || file.status === 'deleted') return null;
  // Forward slashes and no trailing one: the same shape the analysis citations build, and what
  // every editor's URL scheme expects on Windows.
  const base = root.replace(/\\/g, '/').replace(/\/+$/, '');
  return ideUrl.replace('{path}', `${base}/${file.path}`).replace('{line}', String(firstChangedLine(file.patch)));
}

/**
 * The line the first real change sits on, in the NEW file. Landing on line 1 of a two-thousand-line
 * file is the same chore as not linking at all; the hunk header already says where to go.
 *
 * Counted rather than read straight off the header, because `@@ ... +240` is the start of that
 * hunk's leading context and the edit is usually three lines below it. Zero means no hunk has been
 * seen yet — a body dropped for size has none at all, and line 1 is then the honest answer.
 */
function firstChangedLine(patch: string): number {
  let line = 0;
  for (const l of patch.split('\n')) {
    const hunk = /^@@ -\d+(?:,\d+)? \+(\d+)/.exec(l);
    if (hunk) { line = Number(hunk[1]); continue; }
    if (line === 0) continue;
    if (l.startsWith('+') || l.startsWith('-')) return line;
    // A context line advances the new file; a removed line does not, and is returned above anyway.
    line++;
  }
  return line || 1;
}

/** One tier's heading and its files. Collapsed unless told otherwise; the count is always visible. */
function Group({ title, accent, count, note, defaultOpen = false, children }: {
  title: string;
  accent: string;
  count: number;
  note?: string;
  defaultOpen?: boolean;
  children: React.ReactNode;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <div>
      <button
        onClick={() => setOpen(o => !o)}
        className="w-full flex items-baseline gap-2 py-1 text-left"
        aria-expanded={open}
      >
        <span className={`text-[9px] text-gray-600 transition-transform ${open ? 'rotate-90' : ''}`}>▶</span>
        <span className={`text-[11px] font-bold tracking-wide ${accent}`}>{title}</span>
        <span className="text-[10px] font-mono text-gray-500">{count}</span>
        {note && <span className="text-[10px] text-gray-600 truncate hidden sm:inline">— {note}</span>}
        <span className="flex-1 h-px bg-gray-800 self-center" />
      </button>
      {open && <div className="space-y-1 pb-1">{children}</div>}
    </div>
  );
}

// Reasons are not equally alarming. The three that mean "this can break production in a way review
// is the last defence against" get the warm colour; the ones that explain a demotion stay quiet.
const LOUD_REASONS = new Set(['migration', 'schema change', 'query changed']);
const DIM_REASONS = new Set([
  'generated', 'list entries', 'imports', 'formatting', 'renamed', 'binary',
  'docs', 'config', 'tests', 'added', 'edited',
]);

function ReasonChip({ reason }: { reason: string }) {
  const cls = LOUD_REASONS.has(reason)
    ? 'bg-amber-950/60 text-amber-300 border-amber-900/60'
    : DIM_REASONS.has(reason)
      ? 'bg-panel text-gray-600 border-hairsoft'
      : 'bg-[var(--w-queue-950)] text-sky-400/90 border-sky-950';
  return <span className={`text-[9px] px-1.5 py-px rounded border whitespace-nowrap ${cls}`}>{reason}</span>;
}

const STATUS_MARKS: Record<string, { letter: string; cls: string }> = {
  added: { letter: 'A', cls: 'text-emerald-400 border-emerald-900/70' },
  deleted: { letter: 'D', cls: 'text-red-400 border-red-900/70' },
  renamed: { letter: 'R', cls: 'text-sky-400 border-sky-900/70' },
};

/**
 * Added, deleted or renamed, as one letter before the name. An edit — most of any list — carries
 * nothing, so the three that are not edits stand out; on a TFVC project they are the ones a human
 * has to promote by hand.
 */
export function StatusMark({ status }: { status?: string }) {
  const mark = status ? STATUS_MARKS[status] : undefined;
  if (!mark) return null;
  return (
    <span
      title={status}
      className={`flex-none font-mono text-[9px] leading-none px-1 py-0.5 rounded-sm border ${mark.cls}`}
    >
      {mark.letter}
    </span>
  );
}

/**
 * One file: why it ranked where it did, and its hunks.
 *
 * Line colouring is by first character, which is all a unified diff encodes. The `---`/`+++`
 * headers are stripped server-side, so they can never render as one giant deletion followed by one
 * giant addition.
 */
function FileDiff({ file, defaultOpen, pinned, onTogglePin, href }: {
  file: PrFile;
  defaultOpen: boolean;
  pinned: boolean;
  onTogglePin: (path: string) => void;
  /** Opens the file in the reader's editor, or null when there is nowhere to open it. */
  href: string | null;
}) {
  const [open, setOpen] = useState(defaultOpen);
  const lines = useMemo(() => (open ? file.patch.split('\n') : []), [open, file.patch]);

  // The tail of the path identifies the file; the leading directories are shared by half the list
  // and are exactly what pushes the filename off the right-hand edge.
  const cut = file.path.lastIndexOf('/');
  const dir = cut > 0 ? file.path.slice(0, cut + 1) : '';
  const name = cut > 0 ? file.path.slice(cut + 1) : file.path;

  const edge =
    file.tier === 'Major' ? 'border-l-2 border-l-amber-600/70'
    : file.tier === 'Normal' ? 'border-l-2 border-l-sky-800/70'
    : 'border-l-2 border-l-[var(--w-hair)]';

  return (
    <div className={`border border-hairsoft rounded overflow-hidden ${edge}`}>
      <div className="flex items-center gap-1.5 px-2 py-1.5 bg-panel">
        <button
          onClick={() => onTogglePin(file.path)}
          title={pinned ? 'Unstar' : 'Star this file — pins it above everything else'}
          className={`flex-none text-[11px] leading-none px-0.5 transition-colors ${
            pinned ? 'text-yellow-300' : 'text-gray-700 hover:text-yellow-400'
          }`}
        >
          {pinned ? '★' : '☆'}
        </button>
        <button
          onClick={() => setOpen(o => !o)}
          className="flex items-center gap-2 min-w-0 flex-1 text-left hover:brightness-125 transition-all"
        >
          <span className={`text-[9px] text-gray-600 transition-transform flex-none ${open ? 'rotate-90' : ''}`}>▶</span>
          <StatusMark status={file.status} />
          <span className="font-mono text-[11px] truncate min-w-0" title={file.path}>
            <span className="text-gray-600">{dir}</span>
            <span className={file.tier === 'Minor' ? 'text-gray-400' : 'text-gray-200'}>{name}</span>
          </span>
        </button>
        <div className="flex items-center gap-1 flex-none">
          {file.reasons.slice(0, 3).map(r => <ReasonChip key={r} reason={r} />)}
          {file.binary ? (
            <span className="text-[10px] text-gray-600 ml-1">binary</span>
          ) : (
            <span className="font-mono text-[10px] ml-1">
              <span className="text-emerald-500">+{file.added}</span>{' '}
              <span className="text-red-500">−{file.removed}</span>
            </span>
          )}
          {href && (
            <a
              href={href}
              title={`Open ${file.path} in your editor`}
              className="orbit-tap text-[10px] font-semibold text-gray-600 hover:text-sky-300 ml-1 px-1 py-0.5 rounded border border-hairsoft hover:border-sky-900"
            >
              open
            </a>
          )}
        </div>
      </div>

      {file.renamedFrom && (
        <div className="px-3 py-1 text-[10px] font-mono text-gray-600 bg-panel border-t border-hairsoft truncate">
          renamed from {file.renamedFrom}
        </div>
      )}

      {open && (
        <div className="max-h-80 overflow-auto bg-sunk border-t border-hairsoft">
          {file.patchOmitted ? (
            <div className="px-3 py-2 text-[10px] text-amber-500/80">
              Body not sent — the diff exceeded the size budget. Read this one in the repo.
            </div>
          ) : (
            <PatchView patch={lines.join('\n')} />
          )}
        </div>
      )}
    </div>
  );
}
