// 固定した上流ソースと、宣言・呼出の 2 箇所だけの修正を検証する。
import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = dirname(fileURLToPath(import.meta.url));
const manifest = JSON.parse(readFileSync(join(root, 'source-manifest.json'), 'utf8'));
if (manifest.files.length !== 67) throw new Error('Expected 67 upstream files');
for (const file of manifest.files) {
  let data = readFileSync(join(root, 'upstream', file.path.slice(4)));
  // リポジトリ規約で除いた末尾 1 space だけ復元し、上流の原本 SHA と照合する。
  if (file.path === 'src/Uno.UI.Runtime.Skia.Win32/Hosting/Win32Host.cs') {
    const line = "\t/// There's only one running instance of the event loop, even in the";
    const source = data.toString('utf8');
    if (source.split(line + '\n').length !== 2) throw new Error('Unexpected whitespace normalization');
    data = Buffer.from(source.replace(line + '\n', line + ' \n'));
  }
  // 末尾の空行を除いた 2 ファイルだけ、原本の改行を復元する。
  if (file.path === 'src/Uno.UI.Runtime.Skia.Win32/ApplicationMode/DataTransfer/DragDrop/Win32DragDropExtension.cs'
      || file.path === 'src/Uno.UI.Runtime.Skia.Win32/ApplicationMode/DataTransfer/Win32ClipboardExtension.cs') {
    const source = data.toString('utf8');
    if (!source.endsWith('}\n') || source.endsWith('\n\n')) throw new Error('Unexpected EOF normalization');
    data = Buffer.from(source + '\n');
  }
  const sha = createHash('sha1').update(`blob ${data.length}\0`).update(data).digest('hex');
  if (sha !== file.gitBlobSha) throw new Error(`Upstream source differs: ${file.path}`);
}
const notice = '// AnomalyStudio 2026-10-11: UIA 構造変更イベントの ABI を配列と長さの 4 引数へ修正。詳細は ../../NOTICE.md。\n';
const changes = [
  ['Win32UIAutomationInterop.cs',
    '[MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)] int[]? runtimeId);',
    '[MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[]? runtimeId,\n\t\tint runtimeIdLength);'],
  ['Win32Accessibility.cs',
    'Win32UIAutomationInterop.UiaRaiseStructureChangedEvent(provider, type, runtimeId);',
    'Win32UIAutomationInterop.UiaRaiseStructureChangedEvent(provider, type, runtimeId, runtimeId?.Length ?? 0);'],
];
for (const [name, before, after] of changes) {
  const original = readFileSync(join(root, 'upstream/Uno.UI.Runtime.Skia.Win32/Accessibility', name), 'utf8');
  const patched = readFileSync(join(root, 'patched/Accessibility', name), 'utf8');
  if (original.split(before).length !== 2 || patched !== notice + original.replace(before, after)) {
    throw new Error(`Unexpected patch: ${name}`);
  }
}
console.log('PASS: 67 official Git blobs (three documented whitespace normalizations); exactly two ABI changes');
