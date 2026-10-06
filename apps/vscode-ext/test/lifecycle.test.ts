import { test } from 'node:test';
import assert from 'node:assert/strict';
import { OrderedLifecycle } from '../src/lifecycle';
test('queued root/config changes coalesce to the latest ordered startup', async () => {
    const events: string[] = [];
    let first: (() => boolean) | undefined;
    const lifecycle = new OrderedLifecycle(async () => { events.push('stop'); }, async current => { events.push('start'); first = current; }, error => { throw error; });
    await Promise.all([lifecycle.restart(), lifecycle.restart(), lifecycle.restart()]);
    assert.deepEqual(events, ['stop', 'stop', 'stop', 'start']); assert.equal(first?.(), true);
    const previous = first!;
    await lifecycle.restart(); assert.equal(previous(), false);
    await lifecycle.dispose(); assert.equal(first?.(), false);
    const length = events.length; await lifecycle.restart(); assert.equal(events.length, length);
});
test('a root change invalidates an in-flight startup before its result can be used', async () => {
    let release: (() => void) | undefined;
    let ready: (() => void) | undefined;
    const entered = new Promise<void>(resolve => { ready = resolve; });
    const pause = new Promise<void>(resolve => { release = resolve; });
    const seen: boolean[] = []; let starts = 0;
    const lifecycle = new OrderedLifecycle(async () => {}, async current => {
        if (++starts === 1) { ready?.(); await pause; }
        seen.push(current());
    }, error => { throw error; });
    const old = lifecycle.restart(); await entered;
    const next = lifecycle.restart(); release?.(); await Promise.all([old, next]);
    assert.deepEqual(seen, [false, true]); await lifecycle.dispose();
});
