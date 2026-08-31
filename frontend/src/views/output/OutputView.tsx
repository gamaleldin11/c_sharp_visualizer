import type { Diagnostic, Trace } from '../../trace/types';
import { SupportPanel } from './SupportPanel';

export function OutputView({ stdout }: { stdout: string }) {
  if (!stdout) return <div className="empty-pane">No output yet at this step.</div>;
  return <pre className="stdout">{stdout}</pre>;
}

export function DiagnosticsView({ trace }: { trace: Trace }) {
  // The support list belongs here whether or not anything went wrong: this is the pane a user
  // reaches after an unsupported-construct diagnostic, and the answer to "why not?" should be
  // one click away rather than somewhere else entirely.
  return (
    <div className="diagnostics-pane">
      {trace.diagnostics.length === 0 ? (
        <div className="empty-pane">No diagnostics.</div>
      ) : (
        <DiagnosticList diagnostics={trace.diagnostics} />
      )}
      <SupportPanel />
    </div>
  );
}

function DiagnosticList({ diagnostics }: { diagnostics: Diagnostic[] }) {
  return (
    <ul className="diagnostics">
      {diagnostics.map((d: Diagnostic, i: number) => (
        <li key={i} className={d.severity >= 3 ? 'diag diag-error' : 'diag diag-warn'}>
          <span className="diag-loc">
            {d.line}:{d.col}
          </span>
          <span className="diag-id">{d.id}</span>
          <span className="diag-msg">{d.message}</span>
        </li>
      ))}
    </ul>
  );
}
