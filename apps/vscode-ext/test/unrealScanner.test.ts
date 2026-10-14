import { test } from 'node:test';
import assert from 'node:assert/strict';
import { buildNameMap, scanDocument, ColorKind } from '../src/unrealScanner';

function colored(lines: string[], pairs: Array<[string, string]> = []): string {
    const out: string[] = [];
    scanDocument(lines, buildNameMap(pairs), (line, start, length, kind: ColorKind) =>
        out.push(`${kind}:${lines[line]!.slice(start, start + length)}`));
    return out.join(' ');
}

test('specifiers and meta keys inside UPROPERTY, comments ignored', () => {
    assert.equal(colored(['UPROPERTY(EditAnywhere, Category="Stats", meta=(ClampMin="0")) float Health; // Hidden']),
        'macro:UPROPERTY specifier:EditAnywhere specifier:Category specifier:meta metaKey:ClampMin');
});

test('multi-line macro arguments carry state across lines', () => {
    assert.equal(colored(['UFUNCTION(BlueprintCallable,', '    meta = (DisplayName = "Fire", Keywords = "x"))', 'void Fire();']),
        'macro:UFUNCTION specifier:BlueprintCallable specifier:meta metaKey:DisplayName metaKey:Keywords');
});

test('indexed names use the highest-priority kind; properties only after member access', () => {
    const pairs: Array<[string, string]> = [['AActor', 'class'], ['FHit', 'struct'], ['EMode', 'enum'], ['Health', 'property'],
        ['Fire', 'function'], ['Fire', 'property'], ['FOnHit', 'delegate']];
    assert.equal(colored(['AActor* A; FHit H; EMode M; Health = 1; A->Health = 2; Fire(); FOnHit D;'], pairs),
        'class:AActor struct:FHit enum:EMode property:Health function:Fire delegate:FOnHit');
});

test('block comments and strings are skipped across lines', () => {
    assert.equal(colored(['/* UPROPERTY(EditAnywhere)', 'AActor */ AActor x; "AActor" UE_LOG'], [['AActor', 'class']]),
        'class:AActor macro:UE_LOG');
});

test('DECLARE_* macros are macros', () => {
    assert.equal(colored(['DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FOnDied, AActor*, Who);'], [['AActor', 'class']]),
        'macro:DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam class:AActor');
});
