import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import type { Picture, PictureArrow, PictureChange, PictureItem } from '../api/classroom';
import { FLOW_TONES } from './AnalysisBlocks';
import { chamfer, MONO } from './orbit';

/**
 * A walkthrough's picture scene: a small diagram that changes while the narration explains it.
 *
 * The deck only says what is on screen at each beat; it never says how anything moves. This works
 * the move out by id. The picture is laid out once, invisibly, with ordinary flexbox, and every
 * id's box is measured. The visible copy then places each part absolutely at its measured box, so
 * a part that kept its id slides to its new place (`.orbit-morph` carries the move), a new id
 * enters, a missing one leaves from where it last stood, and the camera scales the whole thing to
 * fit the stage. Arrows are redrawn every frame of a move from the boxes as they travel, on the
 * same curve, so they stay attached to what they join.
 */

interface R { x: number; y: number; w: number; h: number }
/**
 * One measured picture. `enter` is decided here, against the picture before it, and not during a
 * render: it has to hold for as long as this picture is up, or a re-render would cut an entrance short.
 */
interface Layout { picture: Picture; items: PictureItem[]; rects: Record<string, R>; w: number; h: number; enter: Set<string> }
interface Ghost { key: string; item?: PictureItem; rect?: R; arrow?: PictureArrow; d?: string }

const ROW_GAP = 64;      // room between boxes for an arrow and its label
const COL_GAP = 44;
const GROUP_ROW_GAP = 40;
const GROUP_COL_GAP = 28;
const PAD = 32;          // the camera keeps this much stage clear around the picture
const LABEL_CH = 7.4;    // one character of an arrow label, at its 12px monospace

// ---- timing, read from the motion layer so this and the CSS can never drift ----------------

const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

function cssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function morphMs(): number {
  if (reducedMotion()) return 0;
  const v = cssVar('--w-dur-morph');
  const n = parseFloat(v);
  if (!Number.isFinite(n)) return 600;
  return v.endsWith('ms') ? n : n * 1000;
}

/** `--w-ease` as a function, for the arrows that have to follow boxes the CSS is moving. */
function easing(): (t: number) => number {
  const m = cssVar('--w-ease').match(/cubic-bezier\(([^)]+)\)/);
  const [x1, y1, x2, y2] = m ? m[1].split(',').map(Number) : [0.2, 0.8, 0.3, 1];
  const bez = (a: number, b: number, t: number) => 3 * a * t * (1 - t) ** 2 + 3 * b * t * t * (1 - t) + t ** 3;
  return (t: number) => {
    if (t <= 0) return 0;
    if (t >= 1) return 1;
    // Find the curve parameter whose x is t, then read its y. Bisection: a dozen steps is sub-pixel here.
    let lo = 0, hi = 1, u = t;
    for (let i = 0; i < 14; i++) {
      u = (lo + hi) / 2;
      if (bez(x1, x2, u) < t) lo = u; else hi = u;
    }
    return bez(y1, y2, u);
  };
}

// ---- geometry -----------------------------------------------------------------------------

const lerp = (a: number, b: number, e: number) => a + (b - a) * e;
const lerpR = (a: R, b: R, e: number): R => ({ x: lerp(a.x, b.x, e), y: lerp(a.y, b.y, e), w: lerp(a.w, b.w, e), h: lerp(a.h, b.h, e) });
const centre = (r: R) => ({ x: r.x + r.w / 2, y: r.y + r.h / 2 });

/** Where the line from a box's centre toward a point leaves the box, a little outside its edge. */
function edge(r: R, toward: { x: number; y: number }, gap = 7) {
  const c = centre(r);
  const dx = toward.x - c.x, dy = toward.y - c.y;
  if (dx === 0 && dy === 0) return c;
  const sx = dx === 0 ? Infinity : (r.w / 2 + gap) / Math.abs(dx);
  const sy = dy === 0 ? Infinity : (r.h / 2 + gap) / Math.abs(dy);
  const s = Math.min(sx, sy);
  return { x: c.x + dx * s, y: c.y + dy * s };
}

function crosses(a: { x: number; y: number }, b: { x: number; y: number }, r: R) {
  for (let i = 1; i < 12; i++) {
    const x = lerp(a.x, b.x, i / 12), y = lerp(a.y, b.y, i / 12);
    if (x > r.x && x < r.x + r.w && y > r.y && y < r.y + r.h) return true;
  }
  return false;
}

