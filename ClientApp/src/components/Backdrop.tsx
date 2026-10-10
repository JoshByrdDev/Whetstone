import { useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { useTheme } from '../theme';
import type { BackdropLayer, Motion } from '../theme';

/**
 * The one background behind the whole app.
 *
 * Mounted once at the root, behind every tab. Switching tabs never touches it — that is the point:
 * the backdrop is the thing that does not move while the content does. What it draws is the theme's
 * business, not this file's; this file knows how to paint the three kinds of thing a theme can ask
 * for and nothing about what any particular one looks like.
 *
 *   base     a colour, painted always
 *   still    CSS planes, painted always — a texture, a fixed vignette. Cheap by definition
 *   motion   the animated half, painted only while the setting below is on. Either a stack of CSS
 *            planes, or the WebGL cloud field the space theme is built on
 *
 * The animated half can be switched off outright from Settings, for machines with no GPU budget to
 * spare. That preference is per-machine and lives in localStorage rather than the server config: it
 * is a statement about the hardware in front of you, not about the system, and reading it
 * synchronously means the first paint is already right instead of flashing a nebula before the
 * setting arrives.
 */

const SKY_KEY = 'whetstone.sky3d';
/** Fired on the window when the preference changes, so a toggle anywhere repaints the backdrop now. */
const SKY_EVENT = 'whetstone-sky-change';

/** On unless this machine has been told otherwise — the animated background is the default. */
export function skyEnabled(): boolean {
  try {
    return localStorage.getItem(SKY_KEY) !== 'off';
  } catch {
    return true; // private mode, blocked storage: the default stands
  }
}

export function setSkyEnabled(on: boolean): void {
  try {
    localStorage.setItem(SKY_KEY, on ? 'on' : 'off');
  } catch {
    /* the toggle still applies to this page; it just will not be remembered */
  }
  window.dispatchEvent(new Event(SKY_EVENT));
}

/** Reads the preference and tracks changes, including ones made in another tab. */
export function useSkyEnabled(): boolean {
  const [on, setOn] = useState(skyEnabled);
  useEffect(() => {
    const sync = () => setOn(skyEnabled());
    window.addEventListener(SKY_EVENT, sync);
    window.addEventListener('storage', sync);
    return () => {
      window.removeEventListener(SKY_EVENT, sync);
      window.removeEventListener('storage', sync);
    };
  }, []);
  return on;
}

/**
 * How far every parallax wrapper is grown past the viewport, on all four sides.
 *
 * It has to clear the largest displacement any theme asks for, or a layer travels far enough to
 * pull its own edge into frame — a photograph laid `cover` over exactly the viewport and then
 * nudged twelve pixels left is a photograph twelve pixels short on the right.
 *
 * That displacement is *travel plus bias*, not travel. A theme that cuts one photograph into two
 * planes has to hold the near copy permanently outboard of the far one, or the far one's own
 * silhouette shows past it, and that standing offset is written into the layer's `transform` where
 * this file cannot see it. Snow sets the number: forty-two of travel and fifty-two of bias is
 * ninety-four at full deflection, and a hundred and twelve leaves eighteen over. Sizing on travel
 * alone is what this constant used to do, and it put a vertical seam down the left of the snow
 * theme — the near piste ran out of picture and uncovered the far copy — with the same fault
 * latent in the coast at six pixels, which is small enough that nobody had caught it.
 *
 * One number for every layer, rather than a margin scaled to each layer's own travel, and that is
 * the half that matters for depth. `cover` resolves against the box it is given, so two layers with
 * different boxes are two differently *scaled* copies of the same picture. Sizing the box off the
 * layer's own travel made a near copy some 8% bigger than the far one it was cut from — a
 * misregistration an order of magnitude larger than the parallax it existed to show, and in the
 * wrong direction half the time. Same box, same scale, and the only thing separating two layers is
 * the transform below, which is the entire point.
 */
const PARALLAX_BOX = 112;

/**
 * One of a theme's painted planes. Full-bleed unless the layer overrides `inset` itself.
 *
 * `live` is whether the pointer is actually driving anything. When it is not — the animated
 * background switched off, or reduced motion — a parallax layer is rendered as a plain plane with
 * no wrapper at all, rather than a wrapper sitting at zero. The wrapper is not free: `willChange`
 * promotes it, and a promoted full-screen plane is its own compositor buffer for as long as the app
 * is open. Dropping it is the same statement the setting makes everywhere else — off means the
 * cheapest thing the browser can paint, not the same thing holding still.
 */
function Plane({ layer, live = false }: { layer: BackdropLayer; live?: boolean }) {
  const base: CSSProperties = { position: 'absolute', inset: 0, pointerEvents: 'none' };
  const plane = <div aria-hidden className={layer.className} style={{ ...base, ...layer.style }} />;
  if (!layer.parallax || !live) return plane;

  /*
   * A parallaxed layer gets a box of its own to be slid around in, rather than having the slide
   * written into its own transform. Two reasons, and both of them are the same reason: an element
   * has one transform. Half these layers are already spending theirs on a keyframe — a bird
   * crossing, a light pool breathing — and the ones that are not have their position written in
   * `inset`, which a transform does not touch. A wrapper is the only place the pointer can be
   * applied without arguing with either.
   *
   * A third reason arrives once a theme cuts one picture into two planes: the wrapper is also what
   * fixes the scale both of them are drawn at. See `PARALLAX_BOX`.
   */
  const p = layer.parallax;
  return (
    <div
      aria-hidden
      style={{
        position: 'absolute', inset: -PARALLAX_BOX, pointerEvents: 'none', willChange: 'transform',
        // Vertically at three fifths: a window is wider than it is tall and the pointer crosses it
        // faster sideways, so equal travel on both axes reads as a lurch on the short one.
        transform: `translate3d(calc(var(--w-mx,0) * ${-p}px),calc(var(--w-my,0) * ${-p * 0.6}px),0)`,
      }}
    >
      {plane}
    </div>
  );
}

/** How much of the way to the pointer the stack travels each frame. Same easing as the nebula's. */
const EASE = 0.06;
/** Below this the layers are where they were going, and the loop has nothing left to write. */
const SETTLED = 0.0006;

/**
 * Where the pointer is, in client coordinates, and the one listener that knows.
 *
 * Two things in this file want the pointer — the CSS layer driver below, and the sky's shader,
 * which takes it as a uniform. They used to attach a `pointermove` each, and while two passive
 * listeners writing two numbers is not a cost worth a refactor on its own, keeping them apart had
 * a real consequence: the space theme could not have parallax layers *and* a parallaxing sky,
 * because turning the driver on for a nebula meant paying for the pointer twice. One source, and
 * that argument goes away — the planet can move on its own plane while the stars move in the
 * shader behind it.
 *
 * Raw rather than eased, deliberately. The two readers ease at different rates on different
 * clocks: the CSS stack runs at the display's rate and parks the moment it arrives, the sky runs at
 * thirty and never stops. Sharing one eased value would have forced them onto one clock.
 */
const pointer = { x: 0, y: 0 };

/**
 * Called after every move, so a reader that has parked its loop can start it again.
 *
 * This is the half of the arrangement that keeps the driver's idleness intact. It reads the pointer
 * rather than being handed it, but it still has to be *told* when to care, or the only way back
 * from parked would be a frame loop asking whether anything had happened yet — which is the exact
 * cost parking exists to avoid.
 */
const wakers = new Set<() => void>();

/** The one `pointermove` in the backdrop, live for as long as anything under it is reading. */
function usePointer(on: boolean) {
  useEffect(() => {
    if (!on) return;
    const onMove = (e: PointerEvent) => {
      pointer.x = e.clientX;
      pointer.y = e.clientY;
      for (const wake of wakers) wake();
    };
    document.addEventListener('pointermove', onMove, { passive: true });
    return () => document.removeEventListener('pointermove', onMove);
  }, [on]);
}

/**
 * The pointer, eased, as two variables on the backdrop's own box.
 *
 * The whole stack reads `--w-mx` and `--w-my`; nothing else in the app does. One frame loop for
 * however many planes a theme paints, off the one listener above, which is the difference between
 * this and the obvious version where each layer tracks the pointer itself — twelve birds with
 * twelve listeners is twelve times the work to arrive at the same two numbers.
 *
 * The loop is not permanent. It starts when the pointer moves and stops as soon as the easing has
 * caught up, so a window nobody is touching costs nothing at all: no rAF, no style writes, no
 * compositor traffic. That matters more here than it looks — this thing is mounted behind every
 * tab, for as long as the app is open.
 *
 * Off entirely when the animated background is off, and under `prefers-reduced-motion`. Both are
 * the same statement — that the background should hold still — and a background that holds still
 * except when you wave at it is not holding still.
 */
function useParallax(on: boolean) {
  const box = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = box.current;
    if (!on || !el) return;
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return;

    let mx = 0, my = 0, raf = 0;

    const step = () => {
      // −1..1 across the viewport. `innerWidth` rather than a measured rect: this box *is* the
      // viewport, and reading it off the window is the one way to ask that cannot force a layout.
      const tx = (pointer.x / window.innerWidth) * 2 - 1;
      const ty = (pointer.y / window.innerHeight) * 2 - 1;
      mx += (tx - mx) * EASE;
      my += (ty - my) * EASE;
      el.style.setProperty('--w-mx', mx.toFixed(4));
      el.style.setProperty('--w-my', my.toFixed(4));
      // Arrived: park the loop rather than spend a frame a tick confirming nothing has changed.
      if (Math.abs(tx - mx) < SETTLED && Math.abs(ty - my) < SETTLED) { raf = 0; return; }
      raf = requestAnimationFrame(step);
    };

    const wake = () => { if (!raf) raf = requestAnimationFrame(step); };
    wakers.add(wake);

    return () => {
      wakers.delete(wake);
      if (raf) cancelAnimationFrame(raf);
      // Leaving them set would freeze the stack wherever the pointer happened to be when the
      // setting went off, which is a background permanently 12px out of true.
      el.style.removeProperty('--w-mx');
      el.style.removeProperty('--w-my');
    };
  }, [on]);

  return box;
}

