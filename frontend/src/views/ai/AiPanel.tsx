import { useCallback, useEffect, useState } from 'react';
import { useApp } from '../../state/store';
import type { Trace } from '../../trace/types';
import { explainTrace, narrateStep, chatStep, type Explanation } from '../../trace/api';

/**
 * Plain-English narration of the current step, a post-mortem when a program crashes,
 * and a conversational AI tutor for asking direct questions about the code.
 *
 * The panel is strictly additive. If the server has no API key, has hit its daily budget, or
 * simply cannot reach the model, this renders a single line saying so and the rest of the
 * visualizer is untouched - the diagrams are derived from the trace and never from the model.
 *
 * AI calls are on demand rather than automatic. Firing a request on every arrow-key press
 * would burn the budget in a minute of ordinary stepping.
 */
export function AiPanel({ trace, stepIndex }: { trace: Trace | null; stepIndex: number }) {
  const setStep = useApp((s) => s.setStep);
  const setSource = useApp((s) => s.setSource);
  const aiAvailable = useApp((s) => s.aiAvailable);
  const aiReason = useApp((s) => s.aiReason);
  
  const chatHistory = useApp((s) => s.chatHistory);
  const addChatMessage = useApp((s) => s.addChatMessage);
  const clearChatHistory = useApp((s) => s.clearChatHistory);

  const [explanation, setExplanation] = useState<Explanation | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const [chatInput, setChatInput] = useState('');

  const failed = trace ? (trace.status === 'runtime_error' || trace.status === 'limit_exceeded') : false;

  useEffect(() => {
    setExplanation(null);
  }, [trace?.sourceHash]);

  const narrate = useCallback(async () => {
    if (!trace) return;
    setBusy(true);
    setError(null);
    try {
      const result = await narrateStep(trace.sourceHash, stepIndex);
      addChatMessage({ role: 'bot', text: `**Explanation for Step ${stepIndex + 1}:**\n${result.text}` });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [trace?.sourceHash, stepIndex, addChatMessage]);

  const explain = useCallback(async () => {
    if (!trace) return;
    setBusy(true);
    setError(null);
    try {
      setExplanation(await explainTrace(trace.sourceHash));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [trace?.sourceHash]);

  const sendChat = useCallback(async () => {
    if (!chatInput.trim()) return;
    const msg = chatInput;
    setChatInput('');
    addChatMessage({ role: 'user', text: msg });
    setBusy(true);
    setError(null);
    try {
      const result = await chatStep(trace?.sourceHash ?? null, stepIndex, msg);
      addChatMessage({ role: 'bot', text: result.text });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }, [chatInput, trace?.sourceHash, stepIndex, addChatMessage]);

  const renderMessage = (text: string) => {
    // Splits by markdown code blocks. The code block content is captured in group 1.
    const parts = text.split(/```(?:\w+)?\n([\s\S]*?)```/);
    return parts.map((part, i) => {
      if (i % 2 === 1) {
        return (
          <div key={i} style={{ position: 'relative', marginTop: '8px', marginBottom: '8px' }}>
            <pre className="ai-code">{part}</pre>
            <button 
              className="ai-button" 
              style={{ position: 'absolute', top: '8px', right: '8px', padding: '2px 6px', fontSize: '10px' }}
              onClick={() => setSource(part.trim())}
              title="Replace the editor content with this code"
            >
              Apply to Editor
            </button>
          </div>
        );
      }
      return <span key={i} style={{ whiteSpace: 'pre-wrap' }}>{part}</span>;
    });
  };

  if (!aiAvailable) {
    return (
      <div className="ai-pane">
        <div className="empty-pane">
          AI explanations are turned off.
          {aiReason && <div className="ai-reason">{aiReason}</div>}
        </div>
      </div>
    );
  }

  return (
    <div className="ai-pane">
      <div className="analysis-bar">
        <button className="ai-button" onClick={narrate} disabled={busy || !trace}>
          {busy ? 'Thinking…' : `Explain step ${stepIndex + 1}`}
        </button>
        {failed && (
          <button className="ai-button" onClick={explain} disabled={busy}>
            Explain the failure
          </button>
        )}
        <button className="ai-button" style={{ marginLeft: 'auto', background: 'transparent', borderColor: 'var(--border)', color: 'var(--muted)' }} onClick={clearChatHistory} title="Clear all chat messages">
          Clear Messages
        </button>
      </div>

      <div className="ai-scroll" style={{ display: 'flex', flexDirection: 'column' }}>
        {error && <div className="banner banner-error">{error}</div>}

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
            {explanation.suggestedFix && (
              <>
                <h3 className="ai-title">Suggested fix</h3>
                <p className="ai-text">{explanation.suggestedFix}</p>
              </>
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
            {explanation.fixedLine && <pre className="ai-code">{explanation.fixedLine}</pre>}
          </section>
        )}

        {chatHistory.map((msg, i) => (
          <section key={i} className="ai-card" style={{ background: msg.role === 'user' ? 'var(--panel-2)' : 'var(--panel)' }}>
            <h3 className="ai-title">{msg.role === 'user' ? 'You' : 'AI Tutor'}</h3>
            <div className="ai-text">{renderMessage(msg.text)}</div>
          </section>
        ))}
      </div>

      {/* Input container moved outside of scroll area so it is pinned to the bottom */}
      <div style={{ display: 'flex', gap: '8px', alignItems: 'center', padding: '10px 14px', background: 'var(--panel)', borderTop: '1px solid var(--border)' }}>
        <input
          type="text"
          value={chatInput}
          onChange={(e) => setChatInput(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && sendChat()}
          placeholder="Ask a question about this step..."
          style={{
            flex: 1, padding: '8px 12px', borderRadius: '6px', border: '1px solid var(--border)', 
            background: 'var(--bg)', color: 'var(--text)', fontFamily: 'inherit'
          }}
        />
        <button className="ai-button" onClick={sendChat} disabled={busy || !chatInput.trim()}>
          Send
        </button>
      </div>
    </div>
  );
}
