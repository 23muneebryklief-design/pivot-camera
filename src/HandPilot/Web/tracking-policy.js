// One inference may run at a time. A late callback cannot release another
// session's slot or turn an old hand position into new mouse input.
export class FrameGate {
  constructor(maxAgeMs = 120) {
    this.maxAgeMs = maxAgeMs;
    this.generation = 0;
    this.stop();
  }
  start(sessionId) {
    this.generation++;
    this.sessionId = sessionId;
    this.running = true;
    this.sequence = 0;
    this.pending = null;
  }
  stop() {
    this.generation++;
    this.running = false;
    this.sessionId = null;
    this.pending = null;
  }
  begin(timestamp) {
    if (!this.running || this.pending || !Number.isFinite(timestamp)) return null;
    this.pending = {
      generation: this.generation, sessionId: this.sessionId,
      sequence: ++this.sequence, timestamp
    };
    return this.pending;
  }
  isCurrent(token) {
    return !!token && this.running && token.generation === this.generation &&
      token.sessionId === this.sessionId && token === this.pending;
  }
  finish(result, now) {
    const token = this.pending;
    if (!this.isCurrent(token) || result.sessionId !== token.sessionId ||
        result.sequence !== token.sequence || result.timestamp !== token.timestamp) return null;
    this.pending = null;
    const age = now - token.timestamp;
    const fresh = Number.isFinite(age) && age >= 0 && age <= this.maxAgeMs;
    const valid = Array.isArray(result.points) && result.points.length === 21 &&
      result.points.every(p => p && Number.isFinite(p.x) && Number.isFinite(p.y));
    const points = fresh && valid ? result.points : [];
    return {
      sessionId: token.sessionId, sequence: token.sequence, timestamp: token.timestamp,
      points, overlayPoints: points, processingAgeMs: Number.isFinite(age) ? Math.max(0, age) : 0,
      inferenceMs: Number.isFinite(result.inferenceMs) ? Math.max(0, result.inferenceMs) : 0,
      backend: result.backend === 'GPU' ? 'GPU' : 'CPU'
    };
  }
}
