import { useEffect, useRef } from 'react';
import { useApp } from '../state/store';
import type { Trace } from '../trace/types';

/** Next step at the same call depth or shallower - i.e. skip over a call's body. */
function stepOverTarget(trace: Trace, from: number, dir: 1 | -1): number {
  const base = trace.steps[from]?.frameDepth ?? 0;
  for (let i = from + dir; i >= 0 && i < trace.steps.length; i += dir) {
    if (trace.steps[i].frameDepth <= base) return i;
  }
  return dir === 1 ? trace.steps.length - 1 : 0;
}

export function Transport() {
  const { trace, stepIndex, setStep, stepBy, playing, setPlaying } = useApp();
  const timer = useRef<number | null>(null);

  const total = trace?.steps.length ?? 0;
  const last = Math.max(0, total - 1);

  useEffect(() => {
    if (!playing || !trace) return;
    timer.current = window.setInterval(() => {
      const s = useApp.getState();
      if (s.stepIndex >= (s.trace?.steps.length ?? 1) - 1) s.setPlaying(false);
      else s.stepBy(1);
    }, 220);
    return () => {
      if (timer.current !== null) window.clearInterval(timer.current);
    };
  }, [playing, trace]);

  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      // Guarded rather than cast: an event whose target is not an Element (the window, the
      // document) would throw on .closest and take every keyboard shortcut down with it.
      const el = e.target instanceof Element ? e.target : null;
      // Never steal keys from the editor or a text field.
      if (el && (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT' || (el as HTMLElement).isContentEditable)) return;

      // Nor from the tab strip. ARIA says arrow keys move between tabs when one has focus, and
      // a keyboard user who has tabbed to the view switcher expects exactly that - stepping the
      // trace out from under them instead would make the tabs unreachable by keyboard.
      if (el?.closest('[role="tablist"]')) return;
      const t = useApp.getState().trace;
      if (!t) return;

      switch (e.key) {
        case 'ArrowRight':
          e.preventDefault();
          if (e.shiftKey) setStep(stepOverTarget(t, useApp.getState().stepIndex, 1));
          else stepBy(1);
          break;
        case 'ArrowLeft':
          e.preventDefault();
          if (e.shiftKey) setStep(stepOverTarget(t, useApp.getState().stepIndex, -1));
          else stepBy(-1);
          break;
        case 'Home': e.preventDefault(); setStep(0); break;
        case 'End': e.preventDefault(); setStep(t.steps.length - 1); break;
        case ' ': e.preventDefault(); setPlaying(!useApp.getState().playing); break;
        default: break;
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [setStep, stepBy, setPlaying]);

  const disabled = !trace || total === 0;

  return (
    <div className="transport">
      <div className="transport-buttons">
        <button onClick={() => setStep(0)} disabled={disabled} title="First step (Home)" aria-label="First step">&#124;&#9664;</button>
        <button onClick={() => stepBy(-1)} disabled={disabled || stepIndex === 0} title="Previous step (Left arrow)" aria-label="Previous step">&#9664;</button>
        <button
          className="play"
          onClick={() => setPlaying(!playing)}
          disabled={disabled || stepIndex >= last}
          title="Play / pause (Space)"
          aria-label={playing ? 'Pause' : 'Play'}
        >
          {playing ? '❙❙' : '▶'}
        </button>
        <button onClick={() => stepBy(1)} disabled={disabled || stepIndex >= last} title="Next step (Right arrow)" aria-label="Next step">&#9654;</button>
        <button onClick={() => setStep(last)} disabled={disabled} title="Last step (End)" aria-label="Last step">&#9654;&#124;</button>
      </div>

      <input
        className="scrubber"
        type="range"
        min={0}
        max={last}
        value={stepIndex}
        disabled={disabled}
        onChange={(e) => setStep(Number(e.target.value))}
        aria-label="Step position"
      />

      <div className="step-count" aria-live="polite">
        {disabled ? '—' : `step ${stepIndex + 1} / ${total}`}
      </div>
    </div>
  );
}
