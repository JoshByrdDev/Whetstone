import type { ReactNode } from 'react';

/**
 * Just enough markdown for a brief: `##` headings, `-` bullets, paragraphs, `**bold**`, `` `code` ``
 * and the `[[note]]` links that point into the repo's own notes store. Deliberately a renderer that
 * builds elements rather than HTML — the text is model-written, so it never reaches innerHTML — and
 * deliberately not a dependency, since the app renders prose in a handful of places and all of it
 * is this plain.
 *
 * `doc` is for a whole document somebody wrote to be read top to bottom - a plan, a spec - rather
 * than a few hundred words in a drawer: its headings keep their levels, so a reader can tell a
 * chapter from a section. Tables, quotes and numbered lists are drawn either way; a brief simply
 * never has them.
 */
export function Markdown({ text, doc = false }: { text: string; doc?: boolean }) {
  const blocks: ReactNode[] = [];
  let bullets: { text: string; depth: number; n?: string }[] = [];
  let quote: string[] = [];
  let rows: string[][] = [];

  const flushBullets = () => {
    if (bullets.length === 0) return;
    blocks.push(
      <ul key={`u${blocks.length}`} className="my-1.5 flex flex-col gap-1">
        {bullets.map((b, i) => (
          <li
            key={i} className="flex gap-2 text-[13px] leading-relaxed text-gray-300"
            style={{ paddingLeft: 4 + b.depth * 16 }}
          >
            <span className="flex-none text-gray-600" style={{ minWidth: b.n ? 18 : 8 }}>{b.n ?? '•'}</span>
            <span className="min-w-0">{inline(b.text)}</span>
          </li>
        ))}
      </ul>,
    );
    bullets = [];
  };
  const flushQuote = () => {
    if (quote.length === 0) return;
    blocks.push(
      <div key={`q${blocks.length}`} className="my-2 border-l-2 border-hair pl-3 flex flex-col gap-1.5">
        {quote.filter(l => l.trim() !== '').map((l, i) => (
          <p key={i} className="text-[12.5px] leading-relaxed text-gray-400">{inline(l)}</p>
        ))}
      </div>,
    );
    quote = [];
  };
  const flushTable = () => {
    if (rows.length === 0) return;
    // The row of dashes under a header says which row was one; a table without it has none.
    const ruled = rows.length > 1 && rows[1].every(c => /^:?-{2,}:?$/.test(c.trim()));
    const head = ruled ? rows[0] : null;
    const body = ruled ? rows.slice(2) : rows;
    blocks.push(
      // Its own scroller: a wide table must never be what makes the page scroll sideways.
      <div key={`t${blocks.length}`} className="my-2 overflow-x-auto">
        <table className="text-[12.5px] leading-relaxed text-gray-300 border-collapse">
          {head && head.some(c => c.trim() !== '') && (
            <thead>
              <tr>{head.map((c, i) => <th key={i} className="text-left font-semibold text-gray-100 border-b border-hair px-2 py-1 align-top">{inline(c.trim())}</th>)}</tr>
            </thead>
          )}
          <tbody>
            {body.map((r, i) => (
              <tr key={i}>{r.map((c, j) => <td key={j} className="border-b border-hair px-2 py-1 align-top">{inline(c.trim())}</td>)}</tr>
            ))}
          </tbody>
        </table>
      </div>,
    );
    rows = [];
  };
  const flush = () => { flushBullets(); flushQuote(); flushTable(); };

  for (const raw of text.split('\n')) {
    const line = raw.trimEnd();
    const heading = /^(#{1,4})\s+(.*)$/.exec(line);
    const bullet = /^(\s*)[-*]\s+(.*)$/.exec(line);
    const numbered = /^(\s*)(\d+)[.)]\s+(.*)$/.exec(line);
    const quoted = /^\s*>\s?(.*)$/.exec(line);
    const cells = /^\s*\|(.*)\|\s*$/.exec(line);

    if (heading) {
      flush();
      const level = heading[1].length;
      // Bright and ruled off, not tinted. The ORBIT theme rebuilt `indigo` out of the board's blue
      // tier, which lands within a few points of `gray-300` — so a heading that separated itself
      // from the body by being indigo separated itself from nothing, and the sections read as one
      // wall of prose broken by capitals. The steps that still carry here are the ones the palette
      // did not flatten: a brighter rung of the same ramp, more weight, and a rule above.
      const cls = !doc || level >= 3
        ? 'text-[12px] font-bold uppercase tracking-[calc(var(--w-track)*0.14em)] text-gray-100 mt-4 pt-3 border-t border-hair first:mt-0 first:pt-0 first:border-0 mb-1.5'
        : level === 1
          ? 'text-[19px] font-bold text-gray-100 mb-2'
          : 'text-[15px] font-bold text-gray-100 mt-7 pt-4 border-t border-hair mb-2';
      blocks.push(<h4 key={`h${blocks.length}`} className={cls}>{inline(heading[2])}</h4>);
    } else if (cells) {
      flushBullets(); flushQuote();
      rows.push(cells[1].split('|'));
    } else if (quoted) {
      flushBullets(); flushTable();
      quote.push(quoted[1]);
    } else if (bullet) {
      flushQuote(); flushTable();
      bullets.push({ text: bullet[2], depth: Math.min(3, Math.floor(bullet[1].length / 2)) });
    } else if (numbered) {
      flushQuote(); flushTable();
      bullets.push({ text: numbered[3], depth: Math.min(3, Math.floor(numbered[1].length / 2)), n: `${numbered[2]}.` });
    } else if (line.trim() === '') {
      flush();
    } else if (bullets.length > 0 && /^\s+\S/.test(line)) {
      // An indented line under a bullet carries that bullet's sentence on.
      bullets[bullets.length - 1].text += ` ${line.trim()}`;
    } else {
      flush();
      blocks.push(
        <p key={`p${blocks.length}`} className="text-[13px] leading-relaxed text-gray-300 mt-1.5 first:mt-0">
          {inline(line)}
        </p>,
      );
    }
  }
  flush();

  return <>{blocks}</>;
}