/**
 * An arrow from one box to another: straight, unless that would run through a third box, in which
 * case it bows around it. Returns the path and where its label sits.
 */
function route(from: R, to: R, others: R[]): { d: string; lx: number; ly: number } {
  const a0 = edge(from, centre(to)), b0 = edge(to, centre(from));
  if (!others.some(o => crosses(a0, b0, o))) {
    return { d: `M${a0.x},${a0.y} L${b0.x},${b0.y}`, lx: (a0.x + b0.x) / 2, ly: (a0.y + b0.y) / 2 - 10 };
  }
  const ca = centre(from), cb = centre(to);
  const len = Math.hypot(cb.x - ca.x, cb.y - ca.y) || 1;
  // Bow away from the picture's middle row: up for a row, left for a column.
  const nx = -(cb.y - ca.y) / len, ny = (cb.x - ca.x) / len;
  const flip = ny > 0 || (ny === 0 && nx > 0) ? -1 : 1;
  const lift = Math.max(...others.map(o => o.h), from.h, to.h) * 0.75 + 28;
  const cx = (ca.x + cb.x) / 2 + nx * lift * flip, cy = (ca.y + cb.y) / 2 + ny * lift * flip;
  const a = edge(from, { x: cx, y: cy }), b = edge(to, { x: cx, y: cy });
  // The label sits on the curve's midpoint: a quadratic's is halfway between the control and the chord.
  return { d: `M${a.x},${a.y} Q${cx},${cy} ${b.x},${b.y}`, lx: (cx * 2 + a.x + b.x) / 4, ly: (cy * 2 + a.y + b.y) / 4 - 8 };
}

const arrowKey = (a: PictureArrow) => `${a.from}>${a.to}`;

// ---- what a part looks like ------------------------------------------------------------------

const tone = (c: PictureChange) => FLOW_TONES[c] ?? FLOW_TONES.unchanged;

/** The frame's box model. The measured copy and the visible copy use exactly this, or the text wraps differently. */
function frame(item: PictureItem): { className: string; style: CSSProperties } {
  const t = tone(item.change);
  switch (item.kind) {
    case 'group':
      return {
        className: 'flex flex-col items-stretch gap-3 border border-dashed px-4 pt-2.5 pb-4',
        style: { borderColor: item.change === 'unchanged' ? 'var(--w-hair)' : t.stroke, background: 'rgb(0 0 0 / .14)', clipPath: chamfer(10) },
      };
    case 'text':
      return { className: 'px-1 py-0.5 text-center text-[16px] leading-snug text-gray-300 max-w-[360px]', style: {} };
    case 'code':
      return {
        className: 'border px-3 py-1.5 text-[14px] leading-snug max-w-[320px] break-words',
        style: { fontFamily: MONO, borderColor: t.stroke, background: 'var(--w-void)', color: t.text, clipPath: chamfer(5) },
      };
    default:
      return {
        className: 'flex flex-col items-center justify-center border-[1.5px] px-4 py-2.5 text-center min-w-[120px] max-w-[260px]',
        style: { borderColor: t.stroke, background: t.fill, color: t.text, clipPath: chamfer(8) },
      };
  }
}

function Words({ item }: { item: PictureItem }) {
  const gone = item.change === 'removed' ? 'line-through decoration-2' : '';
  if (item.kind === 'group') {
    return (
      <span key={item.text} className="orbit-swap block text-[11px] font-semibold uppercase tracking-[calc(var(--w-track)*0.1em)]"
        style={{ color: item.change === 'unchanged' ? 'var(--w-t500)' : tone(item.change).text }}>
        {item.text || ' '}
      </span>
    );
  }
  return (
    <>
      <span key={item.text} className={`orbit-swap block ${item.kind === 'box' ? 'text-[17px] font-medium leading-snug' : ''} ${gone}`}>
        {item.text}
      </span>
      {item.sub && <span key={`s-${item.sub}`} className="orbit-swap mt-1 block text-[12.5px] leading-snug text-gray-400">{item.sub}</span>}
    </>
  );
}

// ---- the invisible layout ------------------------------------------------------------------

/** Room between two boxes in a row for the widest arrow label that might sit between them. */
function rowRoom(p: Picture): number {
  const longest = Math.max(0, ...p.arrows.map(a => a.label?.length ?? 0));
  return longest === 0 ? 0 : longest * LABEL_CH + 28;
}

