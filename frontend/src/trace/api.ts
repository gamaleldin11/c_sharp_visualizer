import type { Trace } from './types';

export interface RunRequest {
  source: string;
  stdin?: string;
}

/** Posts source to the tracer. Relative URL: dev proxies it, prod serves same-origin. */
export async function runTrace(req: RunRequest, signal?: AbortSignal): Promise<Trace> {
  const res = await fetch('/api/trace', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ source: req.source, stdin: req.stdin ?? '' }),
    signal,
  });

  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try {
      const body = await res.json();
      if (body && typeof body.error === 'string') detail = body.error;
    } catch {
      /* response had no JSON body; the status line is the best available message */
    }
    throw new Error(detail);
  }

  return (await res.json()) as Trace;
}

export interface AiStatus {
  available: boolean;
  reason: string | null;
  callsToday: number;
  dailyBudget: number;
}

export interface Narration {
  text: string;
  cached: boolean;
}

export interface Explanation {
  cause: string;
  evidenceSteps: number[];
  suggestedFix: string;
  fixedLine: string | null;
  droppedCitations: number;
  cached: boolean;
}

/**
 * Whether the server can produce AI explanations.
 *
 * Never throws. A visualizer that fails to load because an optional feature is unreachable
 * would be a worse product than one that quietly does without it, so any failure here is
 * reported as simply unavailable.
 */
export async function aiStatus(): Promise<AiStatus> {
  try {
    const res = await fetch('/api/ai/status');
    if (!res.ok) return { available: false, reason: null, callsToday: 0, dailyBudget: 0 };
    return (await res.json()) as AiStatus;
  } catch {
    return { available: false, reason: null, callsToday: 0, dailyBudget: 0 };
  }
}

async function postAi<T>(url: string, body: unknown): Promise<T> {
  const res = await fetch(url, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });

  if (!res.ok) {
    let detail = `${res.status} ${res.statusText}`;
    try {
      const parsed = await res.json();
      if (parsed && typeof parsed.error === 'string') detail = parsed.error;
    } catch {
      /* no JSON body */
    }
    throw new Error(detail);
  }

  return (await res.json()) as T;
}

/**
 * Narrates one step.
 *
 * Only the source hash and the step index go over the wire. The server re-derives the trace
 * from its own cache, so the client cannot put text of its own choosing in front of the model
 * while claiming the interpreter produced it.
 */
export function narrateStep(sourceHash: string, stepIndex: number): Promise<Narration> {
  return postAi<Narration>('/api/ai/narrate', { sourceHash, stepIndex });
}

export function explainTrace(sourceHash: string): Promise<Explanation> {
  return postAi<Explanation>('/api/ai/explain', { sourceHash });
}
