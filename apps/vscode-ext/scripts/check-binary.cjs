const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const target = process.env.PENGUIN_PACKAGE_TARGET || `${process.platform}-${process.arch}`;
if (!/^(win32|linux|darwin)-(x64|arm64)$/.test(target)) {
    throw new Error('PENGUIN_PACKAGE_TARGET must be win32/linux/darwin-x64/arm64.');
}
const file = path.join(root, 'bin', target, target.startsWith('win32-') ? 'penguin-lsp.exe' : 'penguin-lsp');
if (!fs.existsSync(file) || !fs.statSync(file).isFile() || !fs.statSync(file).size) {
    throw new Error(`Backend not bundled: ${file}. Supply the protocol-v1 release executable before packaging. This check never downloads or builds it.`);
}
if (!target.startsWith('win32-')) { fs.accessSync(file, fs.constants.X_OK); }
fs.mkdirSync(path.join(root, 'dist'), { recursive: true });
console.log(`Bundled backend verified: ${file}`);
