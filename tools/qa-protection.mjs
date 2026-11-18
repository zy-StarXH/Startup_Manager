// Native mode cancels the confirmation; demo mode exercises all mutations in memory.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

const native = process.argv.includes('--native');
const port = native ? 9233 : 9234;
const output = path.resolve('_qa/protection-1.3.0');
fs.mkdirSync(output, { recursive: true });
const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
const target = targets.find(page => page.type === 'page' && /index.html/.test(page.url));
assert.ok(target);
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
let id = 0, checks = 0;
const pending = new Map(), errors = [];
ws.onmessage = event => {
  const message = JSON.parse(event.data);
  if (pending.has(message.id)) {
    const callback = pending.get(message.id); pending.delete(message.id);
    message.error ? callback.reject(message.error) : callback.resolve(message.result);
  }
  if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.text);
};
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const request = ++id; pending.set(request, { resolve, reject });
  ws.send(JSON.stringify({ id: request, method, params }));
});
const evaluate = async expression => {
  const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined);
  return result.result.value;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
const wait = async expression => {
  for (let attempt = 0; attempt < 120; attempt++) {
    if (await evaluate(expression)) { checks++; return; }
    await pause(150);
  }
  throw new Error('Timed out: ' + expression);
};
const click = selector => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
try {
  await send('Runtime.enable');
  await wait('window.StartupManagerUi && !StartupManagerUi.getState().loading && StartupManagerUi.getState().items > 0');
  assert.equal(await evaluate('StartupBridge.mode'), native ? 'native' : 'demo'); checks++;
  assert.equal(await evaluate('document.querySelector(".sidebar-version").textContent.trim()'), '版本 1.3.0'); checks++;
  await click('.category-tab[data-category="task"]');
  const itemId = await evaluate(native
    ? `document.querySelector('#startupList .row:has(.lock-chip.overridable)').dataset.id`
    : 'StartupDemo.itemIds.edge');
  await evaluate(`StartupManagerUi.openDetail(${JSON.stringify(itemId)})`);
  await wait('document.querySelector("#detailContent [data-action=allow-unverified]")');
  assert.equal(await evaluate('document.querySelector("#detailContent [role=switch]").disabled'), true); checks++;
  await click('#detailContent [data-action="allow-unverified"]');
  await wait('document.querySelector("#dialogBackdrop").classList.contains("open")');
  assert.match(await evaluate('document.querySelector("#dialogNote").textContent'), /不会改变启动状态/); checks++;
  assert.match(await evaluate('document.querySelector("#dialogConfirm").textContent'), /我了解风险/); checks++;
  await click('#dialogCancel');
  assert.equal(await evaluate('document.querySelector("#detailContent [role=switch]").disabled'), true); checks++;
  if (!native) {
    await click('#detailContent [data-action="allow-unverified"]');
    await click('#dialogConfirm');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#detailContent [data-action=relock-item]")');
    assert.equal(await evaluate('document.querySelector("#detailContent [role=switch]").disabled'), false); checks++;
    assert.equal(await evaluate('document.querySelector("#detailContent [role=switch]").getAttribute("aria-checked")'), 'true'); checks++;
    await click('#detailContent [role="switch"]');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#detailContent [role=switch]").getAttribute("aria-checked") === "false"');
    assert.ok(await evaluate('StartupManagerUi.getState().backups > 0')); checks++;
    await click('#detailContent [data-action="relock-item"]');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#detailContent [role=switch]").disabled');
    assert.equal(await evaluate('document.querySelector("#detailContent [role=switch]").getAttribute("aria-checked")'), 'false'); checks++;
    await click('#detailContent [data-action="allow-unverified"]');
    await click('#dialogConfirm');
    await wait('StartupManagerUi.getState().pendingOps === 0 && !document.querySelector("#detailContent [role=switch]").disabled');
    await click('#detailContent [data-action="restore-detail"]');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#detailContent [role=switch]").getAttribute("aria-checked") === "true"');
    await click('#detailContent [data-action="relock-item"]');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelector("#detailContent [role=switch]").disabled');
  }
  await click('#detailContent [data-action="allow-unverified"]');
  await pause(250);
  const screenshot = await send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(path.join(output, native ? 'native-confirm.png' : 'demo-confirm.png'), Buffer.from(screenshot.data, 'base64'));
  await click('#dialogCancel');
  if (!native) {
    // Bulk allow/relock of protected items (v1.3.0).
    await click('.category-tab[data-category="service"]');
    await wait('StartupManagerUi.getState().pendingOps === 0 && !document.querySelector("#bulkUnlock").disabled');
    assert.equal(await evaluate('document.querySelector("#bulkRelock").disabled'), true); checks++;
    const lockedRows = await evaluate('document.querySelectorAll("#startupList .lock-chip").length');
    assert.ok(lockedRows >= 2); checks++;

    await click('#bulkUnlock');
    await wait('document.querySelector("#dialogBackdrop").classList.contains("open")');
    assert.match(await evaluate('document.querySelector("#dialogTitle").textContent'), /允许管理这些受保护项/); checks++;
    assert.match(await evaluate('document.querySelector("#dialogNote").textContent'), /不会改变任何启动状态/); checks++;
    assert.match(await evaluate('document.querySelector("#dialogConfirm").textContent'), new RegExp('允许管理 ' + lockedRows + ' 项')); checks++;
    await click('#dialogConfirm');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelectorAll("#startupList .lock-chip").length === 0');
    assert.equal(await evaluate('document.querySelectorAll("#startupList .user-chip").length'), lockedRows); checks++;
    assert.equal(await evaluate('document.querySelector("#bulkRelock").disabled'), false); checks++;
    assert.equal(await evaluate('document.querySelector("#bulkUnlock").disabled'), true); checks++;
    assert.equal(await evaluate('document.querySelectorAll("#startupList .row .switch[disabled]").length'), 0); checks++;

    await click('#bulkRelock');
    await wait('document.querySelector("#dialogBackdrop").classList.contains("open")');
    assert.match(await evaluate('document.querySelector("#dialogConfirm").textContent'), new RegExp('恢复保护 ' + lockedRows + ' 项')); checks++;
    await click('#dialogConfirm');
    await wait('StartupManagerUi.getState().pendingOps === 0 && document.querySelectorAll("#startupList .lock-chip").length === ' + lockedRows);
    assert.equal(await evaluate('document.querySelectorAll("#startupList .user-chip").length'), 0); checks++;
    assert.equal(await evaluate('document.querySelector("#bulkUnlock").disabled'), false); checks++;
    const bulkShot = await send('Page.captureScreenshot', { format: 'png' });
    fs.writeFileSync(path.join(output, 'demo-bulk-protection.png'), Buffer.from(bulkShot.data, 'base64'));
  }
  assert.deepEqual(errors, []); checks++;
  const report = { mode: native ? 'native' : 'demo', itemId, checks, errors };
  fs.writeFileSync(path.join(output, (native ? 'native' : 'demo') + '.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report));
} finally { ws.close(); }