const VERT = 'attribute vec2 p;void main(){gl_Position=vec4(p,0.,1.);}';

const FRAG = [
  'precision highp float;',
  'uniform vec2 u_res;uniform float u_time;uniform vec2 u_m;uniform vec3 u_c1,u_c2,u_c3;uniform float u_seed;',
  'float h21(vec2 p){p=fract(p*vec2(123.34,345.45));p+=dot(p,p+34.345);return fract(p.x*p.y);}',
  'float vn(vec2 p){vec2 i=floor(p),f=fract(p);f=f*f*(3.-2.*f);',
  ' float a=h21(i),b=h21(i+vec2(1.,0.)),c=h21(i+vec2(0.,1.)),d=h21(i+vec2(1.,1.));',
  ' return mix(mix(a,b,f.x),mix(c,d,f.x),f.y);}',
  'float fbm(vec2 p){float s=0.,a=.5;for(int i=0;i<5;i++){s+=a*vn(p);p*=2.03;a*=.5;}return s;}',
  'float stars(vec2 uv,float dens,float sz,float t){',
  ' vec2 g=uv*dens;vec2 i=floor(g),f=fract(g);float h=h21(i+u_seed);',
  ' if(h<.87)return 0.;',
  ' vec2 c=vec2(h21(i+1.3),h21(i+2.7));',
  ' float d=length(f-c);',
  ' return smoothstep(sz,0.,d)*(.55+.45*sin(t*1.7+h*44.));}',
  'void main(){',
  ' vec2 uv=(gl_FragCoord.xy-.5*u_res)/u_res.y;',
  ' vec3 col=vec3(0.);',
  ' float n1=fbm((uv*1.1+u_m*.030+vec2(u_time*.010,0.))*1.6+u_seed);',
  ' float n2=fbm((uv*1.6+u_m*.062+vec2(-u_time*.014,u_time*.006))*2.4+u_seed*1.7);',
  ' float n3=fbm((uv*2.4+u_m*.104+vec2(u_time*.020,0.))*3.6+u_seed*2.3);',
  ' col+=u_c1*pow(smoothstep(.34,1.,n1),1.6)*.92;',
  ' col+=u_c2*pow(smoothstep(.42,1.,n2),2.0)*.72;',
  ' col+=u_c3*pow(smoothstep(.50,1.,n3),2.4)*.58;',
  ' col+=vec3(.035,.045,.085)*n2;',
  ' col+=vec3(.72,.80,1.00)*stars(uv+u_m*.012,26.,.055,u_time)*.5;',
  ' col+=vec3(.90,.93,1.00)*stars(uv+u_m*.030,15.,.050,u_time*1.3)*.78;',
  ' col+=vec3(1.00,.96,.88)*stars(uv+u_m*.058,8.,.045,u_time*.8)*1.05;',
  ' float v=1.-smoothstep(.48,1.28,length(uv*vec2(.86,1.)));',
  ' col*=mix(.22,1.,v);',
  ' gl_FragColor=vec4(col,1.);}',
].join('\n');

