import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bounded, CancelledError, pollJob, TimeoutError } from '../src/jobs';
import { JobDto, StaleSessionError } from '../src/protocol';
const job: JobDto = { id: 'j', sessionId: 's', kind: 'ai', state: 'running' };
const options = () => ({ signal: new AbortController().signal, current: () => true, log: (_message: string) => {}, wait: async () => {} });
test('polling stops immediately at terminal success', async () => {
    let calls = 0;
    const api = { job: async () => { calls++; return { ...job, state: 'succeeded' as const, result: { text: 'ok', model: 'm' } }; }, cancelJob: async () => true };
    assert.equal((await pollJob(api, job, options())).state, 'succeeded'); assert.equal(calls, 1);
    await pollJob(api, { ...job, state: 'succeeded' }, options()); assert.equal(calls, 1);
});
test('polling cancellation sends cancelJob once and does not poll', async () => {
    const controller = new AbortController(); controller.abort(); let cancels = 0;
    const api = { job: async () => { throw new Error('should not poll'); }, cancelJob: async () => { cancels++; return true; } };
    await assert.rejects(pollJob(api, job, { ...options(), signal: controller.signal }), CancelledError);
    assert.equal(cancels, 1);
});
test('timeouts are bounded and cancel the addressed job', async () => {
    let time = 0; let cancels = 0; let polls = 0;
    const api = { job: async () => { polls++; return job; }, cancelJob: async () => { cancels++; return true; } };
    await assert.rejects(pollJob(api, job, { ...options(), timeoutMs: 1000, now: () => time, wait: async ms => { time += ms; } }), TimeoutError);
    assert.equal(cancels, 1); assert.equal(polls, 1);
});
test('old sessions never poll or cancel a job in the replacement session', async () => {
    const api = { job: async () => { throw new Error('old poll'); }, cancelJob: async () => { throw new Error('old cancel'); } };
    await assert.rejects(pollJob(api, job, { ...options(), current: () => false }), StaleSessionError);
});
test('failed, cancelled, expired and mismatched responses do not spin', async () => {
    const api = { job: async () => ({ ...job, sessionId: 'other' }), cancelJob: async () => true };
    await assert.rejects(pollJob(api, job, options()), StaleSessionError);
    await assert.rejects(pollJob(api, { ...job, state: 'failed', error: { message: 'offline' } }, options()), /offline/);
    await assert.rejects(pollJob(api, { ...job, state: 'cancelled' }, options()), CancelledError);
    await assert.rejects(pollJob({ ...api, job: async () => { throw new Error('expired'); } }, job, options()), /expired/);
});
test('RPC deadline and cancellation bound even an unresponsive peer', async () => {
    let cancelled = 0;
    await assert.rejects(bounded(new Promise<never>(() => {}), 5, undefined, () => cancelled++), /timed out/);
    const controller = new AbortController(); controller.abort();
    await assert.rejects(bounded(new Promise<never>(() => {}), 100, controller.signal, () => cancelled++), CancelledError);
    assert.equal(cancelled, 2);
});
