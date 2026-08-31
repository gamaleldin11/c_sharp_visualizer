import { useEffect, useMemo, useState } from 'react';
import {
  Background,
  Controls,
  ReactFlow,
  ReactFlowProvider,
  useNodesInitialized,
  useReactFlow,
  Handle,
  Position,
  type Edge,
  type Node,
} from '@xyflow/react';
import { useApp } from '../../state/store';
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
  const theme = useApp((s) => s.theme);
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

    const layoutNodes: any[] = [];
    const layoutEdges: any[] = [];

    method.blocks.forEach((b) => {
      const subs = splitBlockLines(b);
      subs.forEach((sub, i) => {
        layoutNodes.push({
          id: sub.id,
          width: sub.isCond ? NODE_WIDTH + 40 : NODE_WIDTH,
          height: blockHeight(sub.label, sub.isCond) + 20, // Extra space for explanation label
        });
        
        if (i > 0) {
          layoutEdges.push({
            id: `chain-${b.ordinal}-${i}`,
            source: subs[i-1].id,
            target: sub.id
          });
        }
      });

      const lastId = subs[subs.length - 1].id;
      if (b.conditionalTarget != null) {
        layoutEdges.push({ id: `c${b.ordinal}`, source: lastId, target: String(b.conditionalTarget) });
      }
      if (b.fallThrough != null) {
        layoutEdges.push({ id: `f${b.ordinal}`, source: lastId, target: String(b.fallThrough) });
      }
    });

    layoutGraph(layoutNodes, layoutEdges, 'DOWN')
      .then((result) => {
        if (!cancelled) setPositions(result);
      })
      .catch(() => {
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
    return method.blocks.flatMap((b) => {
      const count = perBlock.get(b.ordinal) ?? 0;
      const isBlockCurrent = b.ordinal === currentBlock;
      const subs = splitBlockLines(b);
      
      return subs.map((sub, i) => {
        let isCurrent = false;
        if (isBlockCurrent && step) {
          // Try to match the exact line. If lines array is present, map it.
          const lineNum = b.lines[Math.min(i, Math.max(0, b.lines.length - 1))];
          if (lineNum === step.line) isCurrent = true;
          // Fallback: if we couldn't match any specific line in this block, 
          // highlight all of them just in case (e.g. single line blocks)
          if (b.lines.length === 0) isCurrent = true;
        }

        // We no longer inject explanation into the node, as it will be on the edges!
        const blockWithExpl = { ...b, label: sub.label };
        
        return {
          id: sub.id,
          position: positions[sub.id] ?? { x: 0, y: b.ordinal * 110 },
          data: { b: blockWithExpl, count, maxCount, isCurrent },
          type: sub.type,
          draggable: false, selectable: false,
        };
      });
    });
  }, [method, positions, perBlock, maxCount, currentBlock, step]);

  const edges: Edge[] = useMemo(() => {
    if (!method) return [];
    
    // We need to look up explanations for the FIRST subnode of any target block
    // to label the inter-block edges.
    const firstSubExplanations = new Map<number, string | undefined>();
    method.blocks.forEach(b => {
      const subs = splitBlockLines(b);
      if (subs.length > 0) {
        firstSubExplanations.set(b.ordinal, subs[0].explanation);
      }
    });

    return method.blocks.flatMap((b) => {
      const out: Edge[] = [];
      const ran = (perBlock.get(b.ordinal) ?? 0) > 0;
      const subs = splitBlockLines(b);
      
      // Chain edges
      subs.forEach((sub, i) => {
        if (i > 0) {
          out.push({
             id: `chain-${b.ordinal}-${i}`,
             source: subs[i-1].id,
             target: sub.id,
             label: sub.explanation, // Put explanation on the arrow!
             animated: ran,
             className: ran ? 'cfg-edge cfg-edge-hot' : 'cfg-edge',
          });
        }
      });

      const lastId = subs[subs.length - 1].id;

      if (b.conditionalTarget != null) {
        const expl = firstSubExplanations.get(b.conditionalTarget);
        let label = b.conditionalLabel;
        if (expl && expl !== 'Statement execution') {
           label = label ? `${label} (${expl})` : expl;
        }
        
        out.push({
          id: `c${b.ordinal}`,
          source: lastId,
          target: String(b.conditionalTarget),
          label: label,
          animated: ran,
          className: ran ? 'cfg-edge cfg-edge-hot' : 'cfg-edge',
        });
      }
      
      if (b.fallThrough != null) {
        const expl = firstSubExplanations.get(b.fallThrough);
        let label = b.conditionalLabel === 'true' ? 'false' : b.conditionalLabel === 'false' ? 'true' : undefined;
        if (expl && expl !== 'Statement execution') {
           label = label ? `${label} (${expl})` : expl;
        }
        
        out.push({
          id: `f${b.ordinal}`,
          source: lastId,
          target: String(b.fallThrough),
          label: label,
          animated: ran,
          className: ran ? 'cfg-edge cfg-edge-hot' : 'cfg-edge',
        });
      }
      return out;
    });
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
        <label className="analysis-label" htmlFor="cfg-method">Function</label>
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
          nodeTypes={nodeTypes}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable={false}
          colorMode={theme}
          proOptions={{ hideAttribution: true }}
          minZoom={0.15}
        >
          <Background gap={22} size={1} color="var(--border)" />
          <Controls showInteractive={false} />
        </ReactFlow>
      </div>
    </div>
  );
}