function Tree({ items, parent, layout, top, room }: {
  items: PictureItem[]; parent: string | null; layout: 'row' | 'column'; top?: boolean; room: number;
}) {
  const kids = items.filter(i => (i.in ?? null) === parent);
  if (kids.length === 0) return null;
  const row = layout === 'row';
  const gap = row ? Math.max(top ? ROW_GAP : GROUP_ROW_GAP, room) : (top ? COL_GAP : GROUP_COL_GAP);
  return (
    <div className="flex items-center justify-center" style={{ flexDirection: row ? 'row' : 'column', gap }}>
      {kids.map(k => {
        const f = frame(k);
        return (
          <div key={k.id} data-pid={k.id} className={`shrink-0 ${f.className}`} style={f.style}>
            <Words item={k} />
            {k.kind === 'group' && <Tree items={items} parent={k.id} layout={k.layout ?? 'row'} room={room} />}
          </div>
        );
      })}
    </div>
  );
}

/** Items whose group is not in the picture are drawn at the top level, as the server would have placed them. */
function settle(p: Picture): PictureItem[] {
  const groups = new Set(p.items.filter(i => i.kind === 'group').map(i => i.id));
  return p.items.map(i => (i.in && !groups.has(i.in) ? { ...i, in: null } : i));
}

function depthOf(items: PictureItem[], id: string): number {
  let d = 0;
  for (let p = items.find(i => i.id === id)?.in; p && d < 8; p = items.find(i => i.id === p)?.in) d++;
  return d;
}

// ---- the picture --------------------------------------------------------------------------

