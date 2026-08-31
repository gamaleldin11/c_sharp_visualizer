import { useEffect, useMemo, useState } from 'react';
import {
  Background,
  Controls,
  ReactFlow,
  ReactFlowProvider,
  useNodesInitialized,
  useReactFlow,
  type Edge,
  type Node,
} from '@xyflow/react';
import type { MethodAnalysis, Step, Trace } from '../../trace/types';
import { layoutGraph } from '../memory/layout';
import { blockCounts, blockForLine, lineCounts, methodForLine } from './derive';

/**
 * The method's control-flow graph, with the trace painted onto it.
 *
 * The graph itself is Roslyn's - real basic blocks and real branch edges, never inferred from
 * the text. What makes it worth looking at is the overlay: the block containing the current
 * step is highlighted, and every block is shaded by how often it ran. A static CFG answers
 * "what could happen"; this answers "what did", which is the question a person stepping
 * through their own code is actually asking.
 */
export function FlowchartView({ trace, step }: { trace: Trace; step: Step | null }) {
  return (
    <ReactFlowProvider>
      <FlowchartCanvas trace={trace} step={step} />
    </ReactFlowProvider>
  );
}

const NODE_WIDTH = 220;

function FlowchartCanvas({ trace, step }: { trace: Trace; step: Step | null }) {
  const [selected, setSelected] = useState<string | null>(null);
  const [positions, setPositions] = useState<Record<string, { x: number; y: number }>>({});

  const active = step ? methodForLine(trace, step.line) : null;

  // Follow the executing method unless the user has picked one to pin.
  const method: MethodAnalysis | null = useMemo(() => {
    if (selected) return trace.methods.find((m) => key(m) === selected) ?? null;
    return active ?? trace.methods[0] ?? null;
  }, [selected, active, trace.methods]);

  const counts = useMemo(() => lineCounts(trace, step?.i ?? 0), [trace, step?.i]);
  const perBlock = useMemo(() => (method ? blockCounts(method, counts) : new Map()), [method, counts]);
  const currentBlock = useMemo(
    () => (method && active === method ? blockForLine(method, step?.line ?? null) : null),
    [method, active, step?.line],
  );

  const maxCount = useMemo(() => Math.max(1, ...perBlock.values()), [perBlock]);

  // Re-run layout only when the graph itself changes. The overlay changes every step and must
  // never move a node - a flowchart that reshuffles while you step is unreadable.
  const graphKey = method ? key(method) : '';

  useEffect(() => {
    if (!method) return;
    let cancelled = false;

    const nodes = method.blocks.map((b) => ({
      id: String(b.ordinal),
      width: NODE_WIDTH,
      height: blockHeight(b.label),
    }));

    const edges = method.blocks.flatMap((b) => {
      const out: { id: string; source: string; target: string }[] = [];
      if (b.conditionalTarget != null) {
        out.push({ id: `c${b.ordinal}`, source: String(b.ordinal), target: String(b.conditionalTarget) });
      }
      if (b.fallThrough != null) {
        out.push({ id: `f${b.ordinal}`, source: String(b.ordinal), target: String(b.fallThrough) });
      }
      return out;
    });

    layoutGraph(nodes, edges, 'DOWN')
      .then((result) => {
        if (!cancelled) setPositions(result);
      })
      .catch(() => {
        // A failed layout must not blank the pane; stack the blocks in ordinal order instead.
        if (cancelled) return;
        const fallback: Record<string, { x: number; y: number }> = {};
        method.blocks.forEach((b, i) => {
          fallback[String(b.ordinal)] = { x: 0, y: i * 110 };
        });
        setPositions(fallback);
      });

    return () => {
      cancelled = true;
    };
  }, [graphKey, method]);

  const nodes: Node[] = useMemo(() => {
    if (!method) return [];
    return method.blocks.map((b) => {
      const count = perBlock.get(b.ordinal) ?? 0;
      return {
        id: String(b.ordinal),
        position: positions[String(b.ordinal)] ?? { x: 0, y: b.ordinal * 110 },
        data: { label: renderBlock(b, count, maxCount) },
        type: 'default',
        draggable: false,
        selectable: false,
        style: blockStyle(b, count, maxCount, b.ordinal === currentBlock),
      };
    });
  }, [method, positions, perBlock, maxCount, currentBlock]);

  const edges: Edge[] = useMemo(() => {
    if (!method) return [];
    const out: Edge[] = [];
    for (const b of method.blocks) {
      const ran = (perBlock.get(b.ordinal) ?? 0) > 0;
      if (b.conditionalTarget != null) {
        out.push({
          id: `c${b.ordinal}`,
          source: String(b.ordinal),
          target: String(b.conditionalTarget),
          label: b.conditionalLabel ?? undefined,
          animated: false,
          className: ran ? 'cfg-edge cfg-edge-hot' : 'cfg-edge',
        });
      }
      if (b.fallThrough != null) {
        // The fall-through of a conditional block is the opposite branch, so it earns the
        // complementary label - otherwise only one of the two edges is explained.
        const label = b.conditionalLabel === 'true' ? 'false' : b.conditionalLabel === 'false' ? 'true' : undefined;
        out.push({
          id: `f${b.ordinal}`,
          source: String(b.ordinal),
          target: String(b.fallThrough),
          label,
          className: ran ? 'cfg-edge cfg-edge-hot' : 'cfg-edge',
        });
      }
    }
    return out;
  }, [method, perBlock]);

  const flow = useReactFlow();
  const ready = useNodesInitialized();

  useEffect(() => {
    if (ready && nodes.length > 0) {
      flow.fitView({ padding: 0.2, maxZoom: 1, duration: 200 });
    }
    // Deliberately keyed on the graph, not on the step: refitting every step would fight the
    // user's own panning.
  }, [ready, graphKey, flow]);

  if (trace.methods.length === 0) {
    return <div className="empty-pane">No control-flow graph is available for this program.</div>;
  }

  return (
    <div className="analysis-pane">
      <div className="analysis-bar">
        <label className="analysis-label" htmlFor="cfg-method">Method</label>
        <select
          id="cfg-method"
          className="analysis-select"
          value={method ? key(method) : ''}
          onChange={(e) => setSelected(e.target.value)}
        >
          {trace.methods.map((m) => (
            <option key={key(m)} value={key(m)}>
              {key(m)}
              {active && key(active) === key(m) ? '  (executing)' : ''}
            </option>
          ))}
        </select>
        {selected && (
          <button className="analysis-reset" onClick={() => setSelected(null)}>
            follow execution
          </button>
        )}
        <span className="analysis-hint">shading = lines executed so far</span>
      </div>

      <div className="analysis-canvas">
        <ReactFlow
          nodes={nodes}
          edges={edges}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable={false}
          proOptions={{ hideAttribution: true }}
          minZoom={0.15}
        >
          <Background gap={22} size={1} color="#20283a" />
          <Controls showInteractive={false} />
        </ReactFlow>
      </div>
    </div>
  );
}