function key(m: MethodAnalysis): string {
  return m.declaringType ? `${m.declaringType}.${m.name}` : m.name;
}

function blockHeight(label: string, isDiamond: boolean): number {
  const lines = label.split('\n').length;
  // Diamonds need more vertical padding to fit their text in the center
  return (isDiamond ? 60 : 34) + lines * 16;
}

function FlowchartNode({ data, shapeClass, colorClass }: { data: any; shapeClass: string; colorClass: string }) {
  const { b, count, maxCount, isCurrent } = data;
  const heat = count === 0 ? 0 : Math.sqrt(count / maxCount);

  let labelText = b.label || '(empty)';
  if (b.kind === 'entry') labelText = 'START';
  else if (b.kind === 'exit') labelText = 'END';
  else if (b.conditionalTarget != null && !labelText.trim().endsWith('?')) {
    labelText = labelText.trim() + '?';
  }

  return (
    <div
      className={`cfg-node ${shapeClass} ${colorClass} ${isCurrent ? 'cfg-node-current' : ''} ${!b.reachable ? 'cfg-node-unreachable' : ''}`}
      style={{ '--heat': heat } as React.CSSProperties}
    >
      <Handle type="target" position={Position.Top} className="cfg-handle" />
      <div className="cfg-content">
        <pre className="cfg-code">{labelText}</pre>
      </div>
      <Handle type="source" position={Position.Bottom} className="cfg-handle" />
    </div>
  );
}

const nodeTypes = {
  start: (props: any) => <FlowchartNode data={props.data} shapeClass="shape-pill" colorClass="color-green" />,
  stop: (props: any) => <FlowchartNode data={props.data} shapeClass="shape-pill" colorClass="color-red" />,
  process: (props: any) => <FlowchartNode data={props.data} shapeClass="shape-rect" colorClass="color-blue" />,
  decision: (props: any) => <FlowchartNode data={props.data} shapeClass="shape-diamond" colorClass="color-yellow" />,
  return: (props: any) => <FlowchartNode data={props.data} shapeClass="shape-rect" colorClass="color-purple" />,
  io: (props: any) => <FlowchartNode data={props.data} shapeClass="shape-para" colorClass="color-maroon" />
};

function explainLine(code: string): string {
  const line = code.trim();
  if (!line) return '';
  if (line.includes('new ')) return 'Object instantiation';
  if (line.includes('Console.Write') || line.includes('Console.Read')) return 'I/O operation';
  if (line.includes('=')) {
    if (line.includes('+') || line.includes('-') || line.includes('*') || line.includes('/')) {
      return 'Arithmetic & assignment';
    }
    return 'Variable assignment';
  }
  if (line.includes('+') || line.includes('-') || line.includes('*') || line.includes('/')) {
      return 'Arithmetic operation';
  }
  if (line.includes('return ')) return 'Method return';
  if (line.includes('(') && line.includes(')')) return 'Method call';
  return 'Statement execution';
}

function splitBlockLines(b: import('../../trace/types').CfgBlock) {
  const isStart = b.kind === 'entry';
  const isStop = b.kind === 'exit';

  if (isStart || isStop) {
    return [{
      id: String(b.ordinal),
      label: b.label,
      type: isStart ? 'start' : 'stop',
      isCond: false,
      explanation: isStart ? 'Entry point' : 'Exit point'
    }];
  }

  const lines = (b.label || '').split('\n').map(l => l.trim()).filter(l => l.length > 0);
  const hasSeparateCondition = b.conditionalTarget != null && b.condition && b.condition !== b.label;
  const processLines = hasSeparateCondition ? lines.filter(l => l !== b.condition) : lines;

  const out: any[] = [];
  processLines.forEach((line, i) => {
    const isIO = line.includes('Console.Write') || line.includes('Console.Read');
    const isReturn = line.startsWith('return ');
    let type = 'process';
    if (isReturn) type = 'return';
    else if (isIO) type = 'io';

    out.push({
      id: i === 0 ? String(b.ordinal) : `${b.ordinal}-${i}`,
      label: line,
      type,
      isCond: false,
      explanation: explainLine(line)
    });
  });

  if (hasSeparateCondition) {
    out.push({
      id: out.length === 0 ? String(b.ordinal) : `${b.ordinal}-cond`,
      label: b.condition!,
      type: 'decision',
      isCond: true,
      explanation: 'Decision check'
    });
  } else if (b.conditionalTarget != null && out.length > 0) {
    out[out.length - 1].type = 'decision';
    out[out.length - 1].isCond = true;
    out[out.length - 1].explanation = 'Decision check';
  }

  if (out.length === 0) {
    out.push({
      id: String(b.ordinal),
      label: b.label || ' ',
      type: b.conditionalTarget != null ? 'decision' : 'process',
      isCond: b.conditionalTarget != null,
      explanation: 'No operation'
    });
  }

  return out;
}
