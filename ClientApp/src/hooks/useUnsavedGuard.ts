import { useEffect } from 'react';

/**
 * Unsaved edits a page is holding, and the question to ask before they are thrown away.
 *
 * Switching tabs unmounts the page, and with it every edit not yet saved, without a word. A page
 * holding edits registers here while it does; the tab switch asks {@link confirmLeave} first, and
 * closing or reloading the window gets the browser's own warning.
 */
// One object per registration rather than the message itself, so two holders asking the same
// question do not share an entry, and one of them finishing does not silence the other.
const guards = new Set<{ message: string }>();

/** While `message` is non-null, leaving this page asks it first. */
export function useUnsavedGuard(message: string | null) {
  useEffect(() => {
    if (!message) return;
    const entry = { message };
    guards.add(entry);
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      // Still required by some browsers to show the prompt; its text is never shown.
      e.returnValue = '';
    };
    window.addEventListener('beforeunload', onBeforeUnload);
    return () => {
      guards.delete(entry);
      window.removeEventListener('beforeunload', onBeforeUnload);
    };
  }, [message]);
}

/** True when it is fine to leave: nothing is held, or the person said to drop it. */
export function confirmLeave(): boolean {
  for (const { message } of guards) {
    if (!window.confirm(message)) return false;
  }
  return true;
}