function key(m: MethodAnalysis): string {
  return m.declaringType ? `${m.declaringType}.${m.name}` : m.name;
}

function blockHeight(label: string): number {
  const lines = label.split('\n').length;
  return 34 + lines * 16;
}

function renderBlock(b: { kind: string; label: string }, count: number, _max: number) {
  if (b.kind === 'entry') return 'start';
  if (b.kind === 'exit') return 'end';
  return (
    <div className="cfg-block">
      <pre className="cfg-code">{b.label || '(empty)'}</pre>
      <span className="cfg-count">{count === 0 ? 'not run' : `${count}x`}</span>
    </div>
  );
}

function blockStyle(
  b: { kind: string; reachable: boolean },
  count: number,
  max: number,
  isCurrent: boolean,
): React.CSSProperties {
  // Heat is on a square-root scale. A loop body runs orders of magnitude more often than the
  // setup around it, and on a linear scale everything except the hottest block is the same
  // flat colour.
  const heat = count === 0 ? 0 : Math.sqrt(count / max);

  return {
    width: NODE_WIDTH,
    padding: 0,
    borderRadius: 8,
    fontSize: 12,
    color: '#d7dce3',
    background: count === 0 ? '#171c28' : `rgba(56, 128, 224, ${0.12 + heat * 0.55})`,
    border: isCurrent
      ? '2px solid #f5a524'
      : b.reachable
        ? '1px solid #2b3purple'.replace('purple', '650')
        : '1px dashed #3a2b2b',
    opacity: b.reachable ? 1 : 0.55,
    boxShadow: isCurrent ? '0 0 0 4px rgba(245, 165, 36, 0.18)' : undefined,
  };
}
