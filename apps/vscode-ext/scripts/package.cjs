// Packages a platform-specific .vsix (dist/penguin-vscode-<target>.vsix, plus dist/penguin-vscode.vsix for the host)
// for the staged penguin-lsp in bin/<target>/. Target: PENGUIN_PACKAGE_TARGET or the host platform-arch.
'use strict';
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const target = process.env.PENGUIN_PACKAGE_TARGET || `${process.platform}-${process.arch}`;
require('./check-binary.cjs');
const others = fs.readdirSync(path.join(root, 'bin')).filter(dir => dir !== target);
if (others.length) {
    throw new Error(`Only bin/${target} may be staged when packaging ${target}; remove: ${others.map(d => 'bin/' + d).join(', ')}`);
}
const out = path.join(root, 'dist', `penguin-vscode-${target}.vsix`);
const vsce = path.join(root, 'node_modules', '@vscode', 'vsce', 'vsce');
execFileSync(process.execPath, [vsce, 'package', '--target', target, '--allow-missing-repository', '--skip-license', '--out', out],
    { cwd: root, stdio: 'inherit' });
if (target === `${process.platform}-${process.arch}`) {
    fs.copyFileSync(out, path.join(root, 'dist', 'penguin-vscode.vsix'));
}
console.log(`Packaged ${out}`);
