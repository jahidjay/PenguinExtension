import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { ChangeBatcher, coalesce, FileChange, insideRoots, isWatchable } from '../src/watcher';
test('watch filtering excludes generated and build/cache paths across separators and case', () => {
    for (const file of ['/p/Source/A.h', '/p/Source/A.hpp', '/p/Source/A.cpp', '/p/Game.uproject']) { assert.equal(isWatchable(file), true); }
    for (const file of ['/p/Intermediate/A.h', '/p/Binaries/A.h', '/p/Saved/A.h', '/p/.git/A.h', '/p/node_modules/A.h', '/p/DerivedDataCache/A.h', '/p/A.generated.h', '/p/A.GENERATED.H', '/p/A.gen.cpp', '/p/note.md']) { assert.equal(isWatchable(file), false, file); }
    assert.equal(isWatchable(['C:', 'p', 'Intermediate', 'A.h'].join(String.fromCharCode(92))), false);
});
test('root containment does not confuse sibling prefixes', () => {
    const root = path.resolve('workspace');
    assert.equal(insideRoots(path.join(root, 'a.h'), [root]), true);
    assert.equal(insideRoots(root + '-other/a.h', [root]), false);
});
test('create-change-delete and atomic saves coalesce correctly', () => {
    assert.equal(coalesce(1, 2), 1); assert.equal(coalesce(1, 3), undefined);
    assert.equal(coalesce(3, 1), 2); assert.equal(coalesce(2, 3), 3);
});
test('notifications batch once per file, split large events and dispose queued work', async () => {
    const sent: FileChange[][] = [];
    const batch = new ChangeBatcher(async changes => { sent.push(changes); }, error => { throw error; });
    batch.add('file:///a.h', 1); batch.add('file:///a.h', 2);
    batch.add('file:///transient.h', 1); batch.add('file:///transient.h', 3);
    batch.flush(); await batch.idle();
    assert.deepEqual(sent, [[{ uri: 'file:///a.h', type: 1 }]]);
    for (let i = 0; i < 300; i++) { batch.add(`file:///${i}.h`, 2); }
    batch.flush(); await batch.idle(); assert.deepEqual(sent.slice(1).map(list => list.length), [128, 128, 44]);
    batch.add('file:///late.h', 2); batch.dispose(); batch.flush(); await batch.idle(); assert.equal(sent.length, 4);
});
test('debounce timer coalesces bursts and reports notification failure', async () => {
    let calls = 0; let errors = 0;
    const batch = new ChangeBatcher(async () => { calls++; throw new Error('write'); }, () => errors++, 5, 20);
    batch.add('file:///a.h', 2); batch.add('file:///a.h', 2);
    await new Promise(resolve => setTimeout(resolve, 30)); await batch.idle(); batch.dispose();
    assert.equal(calls, 1); assert.equal(errors, 1);
});
