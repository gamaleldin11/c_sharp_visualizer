import type { HeapObject } from '../../trace/types';

export const NODE_WIDTH = 250;
export const ROW_HEIGHT = 24;
export const HEADER_HEIGHT = 30;

/**
 * elkjs is ~1.4MB - by far the largest dependency, and a GWT-compiled Java layout engine that
 * nothing needs until a graph is actually drawn. Importing it dynamically keeps it out of the
 * initial bundle, so the editor and the transport are interactive while it loads.
 */
let elkPromise: Promise<{ layout: (g: unknown) => Promise<unknown> }> | null = null;

function getElk() {
  if (!elkPromise) {
    elkPromise = import('elkjs/lib/elk.bundled.js').then((mod) => {
      const Elk = (mod.default ?? mod) as new () => { layout: (g: unknown) => Promise<unknown> };
      return new Elk();
    });
  }
  return elkPromise;
}

/** Rough but stable height estimate; exact pixel height is not needed for routing. */
export function heapNodeHeight(obj: HeapObject): number {
  let rows = 1;
  switch (obj.k) {
    case 'object': rows = Math.max(1, obj.fields.length); break;
    case 'array': rows = Math.max(1, obj.elems.length); break;
    case 'list': rows = Math.max(1, obj.backing.length); break;
    case 'dict': rows = Math.max(1, obj.entries.length); break;
    case 'seq': rows = Math.max(1, obj.items.length); break;
    case 'boxed': rows = 1; break;
  }
  return HEADER_HEIGHT + rows * ROW_HEIGHT + 8;
}

export interface LayoutInput {
  nodes: { id: string; width: number; height: number }[];
  edges: { id: string; source: string; target: string }[];
}

export type Positions = Record<string, { x: number; y: number }>;

/**
 * Lays out a graph with ELK's layered algorithm.
 *
 * RIGHT for the heap, so pointer chains (linked lists, trees) read as columns; DOWN for a
 * control-flow graph, which is the direction every flowchart convention expects.
 */
export async function layoutGraph(
  nodes: LayoutInput['nodes'],
  edges: LayoutInput['edges'],
  direction: 'RIGHT' | 'DOWN',
  offsetX = 0,
): Promise<Positions> {
  if (nodes.length === 0) return {};

  const elk = await getElk();

  const graph = {
    id: 'root',
    layoutOptions: {
      'elk.algorithm': 'layered',
      'elk.direction': direction,
      'elk.layered.spacing.nodeNodeBetweenLayers': direction === 'DOWN' ? '48' : '90',
      'elk.spacing.nodeNode': direction === 'DOWN' ? '40' : '28',
      // Keeps sibling order stable between runs so nodes do not swap places when the graph
      // changes shape slightly.
      'elk.layered.considerModelOrder.strategy': 'NODES_AND_EDGES',
    },
    children: nodes.map((n) => ({ id: n.id, width: n.width, height: n.height })),
    edges: edges.map((e) => ({ id: e.id, sources: [e.source], targets: [e.target] })),
  };

  const res = (await elk.layout(graph)) as { children?: { id: string; x?: number; y?: number }[] };
  const out: Positions = {};
  for (const child of res.children ?? []) {
    out[child.id] = { x: (child.x ?? 0) + offsetX, y: child.y ?? 0 };
  }
  return out;
}

/** The heap graph, laid out left to right and shifted clear of the stack column. */
export async function layoutHeap(input: LayoutInput, offsetX: number): Promise<Positions> {
  return layoutGraph(input.nodes, input.edges, 'RIGHT', offsetX);
}