const RES_SCALE = 0.68; // render below CSS size; the nebula is soft, the cost is not
const FPS = 30;

/**
 * A slow cloud field with three parallax star layers, drawn on the GPU.
 *
 * One canvas and nothing else — no DOM per star, no per-frame layout. The pointer is read from a
 * single passive listener and parallaxed in the shader, so moving the mouse across the app costs a
 * uniform upload rather than a reflow. Everything degrades: no WebGL, a lost context or
 * `prefers-reduced-motion` all fall back to the theme's `under` layers, which for the space theme
 * are the same three glows in the same places.
 *
 * The colours and the seed are uniforms, so a second theme wanting a different sky — green over a
 * swamp, dust over a desert — is three numbers and not a second shader.
 */
function Nebula({ spec, live }: { spec: Extract<Motion, { kind: 'nebula' }>; live: boolean }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const { clouds, seed } = spec;

  useEffect(() => {
    const cv = canvas.current;
    if (!cv) return;

    const gl = cv.getContext('webgl', {
      antialias: false, alpha: false, depth: false, powerPreference: 'low-power',
    });
    if (!gl) { cv.style.display = 'none'; return; }

    const compile = (type: number, src: string) => {
      const s = gl.createShader(type)!;
      gl.shaderSource(s, src);
      gl.compileShader(s);
      return gl.getShaderParameter(s, gl.COMPILE_STATUS) ? s : null;
    };
    const vs = compile(gl.VERTEX_SHADER, VERT);
    const fs = compile(gl.FRAGMENT_SHADER, FRAG);
    if (!vs || !fs) { cv.style.display = 'none'; return; }

    const prog = gl.createProgram()!;
    gl.attachShader(prog, vs);
    gl.attachShader(prog, fs);
    gl.linkProgram(prog);
    if (!gl.getProgramParameter(prog, gl.LINK_STATUS)) { cv.style.display = 'none'; return; }
    gl.useProgram(prog);

    // One oversized triangle covers the viewport with three vertices instead of a quad's six.
    const buf = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buf);
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const loc = gl.getAttribLocation(prog, 'p');
    gl.enableVertexAttribArray(loc);
    gl.vertexAttribPointer(loc, 2, gl.FLOAT, false, 0, 0);

    const u = (n: string) => gl.getUniformLocation(prog, n);
    gl.uniform3fv(u('u_c1'), clouds[0]);
    gl.uniform3fv(u('u_c2'), clouds[1]);
    gl.uniform3fv(u('u_c3'), clouds[2]);
    gl.uniform1f(u('u_seed'), seed);
    const uRes = u('u_res'), uTime = u('u_time'), uM = u('u_m');

    let w = 0, h = 0;
    let rect = cv.getBoundingClientRect();
    let mx = 0, my = 0, tx = 0, ty = 0;
    let visible = true, hidden = document.hidden;

    // Rects are cached and refreshed on a slow clock; no pointer move ever forces a layout.
    const measure = () => {
      rect = cv.getBoundingClientRect();
      const dpr = Math.min(window.devicePixelRatio || 1, 2);
      const nw = Math.max(1, Math.round(rect.width * dpr * RES_SCALE));
      const nh = Math.max(1, Math.round(rect.height * dpr * RES_SCALE));
      if (nw !== w || nh !== h) {
        w = nw; h = nh;
        cv.width = w; cv.height = h;
        gl.viewport(0, 0, w, h);
        gl.uniform2f(uRes, w, h);
      }
    };


    let io: IntersectionObserver | undefined;
    if ('IntersectionObserver' in window) {
      io = new IntersectionObserver(
        es => { for (const e of es) visible = e.isIntersecting; },
        { rootMargin: '120px' },
      );
      io.observe(cv);
    }
    const onVis = () => { hidden = document.hidden; };
    document.addEventListener('visibilitychange', onVis);

    const frame = (t: number) => {
      if (!rect.width || !rect.height) return;
      // Pointer in −1..1 across the canvas, eased so the parallax lags the cursor slightly.
      tx = ((pointer.x - rect.left) / rect.width) * 2 - 1;
      ty = 1 - ((pointer.y - rect.top) / rect.height) * 2;
      mx += (tx - mx) * 0.06;
      my += (ty - my) * 0.06;
      gl.uniform2f(uM, mx, my);
      gl.uniform1f(uTime, t);
      gl.drawArrays(gl.TRIANGLES, 0, 3);
    };

    measure();

    // A reduced-motion sky is still a sky — it just never gets a second frame.
    if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
      mx = 0; my = 0;
      frame(0);
      return () => {
        document.removeEventListener('visibilitychange', onVis);
        io?.disconnect();
      };
    }

    const step = 1000 / FPS;
    let raf = 0, last = 0, tick = 0;
    const loop = (now: number) => {
      raf = requestAnimationFrame(loop);
      if (hidden || !visible || now - last < step) return;
      last = now;
      if (++tick % 30 === 0) measure(); // ~1/s: survives resize and tab switches without thrash
      frame(now / 1000);
    };
    raf = requestAnimationFrame(loop);

    const onResize = () => measure();
    window.addEventListener('resize', onResize);

    return () => {
      cancelAnimationFrame(raf);
      window.removeEventListener('resize', onResize);
      document.removeEventListener('visibilitychange', onVis);
      io?.disconnect();
    };
    // The uniforms are uploaded once at setup, so a theme with a different sky has to rebuild the
    // context rather than mutate it. Cheap: it happens on a theme change, not on a render.
  }, [clouds, seed]);

  return (
    <>
      {spec.under?.map((l, i) => <Plane key={`u${i}`} layer={l} live={live} />)}
      <canvas
        ref={canvas}
        aria-hidden
        style={{ position: 'absolute', inset: 0, width: '100%', height: '100%', display: 'block' }}
      />
      {spec.over?.map((l, i) => <Plane key={`o${i}`} layer={l} live={live} />)}
    </>
  );
}

