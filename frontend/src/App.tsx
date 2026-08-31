import { useEffect, useMemo } from 'react';
import { useApp } from './state/store';
import { aiStatus, runTrace } from './trace/api';
import { CodeEditor } from './editor/CodeEditor';
import { Transport } from './player/Transport';
import { MemoryView } from './views/memory/MemoryView';
import { FlowchartView } from './views/analysis/FlowchartView';
import { DataflowView } from './views/analysis/DataflowView';
import { CallTreeView } from './views/analysis/CallTreeView';
import { AiPanel } from './views/ai/AiPanel';
import { OutputView, DiagnosticsView } from './views/output/OutputView';
import { SAMPLES } from './samples';
import type { VmState } from './trace/types';

const EMPTY: VmState = { frames: [], heap: new Map(), stdout: '' };

const TABS = ['memory', 'flowchart', 'dataflow', 'calls', 'explain', 'output', 'diagnostics'] as const;

const TAB_LABELS: Record<string, string> = {
  memory: 'Memory',
  flowchart: 'Flowchart',
  dataflow: 'Dataflow',
  calls: 'Call tree',
  explain: 'Explain',
  output: 'Output',
  diagnostics: 'Diagnostics',
};

export default function App() {
  const source = useApp((s) => s.source);
  const setSource = useApp((s) => s.setSource);
  const trace = useApp((s) => s.trace);
  const player = useApp((s) => s.player);
  const stepIndex = useApp((s) => s.stepIndex);
  const running = useApp((s) => s.running);
  const error = useApp((s) => s.error);
  const tab = useApp((s) => s.tab);
  const setTab = useApp((s) => s.setTab);

  useEffect(() => {
    if (!source) setSource(SAMPLES[0].source);
  }, [source, setSource]);

  // Asked once at startup so the Explain tab can say why it is empty rather than failing on
  // first use. aiStatus never throws.
  useEffect(() => {
    aiStatus().then((s) => useApp.getState().setAi(s.available, s.reason));
  }, []);

  async function run() {
    const st = useApp.getState();
    st.beginRun();
    try {
      const t = await runTrace({ source: st.source, stdin: st.stdin });
      useApp.getState().finishRun(t);
    } catch (e) {
      useApp.getState().failRun(e instanceof Error ? e.message : String(e));
    }
  }

  const state = useMemo(() => (player ? player.stateAt(stepIndex) : EMPTY), [player, stepIndex]);
  const step = trace?.steps[stepIndex] ?? null;
  const activeLine = step?.line ?? null;

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          C# Visualizer
          <span className="brand-sub">trace &amp; replay</span>
        </div>

        <select
          className="sample-select"
          onChange={(e) => {
            const s = SAMPLES.find((x) => x.name === e.target.value);
            if (s) setSource(s.source);
          }}
          defaultValue={SAMPLES[0].name}
          aria-label="Load a sample program"
        >
          {SAMPLES.map((s) => (
            <option key={s.name} value={s.name}>{s.name}</option>
          ))}
        </select>

        <button className="run" onClick={run} disabled={running}>
          {running ? 'Tracing…' : 'Run ▸'}
        </button>

        {trace && <StatusPill />}
      </header>

      <main className="split">
        <section className="pane pane-editor">
          <CodeEditor activeLine={activeLine} />
        </section>

        <section className="pane pane-views">
          <nav
            className="tabs"
            role="tablist"
            aria-label="Visualization views"
            onKeyDown={(e) => {
              // Roving arrow-key navigation, as ARIA specifies for a tab strip.
              if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
              e.preventDefault();
              const at = TABS.indexOf(tab);
              const next = e.key === 'ArrowRight'
                ? TABS[(at + 1) % TABS.length]
                : TABS[(at - 1 + TABS.length) % TABS.length];
              setTab(next);
              document.getElementById(`tab-${next}`)?.focus();
            }}
          >
            {TABS.map((t) => (
              <button
                key={t}
                id={`tab-${t}`}
                role="tab"
                aria-selected={tab === t}
                aria-controls="view-panel"
                // Only the selected tab is in the tab order; arrows move within the strip.
                tabIndex={tab === t ? 0 : -1}
                className={tab === t ? 'tab tab-active' : 'tab'}
                onClick={() => setTab(t)}
              >
                {TAB_LABELS[t]}
                {t === 'diagnostics' && trace && trace.diagnostics.length > 0 && (
                  <span className="tab-count">{trace.diagnostics.length}</span>
                )}
              </button>
            ))}
          </nav>

          <div className="view" id="view-panel" role="tabpanel" aria-labelledby={`tab-${tab}`} tabIndex={-1}>
            {error && <div className="banner banner-error">{error}</div>}
            {!trace && !error && <div className="empty-pane">Press <b>Run</b> to trace this program.</div>}
            {trace && tab === 'memory' && <MemoryView state={state} trace={trace} step={step} />}
            {trace && tab === 'flowchart' && <FlowchartView trace={trace} step={step} />}
            {trace && tab === 'dataflow' && <DataflowView trace={trace} stepIndex={stepIndex} />}
            {trace && tab === 'calls' && <CallTreeView trace={trace} stepIndex={stepIndex} />}
            {trace && tab === 'explain' && <AiPanel trace={trace} stepIndex={stepIndex} />}
            {trace && tab === 'output' && <OutputView stdout={state.stdout} />}
            {trace && tab === 'diagnostics' && <DiagnosticsView trace={trace} />}
          </div>
        </section>
      </main>

      <footer className="bottombar">
        <Transport />
      </footer>
    </div>
  );
}

function StatusPill() {
  const trace = useApp((s) => s.trace)!;
  const label: Record<string, string> = {
    ok: 'completed',
    compile_error: 'compile error',
    runtime_error: 'runtime error',
    limit_exceeded: `stopped: ${trace.limitHit ?? 'limit'} limit`,
  };
  return <span className={`pill pill-${trace.status}`}>{label[trace.status] ?? trace.status}</span>;
}
