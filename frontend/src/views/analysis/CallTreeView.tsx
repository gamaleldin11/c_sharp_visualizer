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
import type { Trace } from '../../trace/types';
import { callTree, type CallNode } from './derive';
import { layoutGraph } from '../memory/layout';

const NODE_WIDTH = 240;
const NODE_HEIGHT = 80;

export function CallTreeView({ trace, stepIndex }: { trace: Trace; stepIndex: number }) {
  return (
    <ReactFlowProvider>
      <RecursionCanvas trace={trace} stepIndex={stepIndex} />
    </ReactFlowProvider>
  );
}

function RecursionCanvas({ trace, stepIndex }: { trace: Trace; stepIndex: number }) {
  const setStep = useApp((s) => s.setStep);
  const theme = useApp((s) => s.theme);
  const [positions, setPositions] = useState<Record<string, { x: number; y: number }>>({});

  const root = useMemo(() => callTree(trace, stepIndex), [trace, stepIndex]);

  const total = useMemo(() => (root ? countCalls(root) - 1 : 0), [root]);
  const maxDepth = useMemo(() => (root ? getMaxDepth(root) : 0), [root]);
  const baseCases = useMemo(() => (root ? countBaseCases(root) : 0), [root]);
  
  useEffect(() => {
    if (!root) return;
    let cancelled = false;

    const layoutNodes: any[] = [];
    const layoutEdges: any[] = [];

    function traverse(node: CallNode) {
      if (node.id !== -1) {
        layoutNodes.push({
          id: String(node.id),
          width: NODE_WIDTH,
          height: NODE_HEIGHT,
        });
      }
      for (const child of node.children) {
        if (node.id !== -1) {
          layoutEdges.push({
            id: `e-${node.id}-${child.id}`,
            source: String(node.id),
            target: String(child.id),
          });
        }
        traverse(child);
      }
    }
    traverse(root);

    layoutGraph(layoutNodes, layoutEdges, 'TB')
      .then((result) => {
        if (!cancelled) setPositions(result);
      })
      .catch(() => {
        if (!cancelled) setPositions({});
      });

    return () => {
      cancelled = true;
    };
  }, [root]);

  const nodes: Node[] = useMemo(() => {
    if (!root) return [];
    const out: Node[] = [];
    
    function traverse(node: CallNode) {
      if (node.id !== -1) {
        out.push({
          id: String(node.id),
          position: positions[String(node.id)] ?? { x: 0, y: 0 },
          data: { node, onSeek: setStep },
          type: 'recursion',
          draggable: false,
          selectable: false,
        });
      }
      for (const child of node.children) traverse(child);
    }
    traverse(root);
    
    return out;
  }, [root, positions, setStep]);

  const edges: Edge[] = useMemo(() => {
    if (!root) return [];
    const out: Edge[] = [];
    
    function traverse(node: CallNode) {
      for (const child of node.children) {
        if (node.id !== -1) {
          out.push({
            id: `e-${node.id}-${child.id}`,
            source: String(node.id),
            target: String(child.id),
            type: 'smoothstep',
            animated: child.active,
            className: child.active ? 'recursion-edge recursion-edge-hot' : 'recursion-edge',
          });
        }
        traverse(child);
      }
    }
    traverse(root);
    
    return out;
  }, [root]);

  const flow = useReactFlow();
  const init = useNodesInitialized();
  useEffect(() => {
    if (init && nodes.length > 0) {
      flow.fitView({ padding: 0.2, maxZoom: 1, duration: 800 });
    }
  }, [init, nodes.length, flow]);

  if (!root || root.children.length === 0) {
    return <div className="empty-pane">This program made no method calls.</div>;
  }

  return (
    <div className="analysis-pane">
      <div className="react-flow-wrapper" style={{ flex: 1, position: 'relative' }}>
        <ReactFlow
          nodes={nodes}
          edges={edges}
          nodeTypes={nodeTypes}
          nodesDraggable={false}
          nodesConnectable={false}
          elementsSelectable={false}
          colorMode={theme}
          fitView
          fitViewOptions={{ padding: 0.2, maxZoom: 1 }}
        >
          <Background color="var(--border)" />
          <Controls showInteractive={false} />
          
          <div className="recursion-hud legend-hud">
            <div className="legend-item"><div className="legend-box active" /> Active call</div>
            <div className="legend-item"><div className="legend-box waiting" /> Waiting (on stack)</div>
            <div className="legend-item"><div className="legend-box base" /> Returned (leaf)</div>
            <div className="legend-item"><div className="legend-box recursive" /> Returned (caller)</div>
          </div>
          
          <div className="recursion-hud stats-hud">
            <h4>STATS</h4>
            <div className="stat-line"><span>CALLS SO FAR</span> <span className="stat-val">{total}</span></div>
            <div className="stat-line"><span>MAX DEPTH</span> <span className="stat-val">{maxDepth}</span></div>
            <div className="stat-line"><span>LEAF CALLS</span> <span className="stat-val">{baseCases}</span></div>
          </div>
        </ReactFlow>
      </div>
    </div>
  );
}

function RecursionNodeComponent({ data }: { data: any }) {
  const node: CallNode = data.node;
  
  const isReturned = node.returnedAt != null;
  const isBaseCase = isReturned && node.children.length === 0;
  
  const hasActiveChildren = node.children.some(c => c.active);
  const isCurrentlyExecuting = node.active && !hasActiveChildren;
  
  let stateClass = '';
  if (isCurrentlyExecuting) stateClass = 'state-active';
  else if (node.active) stateClass = 'state-waiting';
  else if (isBaseCase) stateClass = 'state-base';
  else if (isReturned) stateClass = 'state-recursive';
  else stateClass = 'state-future';

  return (
    <div className={`recursion-node ${stateClass}`} onClick={() => data.onSeek(node.step)}>
      <Handle type="target" position={Position.Top} className="recursion-handle" />
      <div className="recursion-node-inner">
        <div className="recursion-label">{node.label}</div>
        <div className="recursion-step">#{node.step + 1}</div>
        {node.returnValue && (
          <div className="recursion-return">
            {node.returnValue}
          </div>
        )}
      </div>
      <Handle type="source" position={Position.Bottom} className="recursion-handle" />
    </div>
  );
}

const nodeTypes = {
  recursion: RecursionNodeComponent,
};

function countCalls(node: CallNode): number {
  let n = 1;
  for (const child of node.children) n += countCalls(child);
  return n;
}

function getMaxDepth(node: CallNode): number {
  if (node.children.length === 0) return 0;
  let max = 0;
  for (const child of node.children) {
    max = Math.max(max, 1 + getMaxDepth(child));
  }
  return max;
}

function countBaseCases(node: CallNode): number {
  if (node.id !== -1 && node.returnedAt != null && node.children.length === 0) {
    return 1;
  }
  let sum = 0;
  for (const child of node.children) {
    sum += countBaseCases(child);
  }
  return sum;
}
