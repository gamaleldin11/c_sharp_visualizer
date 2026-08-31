import { useCallback, useEffect, useRef, useState } from 'react';
import { useApp } from '../../state/store';
import type { Trace } from '../../trace/types';
import { explainTrace, narrateStep, type Explanation } from '../../trace/api';

/**
 * Plain-English narration of the current step, and a post-mortem when a program crashes.
 *
 * The panel is strictly additive. If the server has no API key, has hit its daily budget, or
 * simply cannot reach the model, this renders a single line saying so and the rest of the
 * visualizer is untouched - the diagrams are derived from the trace and never from the model.
 *
 * Narration is on demand rather than automatic. Firing a request on every arrow-key press
 * would burn the budget in a minute of ordinary stepping, and the answer is only wanted when
 * a step is actually confusing.
 */
export function AiPanel({ trace, stepIndex }: { trace: Trace; stepIndex: number }) {
  const setStep = useApp((s) => s.setStep);
  const aiAvailable = useApp((s) => s.aiAvailable);
  const aiReason = useApp((s) => s.aiReason);

  const [narration, setNarration] = useState<string | null>(null);
  const [explanation, setExplanation] = useState<Explanation | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Narration belongs to the step it was requested for. Without this, stepping away leaves
  // the previous step's explanation on screen next to the new step's memory diagram, which is
  // actively misleading.
  const narratedStep = useRef<number | null>(null);
  if (narratedStep.current !== null && narratedStep.current !== stepIndex && narration !== null) {
    narratedStep.current = null;
    setNarration(null);
    setError(null);
  }

  const failed = trace.status === 'runtime_error' || trace.status === 'limit_exceeded';

  useEffect(() => {
    setExplanation(null);
  }, [trace.sourceHash]);

  const narrate = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await narrateStep(trace.sourceHash, stepIndex);
      narratedStep.current = stepIndex;
      setNarration(result.text);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [trace.sourceHash, stepIndex]);

  const explain = useCallback(async () => {
    setBusy(true);
    setError(null);
    try {
      setExplanation(await explainTrace(trace.sourceHash));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [trace.sourceHash]);

  if (!aiAvailable) {
    return (
      <div className="ai-pane">
        <div className="empty-pane">
          AI explanations are turned off.
          {aiReason && <div className="ai-reason">{aiReason}</div>}
          <div className="ai-reason">Every other view works without them.</div>
        </div>
      </div>
    );
  }

  return (
    <div className="ai-pane">
      <div className="analysis-bar">
        <button className="ai-button" onClick={narrate} disabled={busy}>
          {busy ? 'Thinking…' : `Explain step ${stepIndex + 1}`}
        </button>
        {failed && (
          <button className="ai-button" onClick={explain} disabled={busy}>
            Explain the failure
          </button>
        )}
        <span className="analysis-hint">generated from this step only</span>
      </div>

      <div className="ai-scroll">
        {error && <div className="banner banner-error">{error}</div>}

        {narration && (
          <section className="ai-card">
            <h3 className="ai-title">Step {stepIndex + 1}</h3>
            <p className="ai-text">{narration}</p>
          </section>
        )}

        {explanation && (
          <section className="ai-card">
            <h3 className="ai-title">Why it failed</h3>
            <p className="ai-text">{explanation.cause}</p>

            {explanation.evidenceSteps.length > 0 && (
              <p className="ai-text">
                <span className="ai-label">Evidence:</span>{' '}
                {explanation.evidenceSteps.map((i) => (
                  <button key={i} className="df-link ai-cite" onClick={() => setStep(i)}>
                    step {i + 1}
                  </button>
                ))}
              </p>
            )}

            {/* Shown rather than hidden: a model that cited steps which do not exist is a
                model whose reasoning deserves less trust, and the reader should know. */}
            {explanation.droppedCitations > 0 && (
              <p className="ai-warn">
                {explanation.droppedCitations} cited step
                {explanation.droppedCitations === 1 ? '' : 's'} did not exist in this trace and
                {explanation.droppedCitations === 1 ? ' was' : ' were'} discarded.
              </p>
            )}

            {explanation.suggestedFix && (
              <>
                <h3 className="ai-title">Suggested fix</h3>
                <p className="ai-text">{explanation.suggestedFix}</p>
              </>
            )}

            {explanation.fixedLine && <pre className="ai-code">{explanation.fixedLine}</pre>}
          </section>
        )}

        {!narration && !explanation && !error && (
          <div className="empty-pane">
            Ask for an explanation of the step you are on.
            {failed && ' This program failed, so you can also ask why.'}
          </div>
        )}
      </div>
    </div>
  );
}
