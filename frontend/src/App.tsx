import { useEffect, useMemo, useState } from 'react';
import { useApp } from './state/store';
import { aiStatus, runTrace } from './trace/api';
import { CodeEditor } from './editor/CodeEditor';
import { Transport } from './player/Transport';
import { MemoryView } from './views/memory/MemoryView';
import { FlowchartView } from './views/analysis/FlowchartView';
import { DataflowView } from './views/analysis/DataflowView';
import { CallTreeView } from './views/analysis/CallTreeView';
import { SplitPane } from './components/SplitPane';
import { AiPanel } from './views/ai/AiPanel';
import { OutputView, DiagnosticsView } from './views/output/OutputView';
import { SAMPLES } from './samples';
import type { VmState } from './trace/types';

const EMPTY: VmState = { frames: [], heap: new Map(), stdout: '' };

const TABS = ['memory', 'flowchart', 'dataflow', 'calls'] as const;

const TAB_LABELS: Record<string, string> = {
  memory: 'Memory',
  flowchart: 'Flowchart',
  dataflow: 'Dataflow',
  calls: 'Execution Tree',
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
  const theme = useApp((s) => s.theme);
  const setTheme = useApp((s) => s.setTheme);
  
  // Terminal tabs state
  const [terminalTab, setTerminalTab] = useState<'output' | 'diagnostics'>('output');
  const [showAi, setShowAi] = useState(false);

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
    <div className={`app ${theme === 'light' ? 'theme-light' : ''}`}>
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

        <button 
          className="theme-toggle" 
          onClick={() => setTheme(theme === 'light' ? 'dark' : 'light')}
          aria-label="Toggle theme"
          style={{ marginLeft: 'auto', background: 'transparent', border: 'none', color: 'var(--text)', cursor: 'pointer', fontSize: '18px' }}
        >
          {theme === 'light' ? '🌙' : '☀️'}
        </button>

      </header>

      <main className="split">
        <SplitPane split="vertical" initialSizes={[40, 60]} minSizes={[320, 320]}>
          {/* LEFT COLUMN: Editor + Terminal */}
          <section className="pane pane-editor">
            <SplitPane split="horizontal" initialSizes={[65, 35]} minSizes={[150, 150]}>
              <div className="editor-container">
                <CodeEditor activeLine={activeLine} />
              </div>
              <div className="terminal-container">
            <nav className="tabs" role="tablist" aria-label="Terminal views">
              <button
                role="tab"
                aria-selected={terminalTab === 'output'}
                className={terminalTab === 'output' ? 'tab tab-active' : 'tab'}
                onClick={() => setTerminalTab('output')}
              >
                Output
              </button>
              <button
                role="tab"
                aria-selected={terminalTab === 'diagnostics'}
                className={terminalTab === 'diagnostics' ? 'tab tab-active' : 'tab'}
                onClick={() => setTerminalTab('diagnostics')}
              >
                Diagnostics
                {trace && trace.diagnostics.length > 0 && (
                  <span className="tab-count">{trace.diagnostics.length}</span>
                )}
              </button>
              {trace && <StatusPill />}
            </nav>
            <div className="view" role="tabpanel">
              {!trace && !error && <div className="empty-pane">Press <b>Run</b> to trace this program.</div>}
              {trace && terminalTab === 'output' && <OutputView stdout={state.stdout} />}
              {trace && terminalTab === 'diagnostics' && <DiagnosticsView trace={trace} />}
            </div>
          </div>
          </SplitPane>
        </section>

        {/* MIDDLE COLUMN: Visualizations */}
        <section className="pane pane-views">
          <nav
            className="tabs"
            role="tablist"
            aria-label="Visualization views"
            onKeyDown={(e) => {
              if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
              e.preventDefault();
              const at = TABS.indexOf(tab as any);
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
                tabIndex={tab === t ? 0 : -1}
                className={tab === t ? 'tab tab-active' : 'tab'}
                onClick={() => setTab(t)}
              >
                {TAB_LABELS[t]}
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
          </div>
        </section>
        </SplitPane>
      </main>

      <footer className="bottombar">
        <Transport />
      </footer>

      {/* Floating AI Bubble */}
      <div className="ai-bubble-container">
        {showAi && (
          <div className="ai-floating-panel">
            <AiPanel trace={trace} stepIndex={stepIndex} />
          </div>
        )}
        <button 
          className="ai-bubble-btn" 
          onClick={() => setShowAi(!showAi)}
          aria-label="Toggle AI Assistant"
        >
          {showAi ? '×' : '✨'}
        </button>
      </div>
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