export function Backdrop() {
  const theme = useTheme();
  const animated = useSkyEnabled();
  const { base, still, motion } = theme.backdrop;
  /*
   * Whether the pointer is driving anything at all this render.
   *
   * Every motion kind, not just the CSS-plane ones. A nebula theme used to be excluded on the
   * grounds that its sky already read the pointer and the driver would be a second listener for the
   * same two numbers; now there is only ever one listener, so a nebula can have parallax *planes*
   * over a parallaxing sky — which is how the planet gets to sit in front of the stars instead of
   * on them.
   *
   * Reduced motion is asked here rather than left to `useParallax` alone, because the hook can
   * decline to run a loop but only this can decline to *build* the wrappers — and an unmoving
   * wrapper is still a promoted compositor buffer per plane, for as long as the app is open. It is
   * also exactly the condition the sky's own loop runs under, so one flag arms both. Read once, the
   * way the nebula reads it: a preference changed mid-session lands on the next swap.
   */
  const live =
    animated
    && !!motion
    && !window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  usePointer(live);
  const box = useParallax(live);

  return (
    <div ref={box} aria-hidden style={{ position: 'fixed', inset: 0, zIndex: 0, pointerEvents: 'none', background: base }}>
      {still?.map((l, i) => <Plane key={`s${i}`} layer={l} live={live} />)}
      {/* Switched off, or a theme with nothing animated to draw: the base colour and whatever is
          static, and nothing else. No canvas, no blend layers — for the space theme that is one
          opaque fill, the cheapest thing a browser can paint, which is the whole point of the
          setting. Keyed on the theme so a swap tears the old sky down rather than reconfiguring it. */}
      {animated && motion && (
        motion.kind === 'nebula'
          ? <Nebula key={theme.key} spec={motion} live={live} />
          : motion.layers.map((l, i) => <Plane key={`m${i}`} layer={l} live={live} />)
      )}
    </div>
  );
}