/**
 * Inline spans: **bold**, `code`, [[note]] links into the repo's notes store, and a `[link](to)`,
 * which is shown as its words alone.
 * Exported because the Classroom's blocks render the same model-written prose and must agree with
 * the drawer on what those markers mean.
 */
export function inline(text: string): ReactNode[] {
  const out: ReactNode[] = [];
  const re = /\*\*(.+?)\*\*|`([^`]+)`|\[\[([^\]]+)\]\]|\[([^\]]+)\]\(([^)\s]+)\)/g;
  let last = 0;
  let m: RegExpExecArray | null;

  while ((m = re.exec(text)) !== null) {
    if (m.index > last) out.push(text.slice(last, m.index));
    if (m[1] !== undefined) {
      out.push(<strong key={out.length} className="font-semibold text-gray-100">{m[1]}</strong>);
    } else if (m[2] !== undefined) {
      out.push(<code key={out.length} className="font-mono text-[12px] text-sky-300">{m[2]}</code>);
    } else if (m[3] !== undefined) {
      // A note in the target repo's store. There is no URL to open it with — the value is knowing
      // it exists and what it is called, so it reads as a citation rather than a dead link.
      out.push(
        <span key={out.length} className="font-mono text-[12px] text-indigo-300" title="A note in this repo's notes store">
          {m[3]}
        </span>,
      );
    } else {
      // Nearly always a path inside the repo the text came from, which is nowhere this page can
      // take anyone. So it reads as a reference, and says where it points when asked.
      out.push(
        <span key={out.length} className="text-gray-200 underline decoration-dotted underline-offset-2" title={m[5]}>
          {m[4]}
        </span>,
      );
    }
    last = re.lastIndex;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}
