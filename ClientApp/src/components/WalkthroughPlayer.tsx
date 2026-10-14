import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type {
  Analysis, ClassroomTicket, Deck, DeckBeat, DeckCode, DeckImage, DeckScene, Doc, Rect, Walkthrough,
} from '../api/classroom';
import { blockAt, clipUrl, getWalkthrough, writeWalkthrough } from '../api/classroom';
import { AnalysisContext, Callout, Question, UserFlow } from './AnalysisBlocks';
import { lineCls } from './PatchView';
import { Chip, GhostButton, PrimaryButton } from './ui';
import { useVeil } from '../hooks/useVeil';
import { chamfer, DISPLAY, MONO } from './orbit';

/**
 * A Classroom analysis, played as a narrated slideshow: the synopsis you listen to before reading.
 *
 * Everything on screen is the analysis's own material - its flow diagram, its callouts, the ticket's
 * QA shots and attachments, code cut from the branch's real patch - pointed at by a script the server
 * has already checked (see WalkthroughValidator). This file only plays it: one voiced line per beat,
 * and each beat's cue says what to light, zoom or reveal while that line is spoken.
 */

// ---- what a deck narrates ---------------------------------------------------------------------

/**
 * What a deck narrates: one ticket's analysis, or a report on a unit of a plan. The player shows the
 * same scenes for either; this is the little that differs - what the title card says, which
 * screenshots the shot numbers mean, and where the walkthrough is read and rewritten.
 */
export interface DeckSubject {
  /** Changes when the subject does: what the card re-reads its walkthrough on. */
  key: string;
  /** The big label on the title card and the player's header: a ticket's key, or the unit. */
  mark: string;
  title: string;
  headline: string;
  tags: { text: string; mono?: boolean }[];
  doc: Doc;
  shots: { order: number; caption: string; imageUrl: string }[];
  /** Over the quotes of an "ask" scene: where the words come from. */
  askFrom: string;
  /** How a shot is named under it. */
  shotName: (order: number) => string;
  /** What the "write" note says while one is being written for the first time. */
  writing: string;
  load: () => Promise<Walkthrough>;
  write: () => Promise<{ message: string }>;
}

/** A ticket's analysis, as the player narrates it. */
export function ticketSubject(analysis: Analysis, ticket: ClassroomTicket): DeckSubject {
  const m = analysis.meta;
  return {
    key: `analysis-${m.id}`,
    mark: ticket.issueKey ?? `#${ticket.id}`,
    title: ticket.title,
    headline: m.headline,
    tags: [
      { text: m.phaseAtGeneration },
      ...(m.branch ? [{ text: m.branch, mono: true }] : []),
      ...(m.headSha ? [{ text: m.headSha.slice(0, 7), mono: true }] : []),
    ],
    doc: analysis.document,
    shots: ticket.qaShots.map(s => ({ order: s.order, caption: s.caption, imageUrl: s.imageUrl })),
    askFrom: 'From the ticket',
    shotName: order => `QA shot ${order + 1}`,
    writing: 'writing the walkthrough — it follows the analysis by a few minutes',
    load: () => getWalkthrough(m.id),
    write: () => writeWalkthrough(m.id),
  };
}

// ---- the timeline ----------------------------------------------------------------------------

/** A beat, placed on the deck's clock. */
interface Placed extends DeckBeat { si: number; bi: number; start: number; pause: number }

/** Silence after a line, and a longer one when the scene changes, so a cut does not land mid-breath. */
const BEAT_PAUSE = 0.28;
const SCENE_PAUSE = 0.7;

function place(deck: Deck): { beats: Placed[]; total: number } {
  const beats: Placed[] = [];
  let t = 0;
  deck.scenes.forEach((s, si) => s.beats.forEach((b, bi) => {
    const pause = bi === s.beats.length - 1 ? SCENE_PAUSE : BEAT_PAUSE;
    beats.push({ ...b, si, bi, start: t, pause });
    t += b.dur + pause;
  }));
  return { beats, total: t };
}

/**
 * What a scene looks like at one beat. Replayed from the scene's first beat so a jump lands in the
 * right picture: a reveal or a card, once shown, stays shown; a highlight belongs to its beat.
 */
