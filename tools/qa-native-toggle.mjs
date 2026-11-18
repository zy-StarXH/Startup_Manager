// Native end-to-end check for v1.3.0: creates a temporary HKCU Run value,
// toggles it through the WPF host, times click -> published state, and removes
// the value, approval and backups afterwards.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const port = 9233;
const testName = 'StartupManagerE2E';
const output = path.resolve('_qa/native-1.3.0');
fs.mkdirSync(output, { recursive: true });

const powershell = script => execFileSync(
  'powershell',
  ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', script],
  { stdio: 'pipe' });

function cleanup() {
  try {
    powershell(`
      Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name '${testName}' -ErrorAction SilentlyContinue
      Remove-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run' -Name '${testName}' -ErrorAction SilentlyContinue
      $dir = Join-Path $env:LOCALAPPDATA 'StartupManager\\Backups'
      if (Test-Path -LiteralPath $dir) {
        Get-ChildItem -LiteralPath $dir -File | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match '${testName}' } | Remove-Item -Force
      }
    `);
  } catch (error) {
    console.error('cleanup failed:', error.message);
  }
}

// A stale value from an aborted run must not be reused.
cleanup();
powershell(`Set-ItemProperty -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Run' -Name '${testName}' -Value 'cmd.exe /c exit'`);

let ws = null;
let targets = null;
try {
  for (let attempt = 0; attempt < 60; attempt++) {
    try {
      targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      if (targets.some(t => t.type === 'page' && t.url.includes('app.local'))) break;
    } catch { /* host is still starting */ }
    targets = null;
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  assert.ok(targets, 'native host is reachable');
  const target = targets.find(t => t.type === 'page' && t.url.includes('app.local'));
  assert.ok(target, 'native WebView2 page exists');

  ws = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
  let id = 0;
  const pending = new Map(), errors = [];
  ws.onmessage = event => {
    const message = JSON.parse(event.data);
    const request = pending.get(message.id);
    if (request) {
      pending.delete(message.id);
      message.error ? request.reject(message.error) : request.resolve(message.result);
    }
    if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.text);
  };
  const send = (method, params = {}) => new Promise((resolve, reject) => {
    const request = ++id; pending.set(request, { resolve, reject });
    ws.send(JSON.stringify({ id: request, method, params }));
  });
  const evaluate = async expression => {
    const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
    assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
    return result.result.value;
  };
  const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
  const wait = async (expression, label) => {
    for (let attempt = 0; attempt < 300; attempt++) {
      if (await evaluate(expression)) return;
      await pause(100);
    }
    throw new Error('Timed out: ' + (label || expression));
  };

  await send('Runtime.enable');
  await send('Page.enable');
  await wait('window.StartupManagerUi && !StartupManagerUi.getState().loading && StartupManagerUi.getState().items > 0',
    'initial scan');

  // Pick up the entry created just before this run.
  await evaluate('StartupManagerUi.refresh()');
  await wait('!StartupManagerUi.getState().loading && StartupManagerUi.getState().pendingOps === 0 && !document.querySelector("[data-action=rescan]").disabled',
    'refresh');

  await evaluate(`(() => {
    const input = document.querySelector('#searchInput');
    input.value = '${testName}';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  })()`);
  await wait('document.querySelectorAll("#startupList .row").length === 1', 'test row');
  const rowText = await evaluate('document.querySelector("#startupList .row .app-name").textContent');
  assert.equal(rowText, testName);
  const itemId = await evaluate('document.querySelector("#startupList .row").dataset.id');

  const switchState = () => evaluate('document.querySelector("#startupList .row .switch").getAttribute("aria-checked")');
  assert.equal(await switchState(), 'true');

  const disableStart = Date.now();
  await evaluate('document.querySelector("#startupList .row .switch").click()');
  await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#startupList .row .switch").getAttribute("aria-checked") === "false"',
    'disable result');
  const disableMs = Date.now() - disableStart;
  const managedTag = await evaluate('document.querySelector("#startupList .row .managed-tag") ? document.querySelector("#startupList .row .managed-tag").textContent : null');
  console.log(`disable: ${disableMs} ms; managed tag: ${managedTag}`);

  const enableStart = Date.now();
  await evaluate('document.querySelector("#startupList .row .switch").click()');
  await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#startupList .row .switch").getAttribute("aria-checked") === "true"',
    'enable result');
  const enableMs = Date.now() - enableStart;
  console.log(`enable: ${enableMs} ms`);

  const backups = await evaluate('StartupManagerUi.getState().backups');
  const screenshot = await send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(path.join(output, 'native-toggle.png'), Buffer.from(screenshot.data, 'base64'));

  assert.deepEqual(errors, []);
  assert.ok(disableMs < 3000, 'disable should not wait for full rescans: ' + disableMs);
  assert.ok(enableMs < 3000, 'enable should not wait for full rescans: ' + enableMs);

  const report = { itemId, disableMs, enableMs, backups, errors, checks: 8 };
  fs.writeFileSync(path.join(output, 'native-toggle.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
} finally {
  try { if (ws) ws.close(); } catch { /* best effort */ }
  cleanup();
}
