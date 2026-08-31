import { useMemo } from 'react';
import { useApp } from '../../state/store';
import type { Trace } from '../../trace/types';
import { callTree, type CallNode } from './derive';

/**
 * Every call the program made, as a tree.
 *
 * This is the whole history rather than the live stack, which is the point: the shape of a
 * recursive Fibonacci - the same subtree computed over and over - is a thing you have to see
 * to understand, and no amount of stepping through the stack view shows it.
 *
 * Calls the player has not reached yet are dimmed rather than hidden, so the tree does not
 * jump around as you scrub; clicking any call seeks to it.
 */
export function CallTreeView({ trace, stepIndex }: { trace: Trace; stepIndex: number }) {
  const setStep = useApp((s) => s.setStep);

  // The tree is built from the whole trace, so it only changes when the trace does. Only the
  // active/reached flags depend on the current step.
  const root = useMemo(() => callTree(trace, stepIndex), [trace, stepIndex]);

  const total = useMemo(() => (root ? countCalls(root) - 1 : 0), [root]);

  if (!root || root.children.length === 0) {
    return <div className="empty-pane">This program made no method calls.</div>;
  }

  return (
    <div className="analysis-pane">
      <div className="analysis-bar">
        <span className="analysis-hint">
          {total} call{total === 1 ? '' : 's'} &middot; click one to jump to it
        </span>
      </div>
      <div className="calltree-scroll">
        <ul className="calltree">
          {root.children.map((child, i) => (
            <CallRow key={`${child.step}-${i}`} node={child} stepIndex={stepIndex} onSeek={setStep} />
          ))}
        </ul>
      </div>
    </div>
  );
}

function CallRow({
  node,
  stepIndex,
  onSeek,
}: {
  node: CallNode;
  stepIndex: number;
  onSeek: (i: number) => void;
}) {
  const reached = node.step <= stepIndex;
  const returned = node.returnedAt != null && node.returnedAt <= stepIndex;

  const className = [
    'calltree-node',
    !reached && 'calltree-future',
    node.active && 'calltree-active',
    returned && 'calltree-returned',
  ]
    .filter(Boolean)
    .join(' ');

  return (
    <li>
      <button className={className} onClick={() => onSeek(node.step)} title={`step ${node.step + 1}`}>
        <span className="calltree-name">{node.label}</span>
        <span className="calltree-step">#{node.step + 1}</span>
        {node.active && <span className="badge">on stack</span>}
      </button>
      {node.children.length > 0 && (
        <ul className="calltree">
          {node.children.map((child, i) => (
            <CallRow key={`${child.step}-${i}`} node={child} stepIndex={stepIndex} onSeek={onSeek} />
          ))}
        </ul>
      )}
    </li>
  );
}

function countCalls(node: CallNode): number {
  let n = 1;
  for (const child of node.children) n += countCalls(child);
  return n;
}
