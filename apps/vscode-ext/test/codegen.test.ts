import { test } from 'node:test';
import assert from 'node:assert/strict';
import { accessors, declarationAt, enclosingClass, implementation, sourceCandidates, stripDefaults, ufunctionAbove } from '../src/codegen';

const header = [
    'UCLASS()',
    'class MYGAME_API AMyCharacter : public ACharacter',
    '{',
    '    GENERATED_BODY()',
    'public:',
    '    UFUNCTION(BlueprintCallable)',
    '    virtual float TakeHit(float Damage, const FString& Reason = TEXT("x"),',
    '        AActor* Instigator = nullptr) const override;',
    '    UFUNCTION(Server, Reliable, WithValidation)',
    '    void ServerFire(FVector Dir);',
    '    UPROPERTY(EditAnywhere)',
    '    float Health = 100.f;',
    '    UPROPERTY()',
    '    FString DisplayName;',
    '    bool bIsDead;',
    '};'
];

test('finds the multi-line declaration and its class', () => {
    const d = declarationAt(header, 7)!;
    assert.equal(d.startLine, 6);
    assert.equal(d.endLine, 7);
    assert.equal(enclosingClass(header, 7), 'AMyCharacter');
});

test('implementation strips virtual/override/defaults and keeps const', () => {
    const d = declarationAt(header, 6)!;
    assert.deepEqual(implementation(d.text, 'AMyCharacter', ufunctionAbove(header, d.startLine)),
        ['float AMyCharacter::TakeHit(float Damage, const FString& Reason, AActor* Instigator) const\n{\n\treturn {};\n}\n']);
});

test('server RPC with validation gets _Implementation and _Validate', () => {
    const d = declarationAt(header, 9)!;
    assert.deepEqual(implementation(d.text, 'AMyCharacter', ufunctionAbove(header, d.startLine)), [
        'void AMyCharacter::ServerFire_Implementation(FVector Dir)\n{\n}\n',
        'bool AMyCharacter::ServerFire_Validate(FVector Dir)\n{\n\treturn true;\n}\n'
    ]);
});

test('non-function lines are rejected', () => {
    assert.equal(typeof implementation('float Health = 100.f;', 'A'), 'string');
});

test('getter/setter: primitives by value, others by const ref, bool drops the b prefix', () => {
    assert.deepEqual(accessors('    float Health = 100.f;', '    '),
        { name: 'Health', text: '    float GetHealth() const { return Health; }\n    void SetHealth(float NewHealth) { Health = NewHealth; }' });
    assert.deepEqual(accessors('    FString DisplayName;', '\t'),
        { name: 'DisplayName', text: '\tconst FString& GetDisplayName() const { return DisplayName; }\n\tvoid SetDisplayName(const FString& NewDisplayName) { DisplayName = NewDisplayName; }' });
    assert.equal((accessors('    bool bIsDead;', '') as { name: string }).name, 'IsDead');
    assert.equal(typeof accessors('    void Fire();', ''), 'string');
});

test('default arguments with nested commas are removed', () => {
    assert.equal(stripDefaults('int A = 1, TArray<int, X> B = {}, const FString& C = TEXT("a,b")'), 'int A, TArray<int, X> B, const FString& C');
});

test('source candidates mirror Public/Private', () => {
    assert.deepEqual(sourceCandidates('C:/G/Source/M/Public/Foo.h', '/'),
        ['C:/G/Source/M/Public/Foo.cpp', 'C:/G/Source/M/Private/Foo.cpp', 'C:/G/Source/M/Public/Private/Foo.cpp']);
});