interface State {
  lines: number[] | null;
  nodes: string[];
  seen: string[];
  quotes: number[];
  image: string | null;
  zoom: Rect | null;
  box: Rect | null;
  revealed: boolean;
  show: number;
}

function stateAt(deck: Deck, si: number, bi: number): State {
  const scene = deck.scenes[si];
  const seen = new Set<string>();
  // Consecutive flow scenes over one diagram share their trail: the second picks up where the first left off.
  for (let k = si - 1; k >= 0 && sameCanvas(deck.scenes[k], scene); k--)
    deck.scenes[k].beats.forEach(b => b.cue?.nodes?.forEach(n => seen.add(n)));
  let image = scene.images?.[0]?.name ?? null;
  let revealed = false;
  let show = 0;
  for (let j = 0; j < bi; j++) {
    const c = scene.beats[j].cue;
    c?.nodes?.forEach(n => seen.add(n));
    if (c?.image) image = c.image;
    if (c?.reveal) revealed = true;
    if (c?.show != null) show = c.show;
  }
  const c = scene.beats[bi].cue;
  if (c?.image) image = c.image;
  if (c?.reveal) revealed = true;
  if (c?.show != null) show = c.show;
  const nodes = c?.nodes ?? [];
  nodes.forEach(n => seen.delete(n));
  return {
    lines: c?.lines && c.lines.length === 2 ? c.lines : null,
    nodes, seen: [...seen], quotes: c?.quotes ?? [], image,
    zoom: c?.zoom ?? null, box: c?.box ?? null, revealed, show,
  };
}

const sameCanvas = (a: DeckScene, b: DeckScene) => a.kind === 'flow' && b.kind === 'flow' && a.block === b.block;

/** A scene's identity for `.orbit-swap`: two flow scenes over one diagram are one picture, not a cut. */
const canvasKey = (deck: Deck, si: number) => {
  let k = si;
  while (k > 0 && sameCanvas(deck.scenes[k - 1], deck.scenes[si])) k--;
  return `scene-${k}`;
};

