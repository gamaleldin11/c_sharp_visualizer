import { Handle, Position } from '@xyflow/react';
import type { Frame, HeapObject, Value } from '../../trace/types';

export interface TypeNames {
  (typeId: number): string;
}

/** Renders a scalar. Reference values render as a small arrow; the edge carries the meaning. */
export function ValueCell({ value, typeName }: { value: Value; typeName: TypeNames }) {
  switch (value.k) {
    case 'prim':
      return <span className={`val val-${value.t}`}>{formatPrim(value.t, value.v)}</span>;
    case 'null':
      return <span className="val val-null">null</span>;
    case 'unset':
      return <span className="val val-unset">unset</span>;
    case 'ref':
      return <span className="val val-ref">&rarr;</span>;
    case 'struct':
      return (
        <span className="val-struct">
          <span className="struct-tag">{typeName(value.t)}</span>
          {Object.entries(value.fields).map(([k, v]) => (
            <span key={k} className="struct-field">
              <span className="struct-key">{k}</span>
              <ValueCell value={v} typeName={typeName} />
            </span>
          ))}
        </span>
      );
    default:
      return <span className="val val-unset">?</span>;
  }
}

function formatPrim(t: string, v: unknown): string {
  if (v === null || v === undefined) return 'null';
  if (t === 'string') return JSON.stringify(String(v));
  if (t === 'char') return `'${String(v)}'`;
  if (t === 'bool') return String(v).toLowerCase();
  return String(v);
}

export interface FrameNodeData extends Record<string, unknown> {
  frame: Frame;
  isTop: boolean;
  typeName: TypeNames;
}

export function FrameNode({ data }: { data: FrameNodeData }) {
  const { frame, isTop, typeName } = data;
  const visible = frame.slots.filter((s) => s.inScope || s.value.k !== 'unset');

  return (
    <div className={`node frame-node ${isTop ? 'frame-top' : ''}`}>
      <div className="node-header">
        <span className="frame-method">
          {frame.declaringType ? `${frame.declaringType}.` : ''}
          {frame.methodName}
        </span>
        {isTop && <span className="badge">active</span>}
      </div>
      <div className="node-body">
        {visible.length === 0 && <div className="row row-empty">no locals yet</div>}
        {visible.map((s) => (
          <div key={s.slotId} className={`row ${s.inScope ? '' : 'row-out'}`}>
            <span className="row-key">
              {s.name}
              {s.kind === 'param' && <span className="param-tag">param</span>}
            </span>
            <span className="row-val">
              <ValueCell value={s.value} typeName={typeName} />
            </span>
            {s.value.k === 'ref' && (
              <Handle
                type="source"
                position={Position.Right}
                id={`f${frame.id}-s${s.slotId}`}
                className="ref-handle"
              />
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

export interface HeapNodeData extends Record<string, unknown> {
  objId: number;
  obj: HeapObject;
  typeName: TypeNames;
  changed: boolean;
}

export function HeapNode({ data }: { data: HeapNodeData }) {
  const { objId, obj, typeName, changed } = data;
  const rows = heapRows(obj);

  return (
    <div className={`node heap-node ${changed ? 'node-changed' : ''}`}>
      <Handle type="target" position={Position.Left} id={`o${objId}`} className="target-handle" />
      <div className="node-header">
        <span className="heap-type">{headerLabel(obj, typeName)}</span>
        <span className="heap-id">#{objId}</span>
      </div>
      <div className="node-body">
        {rows.map((r) => (
          <div key={r.key} className="row">
            <span className="row-key">{r.key}</span>
            <span className="row-val">
              <ValueCell value={r.value} typeName={typeName} />
            </span>
            {r.value.k === 'ref' && (
              <Handle
                type="source"
                position={Position.Right}
                id={`o${objId}-${r.handle}`}
                className="ref-handle"
              />
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

function headerLabel(obj: HeapObject, typeName: TypeNames): string {
  switch (obj.k) {
    // The type name already ends in "[]" for arrays, so appending the length naively
    // produces "int[][5]"; put the length inside the existing brackets instead.
    case 'array': {
      const n = typeName(obj.t);
      return n.endsWith('[]') ? `${n.slice(0, -2)}[${obj.elems.length}]` : `${n}[${obj.elems.length}]`;
    }
    case 'list': return `${typeName(obj.t)} (${obj.count}/${obj.capacity})`;
    case 'dict': return `${typeName(obj.t)} (${obj.entries.length})`;
    case 'boxed': return `boxed ${typeName(obj.t)}`;
    case 'seq': return `${typeName(obj.t)} (${obj.items.length})`;
    default: return typeName(obj.t);
  }
}

interface Row { key: string; value: Value; handle: string }

function heapRows(obj: HeapObject): Row[] {
  switch (obj.k) {
    case 'object':
      return obj.fields.map((f) => ({ key: f.name, value: f.value, handle: `f${f.name}` }));
    case 'array':
      return obj.elems.map((v, i) => ({ key: `[${i}]`, value: v, handle: `e${i}` }));
    case 'list':
      // Show the backing array, including slack capacity - that is the point of drawing a
      // List<T> at all rather than treating it as an opaque box.
      return obj.backing.map((v, i) => ({ key: `[${i}]`, value: v, handle: `e${i}` }));
    case 'dict':
      return obj.entries.map((e, i) => ({
        key: e.key.k === 'prim' ? formatPrim(e.key.t, e.key.v) : `#${i}`,
        value: e.value,
        handle: `e${i}`,
      }));
    case 'boxed':
      return [{ key: 'value', value: obj.value, handle: 'v' }];
    case 'seq':
      // A stack pops from the end and a queue from the front, so the row that is next differs.
      // Labelling it is the whole reason these are not just drawn as arrays.
      return obj.items.map((v, i) => {
        const isNext = obj.kind === 'stack' ? i === obj.items.length - 1 : i === 0;
        return { key: isNext ? `[${i}] next` : `[${i}]`, value: v, handle: `e${i}` };
      });
    default:
      return [];
  }
}

/** Every ref reachable from a node, as (handleSuffix, targetObjId). */
export function heapRefs(obj: HeapObject): { handle: string; target: number }[] {
  return heapRows(obj)
    .filter((r) => r.value.k === 'ref')
    .map((r) => ({ handle: r.handle, target: (r.value as { k: 'ref'; id: number }).id }));
}
