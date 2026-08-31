import { useEffect, useMemo, useRef, useState } from 'react';
import {
  ReactFlow,
  ReactFlowProvider,
  Background,
  Controls,
  MarkerType,
  useReactFlow,
  useNodesInitialized,
  type Edge,
  type Node,
} from '@xyflow/react';
import type { Step, Trace, VmState } from '../../trace/types';
import { FrameNode, HeapNode, heapRefs, type TypeNames } from './nodes';
import { HEADER_HEIGHT, NODE_WIDTH, ROW_HEIGHT, heapNodeHeight, layoutHeap, type Positions } from './layout';

const nodeTypes = { frame: FrameNode, heap: HeapNode };
const HEAP_OFFSET_X = 330;

export function MemoryView(props: { state: VmState; trace: Trace; step: Step | null }) {
  return (
    <ReactFlowProvider>
      <MemoryCanvas {...props} />
    </ReactFlowProvider>
  );
}

function MemoryCanvas({ state, trace, step }: { state: VmState; trace: Trace; step: Step | null }) {
  const flow = useReactFlow();
  const typeName: TypeNames = useMemo(() => {
    const names = new Map(trace.types.map((t) => [t.id, t.name]));
    return (id: number) => names.get(id) ?? 'object';
  }, [trace]);

  // Object positions persist across steps and are recomputed only when the SET of heap
  // objects changes. Re-laying out every step would make boxes jump and destroy the
  // reader's ability to follow one object through the program.
  const positions = useRef<Positions>({});
  // Bumped when ELK returns; must be a dependency of the node memo below, otherwise the
  // freshly computed positions are never read and objects keep their stale coordinates.
  const [layoutVersion, setLayoutVersion] = useState(0);

  const heapIds = useMemo(() => [...state.heap.keys()].sort((a, b) => a - b), [state.heap]);

  // The layout key must cover the edge set as well as the node set. Keying on nodes alone
  // means a pointer assignment such as `a.Next = b` never re-runs the layout, so the two
  // objects stay stacked on top of each other in whatever arrangement they had when the
  // second one was allocated. Edges change rarely, so stability is preserved.
  const heapKey = useMemo(() => {
    const parts: string[] = [];
    for (const id of heapIds) {
      const obj = state.heap.get(id);
      if (!obj) continue;
      parts.push(`${id}>${heapRefs(obj).map((r) => r.target).join('.')}`);
    }
    return parts.join('|');
  }, [heapIds, state.heap]);

  // Objects mutated by the step just executed, for a brief highlight.
  const changed = useMemo(() => {
    const s = new Set<number>();
    for (const op of step?.delta ?? []) {
      if (op[0] === 'newObj' || op[0] === 'setField' || op[0] === 'setElem') s.add(op[1] as number);
    }
    return s;
  }, [step]);

  useEffect(() => {
    let cancelled = false;
    const nodes = heapIds.map((id) => {
      const obj = state.heap.get(id)!;
      return { id: `obj-${id}`, width: NODE_WIDTH, height: heapNodeHeight(obj) };
    });
    const edges: { id: string; source: string; target: string }[] = [];
    for (const id of heapIds) {
      const obj = state.heap.get(id);
      if (!obj) continue;
      for (const r of heapRefs(obj)) {
        if (!state.heap.has(r.target)) continue;
        edges.push({ id: `l-${id}-${r.handle}`, source: `obj-${id}`, target: `obj-${r.target}` });
      }
    }

    layoutHeap({ nodes, edges }, HEAP_OFFSET_X)
      .then((pos) => {
        if (cancelled) return;
        positions.current = { ...positions.current, ...pos };
        setLayoutVersion((v) => v + 1);
      })
      .catch((err) => {
        // Never fail silently: without a position every object falls back to the same
        // coordinate and the whole heap renders as one stack of overlapping boxes.
        console.error('[csviz] heap layout failed', err);
        if (cancelled) return;
        const fallback: Positions = {};
        heapIds.forEach((id, i) => {
          fallback[`obj-${id}`] = { x: HEAP_OFFSET_X + (i % 3) * 290, y: Math.floor(i / 3) * 130 };
        });
        positions.current = { ...positions.current, ...fallback };
        setLayoutVersion((v) => v + 1);
      });

    return () => {
      cancelled = true;
    };
    // Deliberately keyed on the object-id set, not on state.heap: field mutations must not
    // trigger a re-layout.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [heapKey]);

  const { nodes, edges } = useMemo(() => {
    const ns: Node[] = [];
    const es: Edge[] = [];

    // Stack frames: fixed left column, bottom frame at top, so growth reads downward.
    let y = 0;
    state.frames.forEach((frame, idx) => {
      const visible = frame.slots.filter((s) => s.inScope || s.value.k !== 'unset');
      ns.push({
        id: `frame-${frame.id}`,
        type: 'frame',
        position: { x: 0, y },
        data: { frame, isTop: idx === state.frames.length - 1, typeName },
        draggable: false,
        selectable: false,
      });
      y += HEADER_HEIGHT + Math.max(1, visible.length) * ROW_HEIGHT + 34;

      for (const s of visible) {
        if (s.value.k === 'ref') {
          es.push({
            id: `e-f${frame.id}-s${s.slotId}`,
            source: `frame-${frame.id}`,
            sourceHandle: `f${frame.id}-s${s.slotId}`,
            target: `obj-${s.value.id}`,
            targetHandle: `o${s.value.id}`,
            markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14 },
            className: 'edge-stack',
          });
        }
      }
    });

    for (const id of heapIds) {
      const obj = state.heap.get(id)!;
      ns.push({
        id: `obj-${id}`,
        type: 'heap',
        position: positions.current[`obj-${id}`] ?? { x: HEAP_OFFSET_X, y: 0 },
        data: { objId: id, obj, typeName, changed: changed.has(id) },
        draggable: false,
        selectable: false,
      });

      for (const r of heapRefs(obj)) {
        es.push({
          id: `e-o${id}-${r.handle}`,
          source: `obj-${id}`,
          sourceHandle: `o${id}-${r.handle}`,
          target: `obj-${r.target}`,
          targetHandle: `o${r.target}`,
          markerEnd: { type: MarkerType.ArrowClosed, width: 14, height: 14 },
          className: 'edge-heap',
        });
      }
    }

    // Drop edges whose target vanished (a field still pointing at a collected object).
    const present = new Set(ns.map((n) => n.id));
    return { nodes: ns, edges: es.filter((e) => present.has(e.target)) };
  }, [state, heapIds, typeName, changed, layoutVersion]);

  // Re-frame only when the object set changes, and only once the laid-out nodes have been
  // committed - fitting inside the layout promise runs a frame too early and leaves newly
  // allocated objects off-screen. Field mutations never change heapKey, so the view stays
  // put while stepping through ordinary assignments.
  const lastFitKey = useRef<string | null>(null);
  const nodesInitialized = useNodesInitialized();
  useEffect(() => {
    // fitView needs measured node dimensions; custom nodes are measured a frame after they
    // render, so firing earlier fits against zero-sized nodes and leaves objects off-screen.
    if (!nodesInitialized) return;
    if (lastFitKey.current === heapKey) return;
    lastFitKey.current = heapKey;
    flow.fitView({ padding: 0.22, maxZoom: 1, duration: 220 });
  }, [nodesInitialized, heapKey, flow]);

  if (state.frames.length === 0 && heapIds.length === 0) {
    return <div className="empty-pane">Nothing on the stack or heap at this step.</div>;
  }

  return (
    <ReactFlow
      nodes={nodes}
      edges={edges}
      nodeTypes={nodeTypes}
      fitView
      fitViewOptions={{ padding: 0.2, maxZoom: 1 }}
      minZoom={0.2}
      maxZoom={1.6}
      proOptions={{ hideAttribution: true }}
      nodesConnectable={false}
    >
      <Background gap={18} size={1} />
      <Controls showInteractive={false} />
    </ReactFlow>
  );
}
