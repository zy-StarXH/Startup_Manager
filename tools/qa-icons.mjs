// Read-only CDP checks against a running native WebView2 host.
// Launch with WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9233.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';

const output = path.resolve(process.argv[2] || '_qa/icons-1.1.2');
fs.mkdirSync(output, { recursive: true });
const targets = await (await fetch('http://127.0.0.1:9233/json/list')).json();
const page = targets.find(p => p.type === 'page' && p.url.includes('app.local'));
assert.ok(page, 'native WebView2 page exists');
const ws = new WebSocket(page.webSocketDebuggerUrl);
await new Promise((resolve, reject) => { ws.onopen = resolve; ws.onerror = reject; });
let id = 0;
const pending = new Map(), errors = [];
ws.onmessage = e => {
  const message = JSON.parse(e.data);
  const request = pending.get(message.id);
  if (request) {
    pending.delete(message.id);
    message.error ? request.reject(message.error) : request.resolve(message.result);
  }
  if (message.method === 'Runtime.exceptionThrown') errors.push(message.params.exceptionDetails.text);
  if (message.method === 'Runtime.consoleAPICalled' && message.params.type === 'error')
    errors.push(message.params.args.map(arg => arg.value || arg.description).join(' '));
};
const send = (method, params = {}) => new Promise((resolve, reject) => {
  const next = ++id;
  pending.set(next, { resolve, reject });
  ws.send(JSON.stringify({ id: next, method, params }));
});
const evaluate = async expression => {
  const result = await send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  assert.equal(result.exceptionDetails, undefined);
  return result.result.value;
};
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

// Runs in WebView2, sampling the actual PNG pixels, not a CSS simulation.
const sampleIcons = async () => {
  const rows = Array.from(document.querySelectorAll('#startupList .row[data-id]'));
  const icons = [];
  for (const row of rows) {
    const img = row.querySelector('.app-ico.has-img img');
    if (!img) continue;
    await img.decode();
    const canvas = document.createElement('canvas');
    canvas.width = img.naturalWidth; canvas.height = img.naturalHeight;
    const context = canvas.getContext('2d');
    context.drawImage(img, 0, 0);
    const pixels = context.getImageData(0, 0, canvas.width, canvas.height).data;
    let transparent = 0, semitransparent = 0, opaqueBlack = 0;
    for (let i = 0; i < pixels.length; i += 4) {
      if (pixels[i + 3] === 0) transparent++;
      else if (pixels[i + 3] < 255) semitransparent++;
      if (pixels[i + 3] === 255 && pixels[i] === 0 && pixels[i + 1] === 0 && pixels[i + 2] === 0) opaqueBlack++;
    }
    const style = getComputedStyle(img.parentElement), imageStyle = getComputedStyle(img);
    icons.push({ id: row.dataset.id, name: row.querySelector('.app-name').textContent.trim(),
      width: canvas.width, height: canvas.height, transparent, semitransparent, opaqueBlack,
      shadow: style.boxShadow, border: style.borderWidth, background: style.backgroundImage,
      fit: imageStyle.objectFit, imageRadius: imageStyle.borderRadius, src: img.src });
  }
  return { rows: rows.length, fallback: rows.length - icons.length, icons };
};

try {
  await send('Runtime.enable');
  await send('Page.enable');
  for (let attempt = 0; attempt < 160; attempt++) {
    if (await evaluate('window.StartupManagerUi && !StartupManagerUi.getState().loading && StartupManagerUi.getState().items > 0')) break;
    await pause(250);
  }
  assert.equal(await evaluate('StartupManagerUi.getState().mode'), 'native');
  const version = await evaluate('document.querySelector(".sidebar-version").textContent.trim()');
  assert.equal(version, '版本 1.1.2');
  const report = { version, categories: {}, errors };
  const gallery = [];
  for (const category of ['startup', 'service', 'task', 'packaged']) {
    const exists = await evaluate(`!!document.querySelector('.category-tab[data-category="${category}"]')`);
    if (!exists) continue;
    await evaluate(`document.querySelector('.category-tab[data-category="${category}"]').click()`);
    await pause(300);
    const sampled = await evaluate(`(${sampleIcons.toString()})()`);
    for (const icon of sampled.icons) {
      assert.equal(icon.shadow, 'none', icon.name + ' has no added shadow');
      assert.equal(icon.border, '0px', icon.name + ' has no added border');
      assert.equal(icon.background, 'none', icon.name + ' has no tile background');
      assert.equal(icon.fit, 'contain');
      assert.equal(icon.imageRadius, '0px');
    }
    assert.ok(sampled.icons.length > 0, category + ' contains real icons');
    const transparent = sampled.icons.filter(icon => icon.transparent > 0);
    assert.ok(transparent.length > 0, category + ' retains native transparent pixels');
    if (category === 'startup') {
      gallery.push(...sampled.icons.slice(0, 12));
      await evaluate(`StartupManagerUi.openDetail(${JSON.stringify(transparent[0].id)})`);
      assert.equal(await evaluate('getComputedStyle(document.querySelector("#detailContent .app-ico.has-img")).boxShadow'), 'none');
      assert.equal(await evaluate('getComputedStyle(document.querySelector("#detailContent .app-ico.has-img img")).borderRadius'), '0px');
      await pause(300);
      const shot = await send('Page.captureScreenshot', { format: 'png' });
      fs.writeFileSync(path.join(output, 'startup-detail.png'), Buffer.from(shot.data, 'base64'));
    }
    report.categories[category] = { rows: sampled.rows, realIcons: sampled.icons.length,
      transparentIcons: transparent.length, fallback: sampled.fallback,
      examples: sampled.icons.slice(0, 8).map(({ src, ...icon }) => icon) };
  }
  // A temporary preview shows actual extracted PNGs enlarged on a white background.
  await evaluate(`(() => {
    const panel = document.createElement('div'); panel.id = 'icon-qa-gallery';
    panel.style.cssText = 'position:fixed;inset:0;z-index:99999;background:#fff;padding:48px;display:grid;grid-template-columns:repeat(4,1fr);gap:24px;font:18px Segoe UI;overflow:auto';
    const icons = ${JSON.stringify(gallery.map(({ name, src }) => ({ name, src })))};
    for (const icon of icons) {
      const cell = document.createElement('div'); cell.style.cssText = 'display:flex;align-items:center;gap:20px;color:#222';
      const image = document.createElement('img'); image.src = icon.src; image.style.cssText = 'width:96px;height:96px;object-fit:contain';
      const label = document.createElement('span'); label.textContent = icon.name;
      cell.append(image,label); panel.append(cell);
    }
    document.body.append(panel);
  })()`);
  await pause(300);
  const shot = await send('Page.captureScreenshot', { format: 'png' });
  fs.writeFileSync(path.join(output, 'original-icons.png'), Buffer.from(shot.data, 'base64'));
  await evaluate('document.querySelector("#icon-qa-gallery").remove()');
  assert.deepEqual(errors, []);
  fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
  console.log('ICON ALPHA AND NATIVE UI CHECKS PASSED');
} finally { ws.close(); }