export function PictureView({ picture, lit }: { picture: Picture; lit: string[] }) {
  const uid = useId().replace(/[^A-Za-z0-9_-]/g, '');
  const stageRef = useRef<HTMLDivElement>(null);
  const measureRef = useRef<HTMLDivElement>(null);
  const [stage, setStage] = useState({ w: 0, h: 0 });
  const [fonts, setFonts] = useState(0);
  const [layout, setLayout] = useState<Layout | null>(null);
  const [ghosts, setGhosts] = useState<Ghost[]>([]);

  const items = useMemo(() => settle(picture), [picture]);

  // The layout, measured and never seen.
  const measure = (
    <div aria-hidden className="pointer-events-none invisible absolute left-0 top-0">
      <div ref={measureRef} className="inline-block">
        <Tree items={items} parent={null} layout={picture.layout} top room={rowRoom(picture)} />
      </div>
    </div>
  );

  useLayoutEffect(() => {
    const el = stageRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setStage({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  // The first measure can land before a face has loaded - the monospace one is often only asked for
  // by this very layout - so measure again whenever the laid-out picture changes size. `fonts.ready`
  // is not enough: it can resolve before a face nothing had requested yet starts loading.
  useEffect(() => {
    const el = measureRef.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setFonts(n => n + 1));
    ro.observe(el);
    document.fonts?.addEventListener?.('loadingdone', bump);
    function bump() { setFonts(n => n + 1); }
    return () => { ro.disconnect(); document.fonts?.removeEventListener?.('loadingdone', bump); };
  }, []);

  // Measure where flexbox put every id.
  useLayoutEffect(() => {
    const root = measureRef.current;
    if (!root || stage.w === 0) return;
    // In layout pixels, not screen pixels: the player opens with the dialog scaled a little, and a
    // picture measured mid-entrance would keep every box 1.5% too narrow for its own words.
    const box = root.getBoundingClientRect();
    const s = root.offsetWidth > 0 ? box.width / root.offsetWidth : 1;
    const base = { left: box.left, top: box.top, width: root.offsetWidth, height: root.offsetHeight };
    const rects: Record<string, R> = {};
    root.querySelectorAll<HTMLElement>('[data-pid]').forEach(el => {
      const r = el.getBoundingClientRect();
      rects[el.dataset.pid!] = { x: (r.left - base.left) / s, y: (r.top - base.top) / s, w: Math.ceil(r.width / s), h: Math.ceil(r.height / s) };   // offsetWidth rounds, so round the box up rather than clip a word
    });
    setLayout(last => {
      // A re-measure of the same picture (a resize, a font arriving) keeps what it decided.
      if (last?.picture === picture) return { ...last, rects, w: base.width, h: base.height };
      const had = new Set([...(last?.items.map(i => i.id) ?? []), ...(last?.picture.arrows.map(arrowKey) ?? [])]);
      const enter = new Set([...items.map(i => i.id), ...picture.arrows.map(arrowKey)].filter(x => !had.has(x)));
      return { picture, items, rects, w: base.width, h: base.height, enter };
    });
  }, [picture, items, stage.w, stage.h, fonts]);

  // ---- arrows: redrawn every frame of a move, from the boxes as they travel --------------------

  const pathRefs = useRef(new Map<string, SVGPathElement>());
  const labelRefs = useRef(new Map<string, SVGTextElement>());
  const shown = useRef<Record<string, R>>({});
  const lastD = useRef(new Map<string, string>());
  const prev = useRef<Layout | null>(null);
  const ghostToken = useRef(0);

  useLayoutEffect(() => {
    if (!layout) return;
    const before = prev.current;
    prev.current = layout;
    const from = shown.current;
    const to = layout.rects;
    const moving = before !== null && before.picture !== layout.picture;
    const dur = moving ? morphMs() : 0;

    // What left: kept on screen, leaving from where it last stood, until the move is over.
    if (moving) {
      const ids = new Set(layout.items.map(i => i.id));
      const keys = new Set(layout.picture.arrows.map(arrowKey));
      const token = ++ghostToken.current;
      const list: Ghost[] = [
        ...before.items.filter(i => !ids.has(i.id) && from[i.id])
          .map(i => ({ key: `g-${i.id}-${token}`, item: i, rect: from[i.id] })),
        ...before.picture.arrows.filter(a => !keys.has(arrowKey(a)) && lastD.current.has(arrowKey(a)))
          .map(a => ({ key: `ga-${arrowKey(a)}-${token}`, arrow: a, d: lastD.current.get(arrowKey(a)) })),
      ];
      setGhosts(list);
      if (list.length > 0) window.setTimeout(() => { if (ghostToken.current === token) setGhosts([]); }, Math.max(dur, 300));
    }

    const ease = easing();
    const parent = new Map(layout.items.map(i => [i.id, i.in ?? null]));
    const nested = (a: string, b: string) => {
      for (let p = parent.get(a); p; p = parent.get(p) ?? null) if (p === b) return true;
      return false;
    };
    const draw = (rects: Record<string, R>) => {
      for (const a of layout.picture.arrows) {
        const f = rects[a.from], t = rects[a.to];
        const el = pathRefs.current.get(arrowKey(a));
        if (!f || !t || !el) continue;
        // What an arrow must go around: every box except its own ends and whatever holds or is held by them.
        const others = layout.items
          .filter(i => i.kind !== 'group' && i.id !== a.from && i.id !== a.to
            && !nested(i.id, a.from) && !nested(i.id, a.to) && !nested(a.from, i.id) && !nested(a.to, i.id))
          .map(i => rects[i.id]).filter(Boolean);
        const { d, lx, ly } = route(f, t, others);
        el.setAttribute('d', d);
        lastD.current.set(arrowKey(a), d);
        const label = labelRefs.current.get(arrowKey(a));
        if (label) { label.setAttribute('x', String(lx)); label.setAttribute('y', String(ly)); }
      }
    };

    const t0 = performance.now();
    let raf = 0;
    const step = (now: number) => {
      const p = dur <= 0 ? 1 : Math.min(1, (now - t0) / dur);
      const e = ease(p);
      const cur: Record<string, R> = {};
      for (const id in to) cur[id] = from[id] ? lerpR(from[id], to[id], e) : to[id];
      shown.current = cur;
      draw(cur);
      if (p < 1) raf = requestAnimationFrame(step);
    };
    step(t0);
    return () => cancelAnimationFrame(raf);
  }, [layout]);

  if (!layout) {
    return (
      <div ref={stageRef} className="relative h-full w-full overflow-hidden">
        {measure}
      </div>
    );
  }

  // ---- the camera ------------------------------------------------------------------------------

  const k = stage.w > 0
    ? Math.min(1, (stage.w - PAD * 2) / Math.max(layout.w, 1), (stage.h - PAD * 2) / Math.max(layout.h, 1))
    : 1;
  const ox = (stage.w - layout.w * k) / 2;
  const oy = (stage.h - layout.h * k) / 2;

  // ---- what is being talked about ---------------------------------------------------------------

  const litSet = new Set(lit);
  const within = (id: string, group: string) => {
    for (let p = layout.items.find(i => i.id === id)?.in; p; p = layout.items.find(i => i.id === p)?.in) if (p === group) return true;
    return false;
  };
  /** Lit if it or a group around it is lit; a group around something lit stays readable; the rest dims. */
  const spotOf = (id: string): 'lit' | 'seen' | 'dim' | undefined => {
    if (litSet.size === 0) return undefined;
    if (litSet.has(id) || lit.some(l => within(id, l))) return 'lit';
    if (lit.some(l => within(l, id))) return 'seen';
    return 'dim';
  };

  const ids = new Set(layout.items.map(i => i.id));
  const parts = [
    ...[...layout.items]
      .sort((a, b) => (a.kind === 'group' ? 0 : 1) - (b.kind === 'group' ? 0 : 1) || depthOf(layout.items, a.id) - depthOf(layout.items, b.id))
      .map(i => ({ key: i.id, item: i, rect: layout.rects[i.id], morph: layout.enter.has(i.id) ? 'enter' : undefined })),
    ...ghosts.filter(g => g.item && !ids.has(g.item.id)).map(g => ({ key: g.key, item: g.item!, rect: g.rect!, morph: 'exit' })),
  ].filter(p => p.rect);
  const arrowKeys = new Set(layout.picture.arrows.map(arrowKey));

  return (
    <div ref={stageRef} className="relative h-full w-full overflow-hidden">
      {measure}
      <div
        className="orbit-morph absolute left-0 top-0 origin-top-left"
        style={{ width: layout.w, height: layout.h, transform: `translate(${ox}px, ${oy}px) scale(${k})` }}
      >
        {parts.map(({ key, item, rect, morph }) => {
          const f = frame(item);
          const spot = morph === 'exit' ? undefined : spotOf(item.id);
          return (
            <div
              key={key}
              className="orbit-morph absolute left-0 top-0"
              data-morph={morph}
              style={{ width: rect.w, height: rect.h, transform: `translate(${rect.x}px, ${rect.y}px)`, zIndex: item.kind === 'group' ? 0 : 2 }}
            >
              <div className={`h-full w-full${spot ? ' orbit-spot' : ''}`} data-spot={spot}>
                <div className={`orbit-morph h-full w-full ${f.className}`} style={f.style}>
                  <Words item={item} />
                </div>
              </div>
            </div>
          );
        })}

        <svg className="pointer-events-none absolute left-0 top-0 overflow-visible" width={layout.w} height={layout.h} style={{ zIndex: 1 }}>
          <defs>
            {CHANGES.map(c => (
              <marker key={c} id={`${uid}-${c}`} viewBox="0 0 10 10" refX="8" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">
                <path d="M0,0 L10,5 L0,10 z" fill={tone(c).line} />
              </marker>
            ))}
          </defs>
          {layout.picture.arrows.map(a => {
            const key = arrowKey(a);
            const spot = litSet.size === 0 ? undefined : litSet.has(a.from) || litSet.has(a.to) ? 'lit' : 'dim';
            return (
              <g key={key} className={spot ? 'orbit-spot' : undefined} data-spot={spot}>
                <path
                  ref={el => { if (el) pathRefs.current.set(key, el); else pathRefs.current.delete(key); }}
                  className="orbit-morph"
                  data-morph={layout.enter.has(key) ? 'enter' : undefined}
                  pathLength={1}
                  fill="none"
                  stroke={tone(a.change).line}
                  strokeWidth={a.change === 'unchanged' ? 1.6 : 2.2}
                  strokeDasharray={a.change === 'removed' ? '0.02 0.015' : undefined}
                  markerEnd={`url(#${uid}-${a.change})`}
                />
                {a.label && (
                  <text
                    ref={el => { if (el) labelRefs.current.set(key, el); else labelRefs.current.delete(key); }}
                    className={layout.enter.has(key) ? 'orbit-morph' : undefined}
                    data-morph={layout.enter.has(key) ? 'enter' : undefined}
                    textAnchor="middle" fontSize={12} fill="var(--w-t400)"
                    stroke="var(--w-void)" strokeWidth={4} paintOrder="stroke" style={{ fontFamily: MONO }}
                  >
                    {a.label}
                  </text>
                )}
              </g>
            );
          })}
          {ghosts.filter(g => g.arrow && !arrowKeys.has(arrowKey(g.arrow))).map(g => (
            <path key={g.key} className="orbit-morph" data-morph="exit" d={g.d} fill="none"
              stroke={tone(g.arrow!.change).line} strokeWidth={1.6} />
          ))}
        </svg>
      </div>
    </div>
  );
}

const CHANGES: PictureChange[] = ['unchanged', 'new', 'changed', 'removed'];