const clock = (s: number) => `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;

const reducedMotion = () => window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;

/**
 * Keep whatever is lit inside a scrolling frame in view. A diagram or a long snippet is often taller
 * than the stage, and the narration is pointing at a part of it the reader would otherwise have to
 * go and find.
 */
function useFollow(ref: React.RefObject<HTMLElement | null>, selector: string, key: string) {
  useLayoutEffect(() => {
    const box = ref.current;
    if (!box) return;
    const lit = [...box.querySelectorAll(selector)];
    if (lit.length === 0) return;
    const frame = box.getBoundingClientRect();
    const rects = lit.map(e => e.getBoundingClientRect());
    const top = Math.min(...rects.map(r => r.top)) - frame.top + box.scrollTop;
    const bottom = Math.max(...rects.map(r => r.bottom)) - frame.top + box.scrollTop;
    const left = Math.min(...rects.map(r => r.left)) - frame.left + box.scrollLeft;
    const right = Math.max(...rects.map(r => r.right)) - frame.left + box.scrollLeft;
    box.scrollTo({
      top: Math.max(0, (top + bottom) / 2 - box.clientHeight / 2),
      left: Math.max(0, (left + right) / 2 - box.clientWidth / 2),
      behavior: reducedMotion() ? 'auto' : 'smooth',
    });
  }, [ref, selector, key]);
}

// ---- the player -------------------------------------------------------------------------------

export function WalkthroughPlayer({ deck, subject, onClose }: {
  deck: Deck; subject: DeckSubject; onClose: () => void;
}) {
  const { fx, dismiss } = useVeil(onClose);
  const { beats, total } = useMemo(() => place(deck), [deck]);
  const [cur, setCur] = useState(0);
  // It opens on a click, and that click is the gesture a browser wants before it will play sound.
  const [playing, setPlaying] = useState(true);
  const [finished, setFinished] = useState(false);
  const [captions, setCaptions] = useState(true);
  const [rate, setRate] = useState(1);

  const audio = useRef<HTMLAudioElement>(new Audio());
  const timer = useRef<number | null>(null);
  const timeRef = useRef<HTMLSpanElement>(null);
  const fillRefs = useRef<(HTMLSpanElement | null)[]>([]);

  const beat = beats[cur];
  const scene = deck.scenes[beat.si];
  const state = useMemo(() => stateAt(deck, beat.si, beat.bi), [deck, beat.si, beat.bi]);

  const clear = () => { if (timer.current) { window.clearTimeout(timer.current); timer.current = null; } };

  const go = useCallback((i: number) => {
    clear();
    setFinished(false);
    setCur(Math.max(0, Math.min(beats.length - 1, i)));
  }, [beats.length]);

  // The engine. One voiced line per beat; a beat that could not be voiced is timed at reading pace
  // instead, so the deck still plays through with its caption where the voice would have been.
  useEffect(() => {
    const a = audio.current;
    clear();
    if (!playing) { a.pause(); return; }

    const next = () => {
      timer.current = window.setTimeout(() => {
        if (cur < beats.length - 1) setCur(cur + 1);
        else { setPlaying(false); setFinished(true); }
      }, (beat.pause * 1000) / rate);
    };

    if (beat.clip) {
      const src = clipUrl(beat.clip);
      if (a.dataset.clip !== beat.clip) { a.src = src; a.dataset.clip = beat.clip; }
      a.playbackRate = rate;
      a.onended = next;
      a.play().catch(() => setPlaying(false));
    } else {
      a.pause();
      a.onended = null;
      timer.current = window.setTimeout(next, (beat.dur * 1000) / rate);
    }
    return clear;
  }, [cur, playing, beat, beats.length, rate]);

  useEffect(() => () => { audio.current.pause(); clear(); }, []);

  // The clock and the scene bar, written straight to the DOM every frame rather than through state:
  // re-rendering a diagram sixty times a second to move a progress bar would be the wrong trade.
  useEffect(() => {
    let raf = 0;
    const tick = () => {
      const within = beat.clip && audio.current.dataset.clip === beat.clip
        ? Math.min(audio.current.currentTime, beat.dur) : 0;
      const now = finished ? total : beat.start + within;
      if (timeRef.current) timeRef.current.textContent = `${clock(now)} / ${clock(total)}`;
      deck.scenes.forEach((_, si) => {
        const bs = beats.filter(b => b.si === si);
        const a = bs[0].start, z = bs[bs.length - 1].start + bs[bs.length - 1].dur + bs[bs.length - 1].pause;
        const el = fillRefs.current[si];
        if (el) el.style.width = `${Math.max(0, Math.min(1, (now - a) / (z - a))) * 100}%`;
      });
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, [beat, beats, deck.scenes, total, finished]);

  const toggle = useCallback(() => {
    if (finished) { go(0); setPlaying(true); return; }
    setPlaying(p => !p);
  }, [finished, go]);

  const back = useCallback(() => {
    // Back replays the line when it is already under way, and only then steps to the previous one.
    if (beat.clip && audio.current.currentTime > 1.5) { audio.current.currentTime = 0; return; }
    go(cur - 1);
  }, [beat.clip, cur, go]);

  useEffect(() => {
    audio.current.playbackRate = rate;
  }, [rate]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') dismiss();
      else if (e.key === ' ') { e.preventDefault(); toggle(); }
      else if (e.key === 'ArrowRight') go(cur + 1);
      else if (e.key === 'ArrowLeft') back();
      else if (e.key.toLowerCase() === 'c') setCaptions(c => !c);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [dismiss, toggle, go, back, cur]);

  return (
    <div className="orbit-veil fixed inset-0 z-50 flex items-center justify-center p-4" data-fx={fx}>
      <div className="orbit-dim absolute inset-0 bg-black/75" onClick={dismiss} />
      <div className="orbit-modal relative flex flex-col w-full max-w-[1240px] h-[min(900px,94vh)] overflow-hidden">
        <header className="flex items-center gap-3 px-5 py-3 border-b border-hair">
          <span className="text-[13px] font-bold text-amber-300" style={{ fontFamily: DISPLAY, letterSpacing: '.06em' }}>
            {subject.mark}
          </span>
          <span className="h-4 w-px bg-hair" />
          <span className="text-[12px] uppercase tracking-[calc(var(--w-track)*0.08em)] text-gray-300">{scene.chapter}</span>
          <span className="ml-auto text-[11px] tabular-nums text-gray-500">
            {String(beat.si + 1).padStart(2, '0')} / {String(deck.scenes.length).padStart(2, '0')}
          </span>
          <GhostButton onClick={dismiss} title="Close (Esc)">Close</GhostButton>
        </header>

        <AnalysisContext shots={subject.shots}>
          <div className="relative flex-1 min-h-0">
            <div key={canvasKey(deck, beat.si)} className="orbit-swap absolute inset-0 px-8 py-6">
              <SceneView scene={scene} state={state} subject={subject} beat={beat} />
            </div>
          </div>
        </AnalysisContext>

        <div className="min-h-[64px] px-10 flex items-center justify-center text-center border-t border-hair">
          {captions && (
            <p key={cur} className="orbit-swap text-[17px] leading-snug text-gray-100 max-w-[70ch]">{beat.say}</p>
          )}
        </div>

        <footer className="px-5 pt-2 pb-4 flex flex-col gap-2.5">
          <div className="flex items-center gap-2">
            <GhostButton onClick={back} title="Previous (Left)">Back</GhostButton>
            <PrimaryButton onClick={toggle} title="Play / pause (Space)">
              {finished ? 'Replay' : playing ? 'Pause' : 'Play'}
            </PrimaryButton>
            <GhostButton onClick={() => go(cur + 1)} disabled={cur >= beats.length - 1} title="Next (Right)">Next</GhostButton>
            <span className="w-3" />
            <Chip on={captions} onClick={() => setCaptions(c => !c)} title="Captions (C)">CC</Chip>
            <Chip on={rate !== 1} onClick={() => setRate(r => (r === 1 ? 1.25 : r === 1.25 ? 1.5 : 1))} title="Speed">
              {rate}×
            </Chip>
            <span ref={timeRef} className="ml-auto text-[12px] tabular-nums text-gray-500" />
          </div>
          <div className="flex gap-[3px]">
            {deck.scenes.map((s, si) => {
              const len = beats.filter(b => b.si === si).reduce((n, b) => n + b.dur + b.pause, 0);
              return (
                <button
                  key={si}
                  onClick={() => go(beats.findIndex(b => b.si === si))}
                  aria-current={si === beat.si ? 'true' : undefined}
                  title={s.chapter}
                  className="orbit-pick pick-lift relative h-7 overflow-hidden border border-hair bg-gray-950/60 text-left aria-[current]:border-amber-700/70"
                  style={{ flex: len, clipPath: chamfer(6) }}
                >
                  <span ref={el => { fillRefs.current[si] = el; }} className="absolute inset-y-0 left-0 bg-amber-500/25" />
                  <span className="relative block truncate px-2 text-[10px] uppercase tracking-[calc(var(--w-track)*0.06em)] text-gray-400">
                    {s.chapter}
                  </span>
                </button>
              );
            })}
          </div>
        </footer>
      </div>
    </div>
  );
}

// ---- scenes -----------------------------------------------------------------------------------

function SceneView({ scene, state, subject, beat }: {
  scene: DeckScene; state: State; subject: DeckSubject; beat: Placed;
}) {
  switch (scene.kind) {
    case 'title': return <TitleScene subject={subject} later={beat.bi > 0} />;
    case 'ask': return <AskScene scene={scene} state={state} from={subject.askFrom} />;
    case 'shot': return <ShotScene scene={scene} state={state} subject={subject} />;
    case 'code': return scene.code ? <CodeScene code={scene.code} state={state} /> : null;
    case 'flow': return <FlowScene scene={scene} state={state} doc={subject.doc} />;
    case 'cards': return <CardsScene scene={scene} state={state} doc={subject.doc} />;
    case 'question': return <QuestionScene scene={scene} state={state} doc={subject.doc} />;
    default: return null;
  }
}

function TitleScene({ subject, later }: { subject: DeckSubject; later: boolean }) {
  return (
    <div className="h-full flex flex-col justify-center px-12">
      <div className="text-[64px] leading-none font-bold text-amber-300" style={{ fontFamily: DISPLAY, letterSpacing: '.04em' }}>
        {subject.mark}
      </div>
      <div className="mt-4 text-[26px] font-medium text-gray-100 max-w-[40ch]">{subject.title}</div>
      <p className="orbit-spot spot-flat mt-6 text-[17px] leading-relaxed text-gray-400 max-w-[70ch]" data-spot={later ? 'lit' : 'off'}>
        {subject.headline}
      </p>
      <div className="mt-8 flex flex-wrap gap-2 text-[11px] uppercase tracking-[calc(var(--w-track)*0.08em)]">
        {subject.tags.map((t, i) => <Tag key={i} mono={t.mono}>{t.text}</Tag>)}
      </div>
    </div>
  );
}

function Tag({ children, mono }: { children: ReactNode; mono?: boolean }) {
  return (
    <span className="border border-hair bg-gray-950/60 px-2.5 py-1 text-gray-400"
      style={{ clipPath: chamfer(6), fontFamily: mono ? MONO : undefined, textTransform: mono ? 'none' : undefined }}>
      {children}
    </span>
  );
}

function AskScene({ scene, state, from }: { scene: DeckScene; state: State; from: string }) {
  const images = scene.images ?? [];
  return (
    <div className="h-full flex flex-col gap-4">
      <div className="text-[11px] uppercase tracking-[calc(var(--w-track)*0.12em)] text-gray-500">{from}</div>
      <div className="flex flex-col gap-2">
        {(scene.quotes ?? []).map((q, i) => (
          <blockquote
            key={i}
            className="orbit-spot spot-flat m-0 border-l-2 border-hair bg-gray-950/50 px-4 py-2.5 text-[16px] leading-relaxed text-gray-200 data-[spot=lit]:border-amber-500"
            data-spot={state.quotes.length === 0 ? 'seen' : state.quotes.includes(i) ? 'lit' : 'dim'}
          >
            {q}
          </blockquote>
        ))}
      </div>
      {images.length > 0 && (
        <figure className="m-0 flex-1 min-h-0 flex flex-col gap-1.5">
          <div className="relative flex-1 min-h-0">
            {images.map(img => (
              <div key={img.name} className="orbit-spot spot-flat absolute inset-0" data-spot={img.name === state.image ? 'lit' : 'off'}>
                <Framed image={img} zoom={img.name === state.image ? state.zoom : null} box={img.name === state.image ? state.box : null} />
              </div>
            ))}
          </div>
          <figcaption className="text-[11px] text-gray-500">
            Jira attachment · {state.image ?? images[0].name}
          </figcaption>
        </figure>
      )}
    </div>
  );
}

function ShotScene({ scene, state, subject }: { scene: DeckScene; state: State; subject: DeckSubject }) {
  const img = scene.images?.[0];
  const caption = subject.shots.find(s => s.order === scene.shot)?.caption;
  if (!img) return null;
  return (
    <figure className="m-0 h-full flex flex-col gap-2">
      <div className="relative flex-1 min-h-0">
        <Framed image={img} zoom={state.zoom} box={state.box} />
      </div>
      {caption && <figcaption className="text-[12px] leading-relaxed text-gray-500 max-w-[110ch]">{subject.shotName(scene.shot ?? 0)} · {caption}</figcaption>}
    </figure>
  );
}

/**
 * A picture in a fixed frame, and the camera on it. Whole and centred by default; zoomed so a region
 * fills the frame when the cue names one. The box rides inside the picture, in its own pixels, so it
 * stays on the row it marks however the frame is sized.
 */
function Framed({ image, zoom, box, still }: {
  image: DeckImage; zoom: Rect | null; box: Rect | null;
  /** A thumbnail: framed once, no camera move - it would only replay on every page load. */
  still?: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState({ w: 0, h: 0 });
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver(() => setSize({ w: el.clientWidth, h: el.clientHeight }));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  let s = Math.min(size.w / image.w, size.h / image.h) || 0;
  let tx = (size.w - image.w * s) / 2, ty = (size.h - image.h * s) / 2;
  if (zoom && size.w > 0) {
    s = Math.min(size.w / zoom.w, size.h / zoom.h) * 0.94;
    tx = size.w / 2 - (zoom.x + zoom.w / 2) * s;
    ty = size.h / 2 - (zoom.y + zoom.h / 2) * s;
  }

  return (
    <div ref={ref} className="absolute inset-0 overflow-hidden border border-hair bg-white" style={{ clipPath: chamfer(10) }}>
      <div
        className={`${still ? '' : 'orbit-pan '}absolute left-0 top-0 origin-top-left`}
        style={{ width: image.w, height: image.h, transform: `translate(${tx}px, ${ty}px) scale(${s})` }}
      >
        <img src={image.url} alt="" className="block" style={{ width: image.w, height: image.h }} />
        <div
          className="orbit-spot spot-flat absolute border-[3px]"
          data-spot={box ? 'lit' : 'off'}
          style={box ? {
            left: box.x, top: box.y, width: box.w, height: box.h,
            borderColor: 'var(--w-active)',
            boxShadow: '0 0 0 9999px rgb(0 0 0 / .32), 0 0 22px rgb(var(--w-active-rgb) / .6)',
          } : undefined}
        />
      </div>
    </div>
  );
}

/**
 * Code on a slide: every line as git marked it. Added lines are green and removed ones red on every
 * slide, whatever is being talked about, because "is this new?" must never depend on the narration.
 * A highlight is a separate layer over that, and the added lines can be held back for a reveal.
 */
function CodeScene({ code, state }: { code: DeckCode; state: State }) {
  const ref = useRef<HTMLDivElement>(null);
  const visible = code.lines.filter(l => !(code.reveal && !state.revealed && l.m === '+'));
  const inLit = (n: number | null) => state.lines != null && n != null && n >= state.lines[0] && n <= state.lines[1];
  const width = String(Math.max(...code.lines.map(l => l.n ?? l.o ?? 0))).length;
  useFollow(ref, '[data-spot="lit"]', `${state.lines?.join('-')}-${state.revealed}`);

  return (
    <figure className="m-0 h-full flex flex-col border border-hair bg-panel overflow-hidden" style={{ clipPath: chamfer(10) }}>
      <figcaption className="flex items-center gap-3 border-b border-hair bg-gray-900/50 px-4 py-2 text-[12px]">
        <span className="font-mono text-gray-400 truncate">{code.path}</span>
        <span className="flex gap-2 text-[12px] tabular-nums">
          {code.added > 0 && <span className="text-emerald-400">+{code.added}</span>}
          {code.removed > 0 && <span className="text-red-400">−{code.removed}</span>}
          {code.added === 0 && code.removed === 0 && <span className="text-gray-600">unchanged, for context</span>}
        </span>
        {code.language && <span className="ml-auto text-[10px] uppercase text-gray-600">{code.language}</span>}
      </figcaption>
      <div ref={ref} className="flex-1 min-h-0 overflow-auto">
        <pre className="m-0 py-3 font-mono text-[14px] leading-[1.6]">
          {visible.map((l, i) => {
            const spot = state.lines == null ? undefined : inLit(l.n) ? 'lit' : 'dim';
            return (
              <div
                key={`${l.m}${l.n ?? `o${l.o}`}-${i}`}
                className={`flex border-l-2 ${spot ? 'orbit-spot spot-flat' : ''} ${spot === 'lit' ? 'border-amber-500' : 'border-transparent'} ${l.m === '+' && code.reveal ? 'orbit-swap' : ''} ${lineCls(l.m + l.t)}`}
                data-spot={spot}
              >
                <span className="select-none shrink-0 pr-2 text-right tabular-nums opacity-60" style={{ width: `${width + 2}ch` }}>
                  {l.n ?? l.o}
                </span>
                <span className="select-none shrink-0 w-[2ch] text-center">{l.m === ' ' ? '' : l.m === '-' ? '−' : '+'}</span>
                <span className="whitespace-pre pr-4">{l.t || ' '}</span>
              </div>
            );
          })}
        </pre>
      </div>
    </figure>
  );
}

function FlowScene({ scene, state, doc }: { scene: DeckScene; state: State; doc: Doc }) {
  const ref = useRef<HTMLDivElement>(null);
  const block = blockAt(doc, scene.block);
  useFollow(ref, '[data-flow-spot][data-spot="lit"]', state.nodes.join(','));
  if (!block) return null;
  return (
    <div ref={ref} className="h-full overflow-auto">
      <UserFlow block={{ ...block, note: undefined }} spot={{ lit: state.nodes, seen: state.seen }} />
    </div>
  );
}

function CardsScene({ scene, state, doc }: { scene: DeckScene; state: State; doc: Doc }) {
  const cards = (scene.blocks ?? []).map(a => blockAt(doc, a)).filter(b => b != null);
  return (
    <div className="h-full flex flex-col justify-center gap-3 max-w-[92ch] mx-auto">
      {cards.map((b, i) => (
        <div
          key={i}
          className="orbit-spot spot-flat [&_p]:text-[15px]"
          data-spot={i < state.show - 1 ? 'seen' : i === state.show - 1 ? 'lit' : 'off'}
        >
          <Callout block={b!} />
        </div>
      ))}
    </div>
  );
}

function QuestionScene({ scene, state, doc }: { scene: DeckScene; state: State; doc: Doc }) {
  const block = blockAt(doc, scene.block);
  if (!block) return null;
  return (
    <div className="h-full flex flex-col justify-center max-w-[80ch] mx-auto [&_p]:text-[20px]">
      <div className="orbit-spot spot-flat" data-spot={state.show >= 1 || scene.beats.every(b => b.cue?.show == null) ? 'lit' : 'off'}>
        <Question block={block} />
      </div>
    </div>
  );
}

// ---- on the Classroom page ---------------------------------------------------------------------

/** The picture a deck is best recognised by: its first screenshot, framed the way its narration frames it. */
function cover(deck: Deck): { image: DeckImage; zoom: Rect | null } | null {
  for (const kind of ['shot', 'ask'] as const) {
    const scene = deck.scenes.find(s => s.kind === kind && s.images?.length);
    if (!scene) continue;
    const beat = scene.beats.find(b => b.cue?.zoom) ?? scene.beats[0];
    const image = scene.images!.find(i => i.name === beat.cue?.image) ?? scene.images![0];
    return { image, zoom: beat.cue?.zoom ?? null };
  }
  return null;
}

/**
 * The walkthrough's place on an analysis: a card with a still from the deck, sitting between the
 * headline and the document so it is the first thing offered - the walkthrough is meant to be heard
 * BEFORE the reading, and a link tucked into the meta row was being read past. It also says when
 * one is being written, and offers to write one for an analysis from before they existed.
 */
export function WalkthroughCard({ subject }: { subject: DeckSubject }) {
  const [w, setW] = useState<Walkthrough | null>(null);
  const [open, setOpen] = useState(false);
  const [asked, setAsked] = useState<string | null>(null);

  // The subject is rebuilt on every render of the page around it; its key is what says it changed.
  const current = useRef(subject);
  current.current = subject;
  const load = useCallback(() => current.current.load().then(setW).catch(() => {}), [subject.key]);
  useEffect(() => { setW(null); setAsked(null); load(); }, [load]);

  // Poll while one is being written - it follows its analysis in the same queue slot, a few minutes behind.
  const pending = w?.status === 'Pending' || asked === 'Queued.';
  useEffect(() => {
    if (!pending) return;
    const t = window.setInterval(load, 5000);
    return () => window.clearInterval(t);
  }, [pending, load]);
  useEffect(() => { if (w && w.status !== 'Pending' && asked === 'Queued.' && w.generatedAt) setAsked(null); }, [w, asked]);

  const write = () => subject.write().then(
    r => { setAsked(r.message); load(); },
    e => setAsked(e instanceof Error ? e.message : 'Could not queue it.'),
  );

  if (!w) return null;
  const deck = w.deck;
  const still = deck ? cover(deck) : null;

  // The small print under the card: what else is true about it, and the one way to act on that.
  const notes = (
    <div className="mt-1.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px]">
      {pending && (
        <span className="flex items-center gap-1.5 text-gray-400">
          <span className="h-1.5 w-1.5 rounded-full animate-pulse" style={{ background: 'var(--w-active)' }} />
          {deck ? 'rewriting the walkthrough…' : subject.writing}
        </span>
      )}
      {!pending && w.status === 'Failed' && (
        <span className="text-rose-400/90" title={w.error ?? undefined}>
          {deck ? 'the last rewrite failed' : 'the walkthrough could not be written'}{w.error ? `: ${w.error}` : ''}
        </span>
      )}
      {!pending && deck && w.status === 'Ready' && w.error && <span className="text-amber-500/90">{w.error}</span>}
      {!pending && deck && w.dropped > 0 && (
        <span className="text-gray-600">{w.dropped} reference{w.dropped === 1 ? '' : 's'} in the script did not check out and {w.dropped === 1 ? 'was' : 'were'} left out</span>
      )}
      {!pending && (
        <button onClick={write} className="ml-auto text-gray-600 hover:text-gray-300 transition-colors">
          {w.status === 'none' ? 'write a walkthrough' : w.status === 'Failed' && !deck ? 'retry' : 'rewrite'}
        </button>
      )}
      {asked && asked !== 'Queued.' && <span className="text-rose-400/90">{asked}</span>}
    </div>
  );

  if (!deck) return <div className="mb-7">{notes}</div>;

  const beats = deck.scenes.reduce((n, s) => n + s.beats.length, 0);

  return (
    <section className="mb-7">
      <button
        onClick={() => setOpen(true)}
        title="Play the narrated walkthrough"
        className="orbit-tap group flex w-full items-stretch gap-5 border border-amber-800/50 bg-amber-950/15 p-3 text-left hover:border-amber-600/70"
        style={{ clipPath: chamfer(12) }}
      >
        <div className="relative w-[248px] shrink-0 aspect-video overflow-hidden bg-gray-950" style={{ clipPath: chamfer(9) }}>
          {still
            ? <Framed image={still.image} zoom={still.zoom} box={null} still />
            : (
              <div className="absolute inset-0 flex items-center justify-center text-[22px] font-bold text-amber-300/80"
                style={{ fontFamily: DISPLAY, letterSpacing: '.06em' }}>
                {subject.mark}
              </div>
            )}
          {/* A shade and a play mark over the still, so it reads as something to press, not a figure. */}
          <div className="absolute inset-0 bg-gradient-to-t from-black/60 via-black/25 to-black/10" />
          <div
            className="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 flex h-12 w-12 items-center justify-center text-[18px]"
            style={{
              clipPath: chamfer(9),
              background: 'linear-gradient(120deg,var(--w-accent-lit),var(--w-accent-deep))',
              color: 'var(--w-on-accent)',
            }}
          >
            ▶
          </div>
          <span className="absolute bottom-1.5 right-2 text-[11px] font-semibold tabular-nums text-gray-100">
            {clock(w.audioSeconds)}
          </span>
        </div>

        <div className="min-w-0 flex-1 flex flex-col justify-center gap-1.5 py-1">
          <div className="text-[10px] font-semibold uppercase tracking-[calc(var(--w-track)*0.14em)] text-amber-300/90">
            Narrated walkthrough
          </div>
          <div className="text-[16px] font-medium text-gray-100">
            Listen before you read — {clock(w.audioSeconds)}
          </div>
          <div className="text-[12px] text-gray-500">
            {deck.scenes.length} scenes · {beats} lines · captions on · space to pause
          </div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            {deck.scenes.map((s, i) => (
              <span key={i} className="border border-hair bg-gray-950/50 px-1.5 py-0.5 text-[10px] text-gray-400"
                style={{ clipPath: chamfer(4) }}>
                {s.chapter}
              </span>
            ))}
          </div>
        </div>
      </button>
      {notes}
      {open && <WalkthroughPlayer deck={deck} subject={subject} onClose={() => setOpen(false)} />}
    </section>
  );
}
