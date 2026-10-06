import { JobDto, plainError, Protocol, StaleSessionError } from './protocol';
export class CancelledError extends Error { constructor() { super('Penguin job cancelled.'); } }
export class TimeoutError extends Error { constructor() { super('Penguin job timed out; cancellation was requested. Retry after checking the backend output.'); } }
export interface PollOptions {
    signal: AbortSignal; current: () => boolean; timeoutMs?: number; intervalMs?: number;
    progress?: (job: JobDto) => void; log: (message: string) => void;
    now?: () => number; wait?: (ms: number, signal: AbortSignal) => Promise<void>;
}
export function delay(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
        if (signal.aborted) { reject(new CancelledError()); return; }
        const abort = (): void => { clearTimeout(timer); reject(new CancelledError()); };
        const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve(); }, ms);
        signal.addEventListener('abort', abort, { once: true });
    });
}
export async function pollJob(protocol: Pick<Protocol, 'job' | 'cancelJob'>, initial: JobDto, options: PollOptions): Promise<JobDto> {
    const now = options.now ?? Date.now;
    const until = now() + (options.timeoutMs ?? 180_000);
    let job = initial;
    try {
        for (;;) {
            if (!options.current()) { throw new StaleSessionError(); }
            if (options.signal.aborted) { throw new CancelledError(); }
            options.progress?.(job);
            if (job.state === 'succeeded') { return job; }
            if (job.state === 'failed') { throw new Error(`Penguin job failed: ${plainError(job.error)}`); }
            if (job.state === 'cancelled') { throw new CancelledError(); }
            if (now() >= until) { throw new TimeoutError(); }
            await (options.wait ?? delay)(Math.min(options.intervalMs ?? 500, until - now()), options.signal);
            if (now() >= until) { throw new TimeoutError(); }
            job = await protocol.job(initial.id, options.signal);
            if (job.sessionId !== initial.sessionId || job.id !== initial.id) { throw new StaleSessionError(); }
        }
    } catch (error) {
        if ((options.signal.aborted || error instanceof CancelledError || error instanceof TimeoutError) && options.current()) {
            try { if (!await protocol.cancelJob(initial.id)) { options.log(`Cancellation was not accepted for ${initial.id}; it may already be terminal or expired.`); } } catch (cancelError) { options.log(`Could not cancel ${initial.id}: ${plainError(cancelError)}`); }
        }
        throw error;
    }
}
export async function bounded<T>(operation: Promise<T>, timeoutMs: number, signal?: AbortSignal, cancel?: () => void): Promise<T> {
    let timer: NodeJS.Timeout | undefined;
    let abort: (() => void) | undefined;
    try {
        return await Promise.race([operation, new Promise<never>((_, reject) => {
            abort = (): void => { cancel?.(); reject(new CancelledError()); };
            if (signal?.aborted) { abort(); return; }
            signal?.addEventListener('abort', abort, { once: true });
            timer = setTimeout(() => { cancel?.(); reject(new Error('Penguin request timed out. Check Penguin Output and restart the backend.')); }, timeoutMs);
        })]);
    } finally {
        if (timer) { clearTimeout(timer); }
        if (abort) { signal?.removeEventListener('abort', abort); }
    }
}
