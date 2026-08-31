// Wire format for schema/trace.schema.json.
// Long term these should be generated with json-schema-to-typescript so they cannot drift
// from the backend DTOs; they are hand-written for now and must be kept in step with
// backend/src/CsViz.Trace/SchemaDto.cs.

export type TraceStatus = 'ok' | 'compile_error' | 'runtime_error' | 'limit_exceeded';
export type LimitHit = 'steps' | 'heap' | 'stackDepth' | 'output' | 'time';
export type StepKind = 'stmt' | 'expr' | 'call' | 'return' | 'throw' | 'catch';

export interface Diagnostic {
  severity: number; // 3 = error, 2 = warning
  line: number;
  col: number;
  endLine: number;
  endCol: number;
  message: string;
  id: string;
}

export interface TypeInfo {
  id: number;
  name: string;
  kind: 'class' | 'struct' | 'array' | 'enum' | 'interface' | 'primitive';
}

export type Value =
  | { k: 'prim'; t: string; v: unknown }
  | { k: 'null' }
  | { k: 'ref'; id: number }
  | { k: 'struct'; t: number; fields: Record<string, Value> }
  | { k: 'unset' };

export interface ObjectField {
  name: string;
  value: Value;
}

export type HeapObject =
  | { k: 'object'; t: number; fields: ObjectField[] }
  | { k: 'array'; t: number; dims: number[]; elems: Value[] }
  | { k: 'list'; t: number; count: number; capacity: number; backing: Value[] }
  | { k: 'dict'; t: number; entries: { key: Value; value: Value }[] }
  | { k: 'boxed'; t: number; value: Value }
  // Stack<T> and Queue<T>. They differ only in which end is next, so "kind" carries that
  // rather than duplicating the shape.
  | { k: 'seq'; t: number; kind: 'stack' | 'queue'; items: Value[] };

export interface Slot {
  slotId: number;
  name: string;
  kind: 'local' | 'param';
  declaredLine: number;
  inScope: boolean;
  value: Value;
}

export interface Frame {
  id: number;
  methodName: string;
  declaringType: string;
  callLine: number;
  slots: Slot[];
}

export interface Snapshot {
  frames: Frame[];
  heap: Record<string, HeapObject>;
  stdout: string;
}

export interface Keyframe {
  stepIndex: number;
  snapshotIndex: number;
}

export interface StepEvent {
  callee?: string;
  returnValue?: Value;
  exception?: { type: string; message: string };
}

export type Op =
  | ['setLocal', number, number, Value]
  | ['pushFrame', Frame]
  | ['popFrame']
  | ['setField', number, string, Value]
  | ['setElem', number, number, Value]
  | ['newObj', number, HeapObject]
  | ['stdout', number]
  | ['scope', number, number, boolean];

export interface Step {
  i: number;
  line: number;
  col: number;
  endLine: number;
  endCol: number;
  kind: StepKind;
  frameDepth: number;
  delta: Op[];
  event?: StepEvent;
}

/** One basic block of a method control-flow graph. */
export interface CfgBlock {
  ordinal: number;
  kind: 'entry' | 'exit' | 'block';
  label: string;
  lines: number[];
  condition?: string;
  fallThrough?: number;
  conditionalTarget?: number;
  conditionalLabel?: string;
  reachable: boolean;
}

/** Variables a source line reads and writes, determined statically by Roslyn. */
export interface LineFacts {
  line: number;
  reads: string[];
  writes: string[];
}

export interface MethodAnalysis {
  name: string;
  declaringType: string;
  startLine: number;
  endLine: number;
  blocks: CfgBlock[];
  lineFacts: LineFacts[];
}

export interface Trace {
  version: number;
  sourceHash: string;
  source: string;
  stdin?: string | null;
  status: TraceStatus;
  limitHit?: LimitHit;
  diagnostics: Diagnostic[];
  strings: string[];
  types: TypeInfo[];
  snapshots: Snapshot[];
  keyframes: Keyframe[];
  steps: Step[];
  methods: MethodAnalysis[];
}

/** Reconstructed virtual-machine state as of the end of some step. */
export interface VmState {
  /** Bottom-first, matching the backend's snapshot ordering. */
  frames: Frame[];
  heap: Map<number, HeapObject>;
  stdout: string;
}
