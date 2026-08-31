import type { CfgBlock, MethodAnalysis, Trace, Value } from '../../trace/types';

/**
 * Everything the static views need that is derived from the trace rather than from Roslyn.
 *
 * Deriving on the client rather than shipping it keeps the trace small and, more importantly,
 * keeps these views honest: they are functions of the same trace the memory view replays, so
 * they can never disagree with it.
 */

/** How many times each source line was executed, up to and including `upTo`. */
export function lineCounts(trace: Trace, upTo: number): Map<number, number> {
  const counts = new Map<number, number>();
  const last = Math.min(upTo, trace.steps.length - 1);
  for (let i = 0; i <= last; i++) {
    const line = trace.steps[i].line;
    counts.set(line, (counts.get(line) ?? 0) + 1);
  }
  return counts;
}

/**
 * Execution count per basic block.
 *
 * Blocks are matched to steps by source line. The interpreter walks Roslyn's unlowered
 * operation tree while the CFG is built from the lowered one, so the two share no operation
 * identity and a line is the only common ground. A line belonging to more than one block -
 * a `for` header, which is split into init, test and increment blocks - therefore credits each
 * of them. The heat map stays qualitatively right; the absolute numbers on a loop header are
 * an over-count, which is why the tooltip says "lines executed" rather than "times entered".
 */
export function blockCounts(method: MethodAnalysis, counts: Map<number, number>): Map<number, number> {
  const perBlock = new Map<number, number>();
  for (const block of method.blocks) {
    let total = 0;
    for (const line of block.lines) total += counts.get(line) ?? 0;
    perBlock.set(block.ordinal, total);
  }
  return perBlock;
}

/** The block that best contains a source line: the one covering the fewest lines overall. */
export function blockForLine(method: MethodAnalysis, line: number | null): number | null {
  if (line == null) return null;
  let best: CfgBlock | null = null;
  for (const block of method.blocks) {
    if (!block.lines.includes(line)) continue;
    if (best == null || block.lines.length < best.lines.length) best = block;
  }
  return best?.ordinal ?? null;
}

/** The method whose source range contains a line, if any. */
export function methodForLine(trace: Trace, line: number | null): MethodAnalysis | null {
  if (line == null) return null;
  let best: MethodAnalysis | null = null;
  for (const m of trace.methods) {
    if (line < m.startLine || line > m.endLine) continue;
    // Nested declarations are rare, but prefer the tightest range if they occur.
    if (best == null || m.endLine - m.startLine < best.endLine - best.startLine) best = m;
  }
  return best;
}

export interface CallNode {
  id: number;
  /** Index of the step whose delta pushed this frame; -1 for the synthetic root. */
  step: number;
  label: string;
  depth: number;
  children: CallNode[];
  /** Step index at which this frame was popped, or null if it never returned. */
  returnedAt: number | null;
  /** True while the player is inside this call. */
  active: boolean;
}

/**
 * Rebuilds the call tree from pushFrame/popFrame deltas.
 *
 * This is the whole call history, not just the live stack, which is what makes it useful for
 * recursion: the shape of a Fibonacci call tree is the lesson.
 */
export function callTree(trace: Trace, upTo: number): CallNode | null {
  const root: CallNode = {
    id: -1,
    step: -1,
    label: 'program',
    depth: 0,
    children: [],
    returnedAt: null,
    active: true,
  };

  const stack: CallNode[] = [root];
  const last = Math.min(upTo, trace.steps.length - 1);

  for (let i = 0; i < trace.steps.length; i++) {
    for (const op of trace.steps[i].delta) {
      if (op[0] === 'pushFrame') {
        const frame = op[1];
        const node: CallNode = {
          id: frame.id,
          step: i,
          label: frame.declaringType ? `${frame.declaringType}.${frame.methodName}` : frame.methodName,
          depth: stack.length,
          children: [],
          returnedAt: null,
          // A call is "active" only if the player has reached it and it has not yet returned.
          active: i <= last,
        };
        stack[stack.length - 1].children.push(node);
        stack.push(node);
      } else if (op[0] === 'popFrame') {
        const node = stack.pop();
        if (node) node.returnedAt = i;
        if (stack.length === 0) stack.push(root);
      }
    }
  }

  markActive(root, last);
  return root;
}

