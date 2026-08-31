import type { Frame, HeapObject, Keyframe, Op, Trace, Value, VmState } from './types';

/**
 * Reconstructs VM state for any step from keyframes plus deltas.
 *
 * SEMANTICS, verified against the backend encoder: a keyframe's snapshot is captured in
 * TraceEncoder.EndStep *after* that step's delta has already been applied. So the snapshot
 * at keyframe {stepIndex: K} is the state AFTER step K, and
 *
 *     stateAt(n) = snapshot(K) + deltas of steps K+1 .. n
 *
 * where K is the largest keyframe stepIndex <= n. Getting this off by one shifts every
 * variable in the UI by one step, which is very hard to spot by eye - hence the note.
 *
 * Forward stepping is incremental: the cached state is advanced in place rather than
 * rebuilt, so pressing "next" repeatedly costs one delta, not a full replay. Mutations use
 * copy-on-write so that React sees new object identities for exactly the things that
 * changed and can skip re-rendering everything else.
 */
export class TracePlayer {
  readonly trace: Trace;
  private cache: { step: number; state: VmState } | null = null;

  constructor(trace: Trace) {
    this.trace = trace;
  }

  get stepCount(): number {
    return this.trace.steps.length;
  }

  private keyframeFor(n: number): Keyframe | null {
    const kfs = this.trace.keyframes;
    if (kfs.length === 0) return null;
    let lo = 0;
    let hi = kfs.length - 1;
    let best: Keyframe | null = null;
    while (lo <= hi) {
      const mid = (lo + hi) >> 1;
      if (kfs[mid].stepIndex <= n) {
        best = kfs[mid];
        lo = mid + 1;
      } else {
        hi = mid - 1;
      }
    }
    return best ?? kfs[0];
  }

  private fromKeyframe(kf: Keyframe): VmState {
    const snap = this.trace.snapshots[kf.snapshotIndex];
    if (!snap) return { frames: [], heap: new Map(), stdout: '' };
    // Clone so that applying deltas never mutates the trace itself. Only runs on a seek,
    // not on ordinary forward stepping.
    const cloned = structuredClone(snap);
    const heap = new Map<number, HeapObject>();
    for (const [id, obj] of Object.entries(cloned.heap)) heap.set(Number(id), obj);
    return { frames: cloned.frames, heap, stdout: cloned.stdout ?? '' };
  }

  stateAt(n: number): VmState {
    const target = Math.max(0, Math.min(n, this.stepCount - 1));
    if (this.stepCount === 0) return { frames: [], heap: new Map(), stdout: '' };

    const kf = this.keyframeFor(target);
    if (!kf) return { frames: [], heap: new Map(), stdout: '' };

    let state: VmState;
    let from: number;

    // Reuse the cache only when moving forward and no keyframe boundary was crossed
    // backwards; otherwise restart from the keyframe.
    if (this.cache && this.cache.step <= target && this.cache.step >= kf.stepIndex) {
      state = this.cache.state;
      from = this.cache.step + 1;
    } else {
      state = this.fromKeyframe(kf);
      from = kf.stepIndex + 1;
    }

    for (let i = from; i <= target; i++) {
      const step = this.trace.steps[i];
      if (!step) continue;
      for (const op of step.delta) state = applyOp(state, op, this.trace);
    }

    this.cache = { step: target, state };
    // Fresh container identities so React re-renders on step change.
    return { frames: [...state.frames], heap: new Map(state.heap), stdout: state.stdout };
  }
}

function applyOp(state: VmState, op: Op, trace: Trace): VmState {
  switch (op[0]) {
    case 'pushFrame': {
      state.frames = [...state.frames, structuredClone(op[1])];
      return state;
    }
    case 'popFrame': {
      state.frames = state.frames.slice(0, -1);
      return state;
    }
    case 'setLocal': {
      const [, frameId, slotId, value] = op;
      state.frames = state.frames.map((f) =>
        f.id === frameId ? withSlot(f, slotId, (s) => ({ ...s, value })) : f,
      );
      return state;
    }
    case 'scope': {
      const [, frameId, slotId, inScope] = op;
      state.frames = state.frames.map((f) =>
        f.id === frameId ? withSlot(f, slotId, (s) => ({ ...s, inScope })) : f,
      );
      return state;
    }
    case 'newObj': {
      const [, objId, obj] = op;
      const heap = new Map(state.heap);
      heap.set(objId, structuredClone(obj));
      state.heap = heap;
      return state;
    }
    case 'setField': {
      const [, objId, name, value] = op;
      const existing = state.heap.get(objId);
      if (!existing || existing.k !== 'object') return state;
      const fields = existing.fields.some((f) => f.name === name)
        ? existing.fields.map((f) => (f.name === name ? { name, value } : f))
        : [...existing.fields, { name, value }];
      const heap = new Map(state.heap);
      heap.set(objId, { ...existing, fields });
      state.heap = heap;
      return state;
    }
    case 'setElem': {
      const [, objId, index, value] = op;
      const existing = state.heap.get(objId);
      if (!existing) return state;
      const heap = new Map(state.heap);
      if (existing.k === 'array') {
        const elems = [...existing.elems];
        elems[index] = value;
        heap.set(objId, { ...existing, elems });
      } else if (existing.k === 'list') {
        const backing = [...existing.backing];
        backing[index] = value;
        heap.set(objId, { ...existing, backing });
      } else {
        return state;
      }
      state.heap = heap;
      return state;
    }
    case 'stdout': {
      state.stdout += trace.strings[op[1]] ?? '';
      return state;
    }
    default:
      return state;
  }
}

/**
 * Updates one slot, creating it if the frame was pushed before its slots were populated
 * (the entry frame is pushed with an empty slot list). Without this, locals written to a
 * frame that has not declared them yet would silently vanish from the UI.
 */
function withSlot(frame: Frame, slotId: number, update: (s: Frame['slots'][number]) => Frame['slots'][number]): Frame {
  const idx = frame.slots.findIndex((s) => s.slotId === slotId);
  if (idx === -1) {
    return {
      ...frame,
      slots: [
        ...frame.slots,
        update({
          slotId,
          name: `slot${slotId}`,
          kind: 'local',
          declaredLine: 0,
          inScope: true,
          value: { k: 'unset' } as Value,
        }),
      ],
    };
  }
  const slots = [...frame.slots];
  slots[idx] = update(slots[idx]);
  return { ...frame, slots };
}
