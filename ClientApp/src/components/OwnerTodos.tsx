import { useCallback, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { Chip, GhostButton, PageHead, Panel, PrimaryButton, SectionRule } from './ui';
import { BODY, MONO, TIER, chamfer } from './orbit';
import { Markdown } from './Markdown';
import { Quiet, ago, label, remember, remembered } from './stewardParts';
import { useNarrow } from '../hooks/useNarrow';
import { useProjectList } from './ProjectPicker';
import {
  KINDS, addOwnerTodo, doneOwnerTodo, dropOwnerTodo, editOwnerTodo, listOwnerTodos, noteOwnerTodo,
  ownerTodosChanged, reopenOwnerTodo, waitOwnerTodo,
} from '../api/ownerTodos';
import type { OwnerTodo, OwnerTodoKind } from '../api/ownerTodos';

/**
 * The owner's to-do list (docs/owner-todo-plan.md): one list, across every project, of what only the
 * owner can do. Agents fill it; everything that empties it is here, or a Claude Code session calling
 * the same routes. Read in the order it is worth doing: what holds a release, then what a ticket is
 * waiting on, then the rest, then what is waiting on somebody else.
 */

type Show = 'open' | 'done' | 'dropped' | 'all';

const KIND_TIER: Record<OwnerTodoKind, keyof typeof TIER> = {
  secret: 'blocked', config: 'gate', infra: 'gate', account: 'queue', deploy: 'active', action: 'backlog',
};

const field = 'w-full min-w-0 rounded-lg border border-hair bg-panel px-2.5 py-1.5 text-[12.5px] text-gray-200 placeholder-gray-600 focus:border-gray-500 focus:outline-none';

export function OwnerTodos({ focus, onFocused }: {
  /** An item to open on arrival, by its ref, from a link elsewhere in the app. */
  focus?: string | null; onFocused?: () => void;
}) {
  const narrow = useNarrow();
  const [projects] = useProjectList();
  const [show, setShow] = useState<Show>(() => (remembered('todo.show') as Show | null) ?? 'open');
  const [project, setProject] = useState<string>(() => remembered('todo.project') ?? '');
  const [items, setItems] = useState<OwnerTodo[] | null>(null);
  const [failed, setFailed] = useState<string | null>(null);
  const [open, setOpen] = useState<number | null>(null);
  const [adding, setAdding] = useState(false);

  const load = useCallback(async () => {
    try {
      const r = await listOwnerTodos(show);
      setItems(r.items);
      setFailed(null);
    } catch (e) { setFailed(e instanceof Error ? e.message : String(e)); }
  }, [show]);

  useEffect(() => { load(); }, [load]);
  // Agents add while the page is up; a quiet re-read keeps it honest without a socket of its own.
  useEffect(() => {
    const t = setInterval(load, 30000);
    const again = () => load();
    window.addEventListener('focus', again);
    return () => { clearInterval(t); window.removeEventListener('focus', again); };
  }, [load]);

  useEffect(() => {
    if (!focus || !items) return;
    const hit = items.find(i => i.ref.toLowerCase() === focus.toLowerCase());
    if (hit) { setOpen(hit.id); onFocused?.(); }
    else if (show !== 'all') setShow('all');
    else onFocused?.();
  }, [focus, items, show, onFocused]);

  const changed = async () => { await load(); ownerTodosChanged(); };
  const pickShow = (s: Show) => { setShow(s); remember('todo.show', s); };
  const pickProject = (p: string) => { setProject(p); remember('todo.project', p); };

  const name = (key?: string | null) => key ? projects.find(p => p.key === key)?.displayName ?? key : 'Your own';
  const keys = useMemo(() => [...new Set((items ?? []).map(i => i.project ?? ''))].sort(), [items]);
  const shown = (items ?? []).filter(i => !project || (project === 'none' ? !i.project : i.project === project));

  const openCount = (items ?? []).filter(i => i.status === 'open').length;
  const waiting = (items ?? []).filter(i => i.status === 'waiting').length;

  const groups: { title: string; colour: string; rows: OwnerTodo[]; meta?: string }[] = show === 'open' ? [
    { title: 'Holds a release', colour: TIER.blocked.c, rows: shown.filter(i => i.status === 'open' && i.holdsRelease), meta: 'a slice cannot go out until these are done' },
    { title: 'A ticket is waiting on it', colour: TIER.gate.c, rows: shown.filter(i => i.status === 'open' && !i.holdsRelease && i.ticketId != null) },
    { title: 'The rest', colour: TIER.active.c, rows: shown.filter(i => i.status === 'open' && !i.holdsRelease && i.ticketId == null) },
    { title: 'Waiting on someone', colour: TIER.queue.c, rows: shown.filter(i => i.status === 'waiting') },
  ] : [{ title: show === 'done' ? 'Done' : show === 'dropped' ? 'Dropped' : 'Everything', colour: TIER.done.c, rows: shown }];

  return (
    <div className={`max-w-5xl mx-auto py-4 ${narrow ? 'px-3' : 'px-6'}`}>
      <PageHead
        eyebrow="Whetstone // owner"
        title="To-Do"
        sub={items ? `${openCount} open${waiting > 0 ? `, ${waiting} waiting on someone` : ''} - in Claude Code, /todo OT-n walks you through one` : 'Loading'}
        right={<PrimaryButton onClick={() => setAdding(a => !a)}>{adding ? 'Close' : 'Add one'}</PrimaryButton>}
      />

      {adding && (
        <div key="add" className="orbit-swap" style={{ marginBottom: 16 }}>
          <AddForm projects={projects} defaultProject={project && project !== 'none' ? project : ''}
            onAdded={async () => { setAdding(false); await changed(); }} />
        </div>
      )}

      <div className="flex flex-wrap items-center gap-2" style={{ marginBottom: 8 }}>
        <span style={label()}>Show</span>
        {(['open', 'done', 'dropped', 'all'] as Show[]).map(s => (
          <Chip key={s} on={show === s} onClick={() => pickShow(s)}>{s === 'open' ? 'To do' : s}</Chip>
        ))}
      </div>
      {keys.length > 1 && (
        <div className="flex flex-wrap items-center gap-2" style={{ marginBottom: 14 }}>
          <span style={label()}>For</span>
          <Chip on={!project} onClick={() => pickProject('')}>Everything</Chip>
          {keys.map(k => (
            <Chip key={k || 'none'} on={project === (k || 'none')} onClick={() => pickProject(k || 'none')}>{name(k || null)}</Chip>
          ))}
        </div>
      )}

      {failed && <Panel tone={TIER.blocked.c} style={{ marginBottom: 12 }}><span style={{ font: `400 12.5px ${BODY}` }}>{failed}</span></Panel>}

      {items && shown.length === 0 && <Quiet>{show === 'open' ? 'Nothing is yours to do right now.' : 'Nothing here.'}</Quiet>}

      {groups.filter(g => g.rows.length > 0).map(g => (
        <div key={g.title} style={{ marginBottom: 18 }}>
          <SectionRule title={g.title} colour={g.colour} count={g.rows.length} meta={g.meta} />
          <div className="flex flex-col gap-1.5">
            {g.rows.map(t => (
              <Row key={t.id} t={t} project={name(t.project)} open={open === t.id}
                onToggle={() => setOpen(o => (o === t.id ? null : t.id))} onChanged={changed} />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

function KindTag({ kind }: { kind: OwnerTodoKind }) {
  const c = TIER[KIND_TIER[kind]].c;
  return (
    <span style={{ ...label(c), padding: '1px 6px', border: `1px solid ${c}`, clipPath: chamfer(4), flex: 'none' }}>
      {KINDS.find(k => k.id === kind)?.word ?? kind}
    </span>
  );
}

function Row({ t, project, open, onToggle, onChanged }: {
  t: OwnerTodo; project: string; open: boolean; onToggle: () => void; onChanged: () => Promise<void>;
}) {
  const closed = t.status === 'done' || t.status === 'dropped';
  return (
    <div>
      <button
        onClick={onToggle}
        aria-current={open ? 'true' : undefined}
        aria-expanded={open}
        className="orbit-pick w-full text-left"
        style={{
          clipPath: chamfer(8), padding: '9px 12px',
          background: open ? 'rgb(var(--w-accent-rgb) / .10)' : 'rgb(var(--w-line-rgb) / .04)',
          border: `1px solid ${open ? 'rgb(var(--w-accent-rgb) / .45)' : 'rgb(var(--w-line-rgb) / .13)'}`,
          opacity: closed ? 0.7 : 1,
        }}
      >
        <div className="flex items-baseline gap-2.5 min-w-0">
          <span style={{ font: `600 11px ${MONO}`, color: 'var(--w-accent-text)', flex: 'none' }}>{t.ref}</span>
          <KindTag kind={t.kind} />
          <span className="min-w-0 flex-1" style={{ font: `600 13px/1.4 ${BODY}`, color: 'var(--w-t100)', textDecoration: t.status === 'dropped' ? 'line-through' : undefined }}>
            {t.title}
          </span>
        </div>
        <div className="flex flex-wrap gap-x-3 gap-y-0.5" style={{ font: `400 11px ${BODY}`, color: 'var(--w-t500)', marginTop: 3, paddingLeft: 2 }}>
          <span>{project}</span>
          {t.ticketId != null && <span>ticket #{t.ticketId}{t.askers.length > 1 ? ` +${t.askers.length - 1}` : ''}</span>}
          {t.holdsRelease && <span style={{ color: TIER.blocked.c }}>holds {t.unit != null ? `slice ${t.unit}` : 'its slice'}</span>}
          {t.status === 'waiting' && <span style={{ color: TIER.queue.c }}>waiting on {t.waitingOn}{t.waitingUntil ? `, look again ${t.waitingUntil.slice(0, 10)}` : ''}</span>}
          {t.suggestion && !closed && <span style={{ color: TIER.done.c }}>an agent says it looks {t.suggestion}</span>}
          <span>{t.status === 'done' ? `done ${ago(t.doneAt)}` : t.status === 'dropped' ? `dropped ${ago(t.droppedAt)}` : `added ${ago(t.createdAt)} by ${t.createdBy}`}</span>
        </div>
      </button>
      {open && (
        <div key="open" className="orbit-swap" style={{ padding: '10px 4px 6px 12px' }}>
          <Detail t={t} onChanged={onChanged} />
        </div>
      )}
    </div>
  );
}

type Act = 'done' | 'wait' | 'drop' | 'edit' | 'note' | null;

function Detail({ t, onChanged }: { t: OwnerTodo; onChanged: () => Promise<void> }) {
  const [act, setAct] = useState<Act>(null);
  const [text, setText] = useState('');
  const [until, setUntil] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [edit, setEdit] = useState({ title: t.title, body: t.body, kind: t.kind });
  const closed = t.status === 'done' || t.status === 'dropped';

  const run = async (fn: () => Promise<unknown>) => {
    setBusy(true); setFailed(null);
    try { await fn(); setAct(null); setText(''); setUntil(''); await onChanged(); }
    catch (e) { setFailed(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };
  const pick = (a: Act) => { setAct(x => (x === a ? null : a)); setText(''); setFailed(null); };
  const copy = async () => {
    try { await navigator.clipboard.writeText(`/todo ${t.ref}`); setCopied(true); setTimeout(() => setCopied(false), 1600); }
    catch { /* a clipboard refused is not worth a message */ }
  };

  const say: Record<Exclude<Act, null | 'edit'>, { hint: string; go: string; fn: () => Promise<unknown> }> = {
    done: { hint: 'What you did, and where the result lives - the file and the key name. Never the secret itself.', go: 'Mark it done', fn: () => doneOwnerTodo(t.id, text) },
    wait: { hint: 'Who or what it is waiting on.', go: 'Waiting', fn: () => waitOwnerTodo(t.id, text, until) },
    drop: { hint: 'Why it is no longer needed.', go: 'Drop it', fn: () => dropOwnerTodo(t.id, text) },
    note: { hint: 'A note on it: a link, what you found, a correction.', go: 'Add the note', fn: () => noteOwnerTodo(t.id, text) },
  };

  return (
    <div className="flex flex-col gap-3">
      {t.suggestion && !closed && (
        <Panel tone={TIER.done.c}>
          <div style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>
            An agent says this looks <b>{t.suggestion}</b>: {t.suggestionBecause}
          </div>
        </Panel>
      )}
      {t.doneNote && <Line k="Done">{t.doneNote}</Line>}
      {t.droppedBecause && <Line k="Dropped">{t.droppedBecause}</Line>}

      {act === 'edit' ? (
        <div className="flex flex-col gap-2">
          <input className={field} value={edit.title} onChange={e => setEdit({ ...edit, title: e.target.value })} aria-label="Title" />
          <KindPicker kind={edit.kind} onKind={k => setEdit({ ...edit, kind: k })} />
          <textarea className={field} rows={10} value={edit.body} onChange={e => setEdit({ ...edit, body: e.target.value })} aria-label="Detail" />
          <div className="flex gap-2 flex-wrap">
            <GhostButton tone="go" disabled={busy} onClick={() => run(() => editOwnerTodo(t.id, edit))}>{busy ? 'Saving' : 'Save'}</GhostButton>
            <GhostButton disabled={busy} onClick={() => setAct(null)}>Cancel</GhostButton>
          </div>
        </div>
      ) : (
        <div style={{ font: `400 13px/1.55 ${BODY}` }}>
          {t.body ? <Markdown text={t.body} /> : <span style={{ color: 'var(--w-t600)' }}>No detail written.</span>}
        </div>
      )}

      {t.key && <Line k="Key"><span style={{ font: `400 11.5px ${MONO}` }}>{t.key}</span></Line>}

      {t.notes.length > 1 && (
        <div>
          <div style={{ ...label(), marginBottom: 4 }}>Log</div>
          <div className="flex flex-col gap-1">
            {t.notes.map(n => (
              <div key={n.id} style={{ font: `400 12px/1.45 ${BODY}`, color: 'var(--w-t400)' }}>
                <span style={{ font: `400 11px ${MONO}`, color: 'var(--w-t600)' }}>{n.at.slice(0, 10)}</span>{' '}
                <span style={{ color: 'var(--w-t500)' }}>{n.by}{n.ticketId != null ? ` (#${n.ticketId})` : ''}:</span>{' '}
                <span style={{ whiteSpace: 'pre-wrap' }}>{n.text}</span>
              </div>
            ))}
          </div>
        </div>
      )}

      {act && act !== 'edit' && (
        <div key={act} className="orbit-swap flex flex-col gap-2">
          <textarea className={field} rows={act === 'done' ? 3 : 2} value={text} autoFocus
            placeholder={say[act].hint} onChange={e => setText(e.target.value)} aria-label={say[act].hint} />
          {act === 'wait' && (
            <label className="flex items-center gap-2" style={{ font: `400 12px ${BODY}`, color: 'var(--w-t400)' }}>
              Look again on
              <input type="date" className={field} style={{ maxWidth: 170 }} value={until} onChange={e => setUntil(e.target.value)} />
            </label>
          )}
          <div className="flex gap-2 flex-wrap">
            <GhostButton tone={act === 'drop' ? 'danger' : 'go'} disabled={busy || !text.trim()} onClick={() => run(say[act].fn)}>
              {busy ? 'Saving' : say[act].go}
            </GhostButton>
            <GhostButton disabled={busy} onClick={() => setAct(null)}>Cancel</GhostButton>
          </div>
        </div>
      )}

      {failed && <div style={{ font: `400 12px ${BODY}`, color: TIER.blocked.c }}>{failed}</div>}

      {!act && (
        <div className="flex gap-2 flex-wrap">
          {closed ? (
            <GhostButton disabled={busy} onClick={() => run(() => reopenOwnerTodo(t.id))}>Reopen</GhostButton>
          ) : (
            <>
              <GhostButton tone="go" onClick={() => pick('done')}>Done</GhostButton>
              <GhostButton onClick={() => pick('wait')}>{t.status === 'waiting' ? 'Still waiting' : 'Waiting on someone'}</GhostButton>
              <GhostButton tone="danger" onClick={() => pick('drop')}>No longer needed</GhostButton>
            </>
          )}
          <GhostButton onClick={() => pick('note')}>Add a note</GhostButton>
          <GhostButton onClick={() => { setEdit({ title: t.title, body: t.body, kind: t.kind }); setAct('edit'); }}>Edit</GhostButton>
          <GhostButton onClick={copy} title={`Copies "/todo ${t.ref}" to paste into Claude Code`}>{copied ? 'Copied' : 'Copy for Claude'}</GhostButton>
        </div>
      )}
    </div>
  );
}

function Line({ k, children }: { k: string; children: ReactNode }) {
  return (
    <div className="flex gap-2 items-baseline" style={{ font: `400 12.5px/1.5 ${BODY}`, color: 'var(--w-t200)' }}>
      <span style={{ ...label(), flex: 'none', minWidth: 58 }}>{k}</span>
      <span className="min-w-0">{children}</span>
    </div>
  );
}

function KindPicker({ kind, onKind }: { kind: OwnerTodoKind; onKind: (k: OwnerTodoKind) => void }) {
  return (
    <div className="flex flex-wrap gap-1.5">
      {KINDS.map(k => <Chip key={k.id} on={kind === k.id} onClick={() => onKind(k.id)}>{k.word}</Chip>)}
    </div>
  );
}

function AddForm({ projects, defaultProject, onAdded }: {
  projects: { key: string; displayName: string }[]; defaultProject: string; onAdded: () => Promise<void>;
}) {
  const [project, setProject] = useState(defaultProject);
  const [kind, setKind] = useState<OwnerTodoKind>('action');
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState<string | null>(null);

  const add = async () => {
    setBusy(true); setFailed(null);
    try { await addOwnerTodo({ project: project || null, title, body, kind }); await onAdded(); }
    catch (e) { setFailed(e instanceof Error ? e.message : String(e)); }
    finally { setBusy(false); }
  };

  return (
    <Panel>
      <div className="flex flex-col gap-2.5">
        <input className={field} value={title} onChange={e => setTitle(e.target.value)} placeholder="What to do, in one line" aria-label="Title" autoFocus />
        <div className="flex flex-wrap items-center gap-2">
          <span style={label()}>For</span>
          <select className={field} style={{ maxWidth: 220 }} value={project} onChange={e => setProject(e.target.value)} aria-label="Project">
            <option value="">Your own (no project)</option>
            {projects.map(p => <option key={p.key} value={p.key}>{p.displayName}</option>)}
          </select>
        </div>
        <KindPicker kind={kind} onKind={setKind} />
        <textarea className={field} rows={5} value={body} onChange={e => setBody(e.target.value)}
          placeholder={'Why, where, the steps, how you will know it is done, where the result goes'} aria-label="Detail" />
        {failed && <div style={{ font: `400 12px ${BODY}`, color: TIER.blocked.c }}>{failed}</div>}
        <div><GhostButton tone="go" disabled={busy || !title.trim()} onClick={add}>{busy ? 'Adding' : 'Add it'}</GhostButton></div>
      </div>
    </Panel>
  );
}
