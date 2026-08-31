import { useMemo, useState } from 'react';
import { useApp } from '../../state/store';
import type { Trace, Value } from '../../trace/types';
import { defUseChain } from './derive';
import { ValueCell } from '../memory/nodes';

/**
 * Where each value came from.
 *
 * Every row is a real def-use edge: a line read a variable, and this is the step that last
 * wrote it, with the value it wrote. Answering "why is x 7 here?" by pointing at the exact
 * step that made it 7 is the question a debugger makes you reconstruct by hand.
 *
 * Both endpoints are clickable, so a chain of "where did this come from" walks backwards
 * through the execution.
 */
export function DataflowView({ trace, stepIndex }: { trace: Trace; stepIndex: number }) {
  const setStep = useApp((s) => s.setStep);
  const [filter, setFilter] = useState('');

  const typeName = useMemo(() => {
    const byId = new Map(trace.types.map((t) => [t.id, t.name]));
    return (id: number) => byId.get(id) ?? `type${id}`;
  }, [trace.types]);

  const edges = useMemo(() => defUseChain(trace, stepIndex), [trace, stepIndex]);

  const variables = useMemo(() => {
    const names = new Set<string>();
    for (const e of edges) names.add(e.variable);
    return [...names].sort();
  }, [edges]);

  const shown = useMemo(
    () => (filter ? edges.filter((e) => e.variable === filter) : edges).slice().reverse(),
    [edges, filter],
  );

  if (trace.methods.length === 0) {
    return <div className="empty-pane">No dataflow information is available for this program.</div>;
  }

  if (edges.length === 0) {
    return (
      <div className="empty-pane">
        No values have flowed yet. Step forward to see where each variable&rsquo;s value came from.
      </div>
    );
  }

  return (
    <div className="analysis-pane">
      <div className="analysis-bar">
        <label className="analysis-label" htmlFor="df-var">Variable</label>
        <select
          id="df-var"
          className="analysis-select"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        >
          <option value="">all</option>
          {variables.map((v) => (
            <option key={v} value={v}>{v}</option>
          ))}
        </select>
        <span className="analysis-hint">most recent first &middot; click a step to jump there</span>
      </div>

      <div className="dataflow-scroll">
        <div className="df-cards">
          {shown.map((e, i) => {
            const isActive = stepIndex === e.defStep || stepIndex === e.useStep;
            return (
              <div key={`${e.defStep}-${e.useStep}-${e.variable}-${i}`} className={`df-card ${isActive ? 'df-card-active' : ''}`}>
                <div className="df-card-header">
                  <span className="df-var">{e.variable}</span>
                  <span className="df-equals">=</span>
                  <span className="df-value">
                    <ValueCell value={e.value as Value} typeName={typeName} />
                  </span>
                </div>
                <div className="df-card-body">
                  <div className="df-event">
                    <span className="df-event-icon">📝</span>
                    <span>Set at <button className="df-link" onClick={() => setStep(e.defStep)}>line {e.defLine}</button> <span className="df-step">#{e.defStep + 1}</span></span>
                  </div>
                  <div className="df-event-arrow">↓</div>
                  <div className="df-event">
                    <span className="df-event-icon">👁️</span>
                    <span>Used at <button className="df-link" onClick={() => setStep(e.useStep)}>line {e.useLine}</button> <span className="df-step">#{e.useStep + 1}</span></span>
                  </div>
                </div>
              </div>
            );
          })}
        </div>
      </div>
    </div>
  );
}
