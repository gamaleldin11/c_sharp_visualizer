import { create } from 'zustand';
import type { Trace } from '../trace/types';
import { TracePlayer } from '../trace/player';

export type Tab = 'memory' | 'flowchart' | 'dataflow' | 'calls' | 'explain' | 'output' | 'diagnostics';

interface AppState {
  source: string;
  stdin: string;
  trace: Trace | null;
  player: TracePlayer | null;
  stepIndex: number;
  running: boolean;
  error: string | null;
  tab: Tab;
  playing: boolean;
  aiAvailable: boolean;
  aiReason: string | null;

  setSource: (s: string) => void;
  setStdin: (s: string) => void;
  setTab: (t: Tab) => void;
  setStep: (i: number) => void;
  stepBy: (d: number) => void;
  setPlaying: (p: boolean) => void;
  beginRun: () => void;
  finishRun: (t: Trace) => void;
  failRun: (message: string) => void;
  setAi: (available: boolean, reason: string | null) => void;
}

// The Trace object is held by reference and never spread or cloned - it can be tens of
// megabytes. Components select the slices they need; reconstruction happens in TracePlayer.
export const useApp = create<AppState>((set, get) => ({
  source: '',
  stdin: '',
  trace: null,
  player: null,
  stepIndex: 0,
  running: false,
  error: null,
  tab: 'memory',
  playing: false,
  // Assumed off until the server says otherwise, so the panel never flashes into view and
  // then disappears on a server that has no key.
  aiAvailable: false,
  aiReason: null,

  setSource: (source) => set({ source }),
  setStdin: (stdin) => set({ stdin }),
  setTab: (tab) => set({ tab }),

  setStep: (i) => {
    const { player } = get();
    if (!player) return;
    const max = Math.max(0, player.stepCount - 1);
    set({ stepIndex: Math.max(0, Math.min(i, max)) });
  },

  stepBy: (d) => get().setStep(get().stepIndex + d),
  setPlaying: (playing) => set({ playing }),

  beginRun: () => set({ running: true, error: null, playing: false }),

  finishRun: (trace) =>
    set({
      trace,
      player: new TracePlayer(trace),
      stepIndex: 0,
      running: false,
      // Send the user straight to whichever pane explains the result.
      tab: trace.status === 'compile_error' ? 'diagnostics' : 'memory',
    }),

  failRun: (error) => set({ running: false, error, trace: null, player: null }),

  setAi: (aiAvailable, aiReason) => set({ aiAvailable, aiReason }),
}));