function markActive(node: CallNode, upTo: number): boolean {
  // Reached, and either still running or returning after the current step.
  const started = node.step <= upTo;
  const returned = node.returnedAt != null && node.returnedAt <= upTo;
  node.active = started && !returned;
  for (const child of node.children) markActive(child, upTo);
  return node.active;
}

export interface DefUse {
  variable: string;
  /** Step at which the value was written. */
  defStep: number;
  defLine: number;
  /** Step at which it was read. */
  useStep: number;
  useLine: number;
  value: Value;
}

/**
 * Def-use edges: for each variable a line reads, the step that last wrote it.
 *
 * Writes come from the trace's setLocal deltas, so they are what actually happened. Reads come
 * from Roslyn's static analysis of each line, because recording every read would multiply the
 * trace's size for information that is almost entirely recoverable. The combination is exact
 * except where a line reads a variable only on some paths through it - a short-circuited
 * `a && b` reports reading b even on the runs where b was never evaluated.
 */
export function defUseChain(trace: Trace, upTo: number, limit = 400): DefUse[] {
  const readsByLine = new Map<number, string[]>();
  for (const method of trace.methods) {
    for (const facts of method.lineFacts) {
      if (facts.reads.length > 0) readsByLine.set(facts.line, facts.reads);
    }
  }

  interface Write { step: number; line: number; value: Value }

  // One scope per live frame, so `n` in a recursive call resolves to that call's own `n` and
  // never to its caller's. Frame ids repeat at the same depth, which is exactly why the
  // bookkeeping is a stack rather than a flat map keyed by id.
  const scopes: Map<string, Write>[] = [new Map()];

  // slotId -> name, per frame id. setLocal deltas carry only ids, so the names have to be
  // remembered from the pushFrame that declared them.
  const names = new Map<number, Map<number, string>>();
  const edges: DefUse[] = [];
  const last = Math.min(upTo, trace.steps.length - 1);

  for (let i = 0; i <= last; i++) {
    const step = trace.steps[i];
    const current = scopes[scopes.length - 1];

    // Reads are resolved before writes are applied: `n = n + 1` reads the old value.
    for (const variable of readsByLine.get(step.line) ?? []) {
      const def = current.get(variable);
      if (!def) continue;
      edges.push({
        variable,
        defStep: def.step,
        defLine: def.line,
        useStep: i,
        useLine: step.line,
        value: def.value,
      });
    }

    for (const op of step.delta) {
      if (op[0] === 'pushFrame') {
        const scope = new Map<string, Write>();
        for (const slot of op[1].slots) {
          // Binding an argument to a parameter is a write, and the call is where it happened.
          // Parameters are set when the frame is pushed rather than through a setLocal delta,
          // so without this a recursive function's most interesting dataflow - how n gets
          // smaller on each call - is missing entirely.
          if (slot.kind === 'param' && slot.value.k !== 'unset') {
            // step.line is the call site: the encoder attributes each op to the statement
            // that was actually executing when it happened.
            scope.set(slot.name, { step: i, line: step.line, value: slot.value });
          }
        }
        names.set(op[1].id, new Map(op[1].slots.map((s) => [s.slotId, s.name])));
        scopes.push(scope);
      } else if (op[0] === 'popFrame') {
        if (scopes.length > 1) scopes.pop();
      } else if (op[0] === 'setLocal') {
        const name = names.get(op[1])?.get(op[2]);
        if (name) {
          scopes[scopes.length - 1].set(name, { step: i, line: step.line, value: op[3] });
        }
      }
    }
  }

  // Newest last: the panel shows the most recent flow, which is what the user is looking at.
  return edges.slice(-limit);
}
