// Generates themes/penguin-{dark,light}-color-theme.json and prints the package.json "colors" contribution from one
// palette — the same Penguin Dark/Light table as the Visual Studio extension (SYNTAX_COLOR_PLAN.md).
// Run: node scripts/gen-themes.cjs   (commit the generated files)
'use strict';
const fs = require('node:fs');
const path = require('node:path');

// [dark, light, fontStyle?]
const p = {
    keyword: ['#569CD6', '#0000FF'], control: ['#D8A0DF', '#8F08C4'], preprocessor: ['#9B9B9B', '#808080'],
    string: ['#CE9178', '#A31515'], escape: ['#FFD68F', '#EE0000'], number: ['#B5CEA8', '#098658'],
    comment: ['#6A9955', '#008000', 'italic'], operator: ['#D4D4D4', '#000000'], namespace: ['#C8C8C8', '#333333'],
    class: ['#4EC9B0', '#267F99'], struct: ['#86C691', '#2E7D32'], enum: ['#EE9D5B', '#B35A00'],
    template: ['#4EC9B0', '#267F99'], delegate: ['#D16D9E', '#A4286A'], function: ['#DCDCAA', '#795E26'],
    property: ['#9CDCFE', '#001080'], local: ['#D4D4D4', '#1F1F1F'], parameter: ['#9A9A9A', '#808080'],
    global: ['#9CDCFE', '#001080'], enumValue: ['#B8D7A3', '#4F7A28'], label: ['#C8C8C8', '#000000'],
    ueMacro: ['#C586C0', '#AF00DB', 'bold'], macro: ['#BEB7FF', '#8A1BFF'], specifier: ['#569CD6', '#0451A5'],
    metaKey: ['#D7BA7D', '#8B6914'], text: ['#D4D4D4', '#000000']
};

// TextMate scopes (C/C++ grammar) per palette entry.
const scopes = {
    keyword: ['keyword', 'storage.type', 'storage.modifier', 'keyword.operator.new', 'keyword.operator.delete', 'constant.language', 'variable.language.this'],
    control: ['keyword.control', 'keyword.other.using', 'keyword.other.operator'],
    preprocessor: ['keyword.control.directive', 'meta.preprocessor', 'punctuation.definition.directive'],
    string: ['string', 'meta.preprocessor.include string', 'string.quoted.other.lt-gt.include'],
    escape: ['constant.character.escape'], number: ['constant.numeric'], comment: ['comment', 'punctuation.definition.comment'],
    operator: ['keyword.operator'], namespace: ['entity.name.namespace', 'entity.name.scope-resolution'],
    class: ['entity.name.type', 'entity.name.class', 'support.class', 'entity.other.inherited-class'],
    struct: ['entity.name.type.struct'], enum: ['entity.name.type.enum'], function: ['entity.name.function', 'support.function'],
    property: ['variable.other.property', 'variable.other.object.property', 'variable.other.member'],
    parameter: ['variable.parameter'], local: ['variable', 'variable.other'], enumValue: ['variable.other.enummember'],
    macro: ['entity.name.function.preprocessor', 'entity.name.other.preprocessor.macro'], label: ['entity.name.label']
};

// Standard semantic token types/modifiers (cpptools + clangd) per palette entry.
const semantic = {
    class: ['class', 'type', 'typeParameter', 'concept'], struct: ['struct'], enum: ['enum'], template: ['templateType'],
    function: ['function', 'method', 'templateFunction', 'operatorOverload', 'memberOperatorOverload'],
    property: ['property', 'field', 'property.static'], local: ['variable', 'variable.local'], parameter: ['parameter'],
    global: ['variable.global', 'variable.static'], enumValue: ['enumMember'], namespace: ['namespace'],
    macro: ['macro'], label: ['label'], number: ['number', 'numberLiteral'], string: ['string', 'stringLiteral'],
    comment: ['comment'], keyword: ['keyword'], operator: ['operator'], delegate: ['event']
};

// Theme colors used by the Unreal coloring decorations (src/unrealColoring.ts).
const penguinColors = {
    'penguin.ueMacro': ['ueMacro', 'UE macros (UPROPERTY, UFUNCTION, UE_LOG …)'],
    'penguin.specifier': ['specifier', 'Reflection specifiers (EditAnywhere, BlueprintCallable …)'],
    'penguin.metaKey': ['metaKey', 'Keys inside meta=( … )'],
    'penguin.class': ['class', 'Indexed Unreal classes'], 'penguin.struct': ['struct', 'Indexed Unreal structs'],
    'penguin.enum': ['enum', 'Indexed Unreal enums'], 'penguin.enumValue': ['enumValue', 'Indexed Unreal enum values'],
    'penguin.delegate': ['delegate', 'Indexed Unreal delegates'], 'penguin.function': ['function', 'Indexed Unreal functions'],
    'penguin.property': ['property', 'Indexed Unreal properties (after -> or .)']
};

function theme(name, dark) {
    const pick = key => p[key][dark ? 0 : 1];
    const style = key => p[key][2];
    const tokenColors = Object.entries(scopes).map(([key, scope]) => ({
        name: key, scope, settings: Object.assign({ foreground: pick(key) }, style(key) ? { fontStyle: style(key) } : {})
    }));
    const semanticTokenColors = {};
    for (const [key, types] of Object.entries(semantic)) {
        for (const type of types) {
            semanticTokenColors[type] = style(key) ? { foreground: pick(key), fontStyle: style(key) } : pick(key);
        }
    }
    const colors = {
        'editor.background': dark ? '#1E1E1E' : '#FFFFFF', 'editor.foreground': pick('text')
    };
    for (const [id, [key]] of Object.entries(penguinColors)) { colors[id] = pick(key); }
    return { $schema: 'vscode://schemas/color-theme', name, type: dark ? 'dark' : 'light', semanticHighlighting: true, colors, tokenColors, semanticTokenColors };
}

const dir = path.join(__dirname, '..', 'themes');
fs.mkdirSync(dir, { recursive: true });
fs.writeFileSync(path.join(dir, 'penguin-dark-color-theme.json'), JSON.stringify(theme('Penguin Dark', true), null, 2) + '\n');
fs.writeFileSync(path.join(dir, 'penguin-light-color-theme.json'), JSON.stringify(theme('Penguin Light', false), null, 2) + '\n');
const contribution = Object.entries(penguinColors).map(([id, [key, description]]) => ({
    id, description, defaults: { dark: p[key][0], light: p[key][1], highContrast: p[key][0], highContrastLight: p[key][1] }
}));
console.log(JSON.stringify(contribution, null, 2));
