const { test } = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const Module = require('node:module');
const { JSDOM } = require('jsdom');
const bootstrap = new JSDOM('<!doctype html><html><body></body></html>');
global.window = bootstrap.window; global.document = bootstrap.window.document;
const React = require('react');
const { act } = React;
const { createRoot } = require('react-dom/client');

// Bundle the real assistant and API client. PDF rendering is stubbed here:
// these tests exercise the wizard, selection callbacks and publishing contract.
const { build } = require('esbuild');
const compiled = build({
  entryPoints: [path.join(__dirname, '../src/LayoutRuleEditor.tsx')],
  bundle: true, write: false, format: 'cjs', platform: 'node', jsx: 'automatic',
  external: ['react', 'react/jsx-runtime'],
  plugins: [{ name: 'pdf-selection', setup(builder) {
    builder.onLoad({ filter: /\/PdfPicker\.tsx$/ }, () => ({ loader: 'jsx', contents: `
      import React from 'react';
      export default function Picker(props) {
        return <button onClick={() => props.onPick(props.confirmLabel === 'Usar como rótulo' ? 'Placa do veículo' : 'Seguradora Exemplo')}>{props.confirmLabel}</button>;
      }` }));
  } }],
}).then(result => {
  const loaded = new Module(path.join(__dirname, 'assistant.cjs'), module);
  loaded.filename = path.join(__dirname, 'assistant.cjs');
  loaded.paths = Module._nodeModulePaths(__dirname);
  loaded._compile(result.outputFiles[0].text, loaded.filename);
  return loaded.exports.default;
});
const source = { id: 'source', fileName: 'exemplo.pdf', contentType: 'application/pdf', documentTypeId: 'type', ocrStatus: 'Completed', ocrText: 'Seguradora Exemplo Placa do veículo TST1A10' };
const second = { ...source, id: 'second', fileName: 'teste.png', contentType: 'image/png' };

async function setup({ files = [source, second], result = 'TST1A11', failPublish = false, failFile = false } = {}) {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' });
  for (const key of ['window', 'document', 'HTMLElement', 'HTMLDialogElement', 'Event', 'MouseEvent']) global[key] = dom.window[key];
  global.IS_REACT_ACT_ENVIRONMENT = true;
  dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  let revoked = [];
  URL.createObjectURL = () => 'blob:test'; URL.revokeObjectURL = url => revoked.push(url);
  const calls = [];
  global.fetch = async (url, options) => {
    calls.push({ url, options });
    if (String(url).endsWith('/file')) return new Response('image', { status: failFile ? 500 : 200 });
    const request = JSON.parse(options.body);
    return new Response(JSON.stringify(request.publish && failPublish ? { error: 'Modelo alterado. Refaça o teste.' } : { value: result }), {
      status: request.publish && failPublish ? 409 : 200, headers: { 'Content-Type': 'application/json' },
    });
  };
  let saved = 0;
  const root = createRoot(document.getElementById('root'));
  const Assistant = await compiled;
  const props = { document: source, documents: files, field: { key: 'placa', label: 'Placa', dataType: 'text', required: true }, version: 3, sourceUrl: 'blob:source', currentValue: 'TST1A10', onSaved: () => saved++ };
  async function render(extra = {}) { Object.assign(props, extra); await act(async () => root.render(React.createElement(Assistant, props))); }
  await render();
  const button = text => [...document.querySelectorAll('button')].find(el => el.textContent === text);
  async function click(text) { const el = button(text); assert.ok(el, `Missing button: ${text}`); await act(async () => el.click()); }
  async function fill(selector, value) {
    const el = document.querySelector(selector); assert.ok(el, selector);
    const setter = Object.getOwnPropertyDescriptor(el instanceof dom.window.HTMLSelectElement ? dom.window.HTMLSelectElement.prototype : dom.window.HTMLInputElement.prototype, 'value').set;
    await act(async () => { setter.call(el, value); el.dispatchEvent(new dom.window.Event(el.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })); });
  }
  async function mark() {
    await click('Ensinar leitura de Placa'); await click('Começar com este campo');
    await click('Marcar rótulo no PDF'); await click('Usar como rótulo');
    await click('Marcar texto de identificação'); await click('Usar para reconhecer este formato');
    await click('Testar em outro arquivo');
  }
  async function verify() { await mark(); await fill('select', 'second'); await fill('.teaching-grid input', 'TST1A11'); await click('Verificar leitura'); }
  return { button, click, fill, mark, verify, render, calls, get saved() { return saved; }, cleanup: async () => { await act(async () => root.unmount()); dom.window.close(); }, revoked };
}

test('matching reading needs explicit confirmation and publishes the tested rule', async () => {
  const ui = await setup(); try {
    await ui.verify();
    assert.equal(document.querySelector('.teaching-original').getAttribute('alt'), 'teste.png');
    assert.equal(ui.button('Confirmar para os próximos documentos').disabled, true);
    await act(async () => document.querySelector('input[type=checkbox]').click());
    await ui.click('Confirmar para os próximos documentos');
    const requests = ui.calls.filter(x => x.options.body).map(x => JSON.parse(x.options.body));
    assert.deepEqual(requests.map(x => x.publish), [false, true]);
    assert.equal(requests[1].marker, 'Seguradora Exemplo'); assert.equal(requests[1].anchor, 'Placa do veículo');
    assert.equal(requests[1].testDocumentId, 'second'); assert.equal(requests[1].version, 3);
    assert.equal(ui.saved, 1); assert.match(document.body.textContent, /Leitura ensinada/);
    assert.match(document.body.textContent, /Salvar na biblioteca/);
  } finally { await ui.cleanup(); }
});

test('incorrect extraction cannot be published', async () => {
  const ui = await setup({ result: 'ERRADO' }); try {
    await ui.verify(); assert.equal(ui.button('Confirmar para os próximos documentos').disabled, true);
    assert.equal(document.querySelector('input[type=checkbox]'), null);
    assert.equal(ui.calls.filter(x => x.options.body).length, 1);
  } finally { await ui.cleanup(); }
});

test('changing a marking invalidates the previous test', async () => {
  const ui = await setup(); try {
    await ui.verify(); await ui.click('Ajustar marcação');
    await ui.fill('input[maxlength][value="Placa do veículo"]', 'Identificação da placa');
    await ui.click('Testar em outro arquivo'); await ui.click('Verificar leitura');
    const requests = ui.calls.filter(x => x.options.body).map(x => JSON.parse(x.options.body));
    assert.equal(requests.length, 2); assert.equal(requests[1].anchor, 'Identificação da placa');
    assert.equal(ui.button('Confirmar para os próximos documentos').disabled, true);
  } finally { await ui.cleanup(); }
});

test('model version changes invalidate the tested preview', async () => {
  const ui = await setup(); try {
    await ui.verify(); await ui.render({ version: 4 });
    assert.equal(ui.button('Confirmar para os próximos documentos').disabled, true);
    assert.equal(document.querySelector('input[type=checkbox]'), null);
  } finally { await ui.cleanup(); }
});

test('missing second document explains the next step', async () => {
  const ui = await setup({ files: [source] }); try {
    await ui.mark(); assert.match(document.body.textContent, /Importe outro arquivo/);
    assert.equal(ui.button('Verificar leitura').disabled, true);
  } finally { await ui.cleanup(); }
});

test('failed publication keeps the assistant open and reports the server error', async () => {
  const ui = await setup({ failPublish: true }); try {
    await ui.verify(); await act(async () => document.querySelector('input[type=checkbox]').click());
    await ui.click('Confirmar para os próximos documentos');
    assert.equal(ui.saved, 0); assert.match(document.querySelector('[role=alert]').textContent, /Modelo alterado/);
  } finally { await ui.cleanup(); }
});

test('unavailable original blocks testing and gives an actionable message', async () => {
  const ui = await setup({ failFile: true }); try {
    await ui.mark(); await ui.fill('select', 'second'); await ui.fill('.teaching-grid input', 'TST1A11');
    assert.equal(ui.button('Verificar leitura').disabled, true);
    assert.match(document.querySelector('[role=alert]').textContent, /Não foi possível abrir o original/);
  } finally { await ui.cleanup(); }
});

test('closing the assistant does not publish or save the document', async () => {
  const ui = await setup(); try {
    await ui.mark(); await ui.click('Fechar');
    assert.equal(document.querySelector('dialog'), null);
    assert.equal(ui.calls.filter(x => x.options.body).length, 0);
    assert.equal(ui.saved, 0);
    await ui.click('Ensinar leitura de Placa');
    assert.ok(ui.button('Começar com este campo'));
  } finally { await ui.cleanup(); }
});

test('empty extraction remains blocked even after a completed test', async () => {
  const ui = await setup({ result: '' }); try {
    await ui.verify();
    assert.match(document.body.textContent, /Nenhum valor encontrado/);
    assert.equal(ui.button('Confirmar para os próximos documentos').disabled, true);
  } finally { await ui.cleanup(); }
});

test('changing test files clears the expected value and the previous result', async () => {
  const ui = await setup({ files: [source, second, { ...second, id: 'third', fileName: 'outro.png' }] }); try {
    await ui.verify(); await ui.click('Revisar teste'); await ui.fill('select', 'third');
    assert.equal(document.querySelector('.teaching-grid input').value, '');
    assert.equal(ui.button('Verificar leitura').disabled, true);
    assert.equal(ui.button('Confirmar para os próximos documentos'), undefined);
  } finally { await ui.cleanup(); }
});

test('images keep their original visible and offer manual configuration', async () => {
  const ui = await setup(); try {
    await ui.render({ document: { ...source, contentType: 'image/png', fileName: 'original.png' } });
    await ui.click('Ensinar leitura de Placa'); await ui.click('Começar com este campo');
    assert.equal(document.querySelector('.teaching-original').getAttribute('alt'), 'original.png');
    assert.equal(document.querySelector('details').open, true);
    assert.equal(ui.button('Marcar rótulo no PDF'), undefined);
  } finally { await ui.cleanup(); }
});

test('daily review hides teaching controls and preserves corrections when teaching is opened', async () => {
  const dom = new JSDOM('<div id="root"></div>', { url: 'http://localhost' });
  for (const key of ['window', 'document', 'HTMLElement', 'HTMLDialogElement', 'Event', 'MouseEvent']) global[key] = dom.window[key];
  dom.window.HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  global.IS_REACT_ACT_ENVIRONMENT = true;
  URL.createObjectURL = () => 'blob:test'; URL.revokeObjectURL = () => {};
  const doc = { ...source, values: { placa: { value: 'TST1A10', source: 'layout-anchor', confidence: 1 } }, status: 'NeedsReview', suggestions: [], createdAt: '2026-11-20T12:00:00Z' };
  const type = { id: 'type', name: 'Apólice Auto', version: 3, active: true, keywords: [], fields: [{ key: 'placa', label: 'Placa', dataType: 'text', required: true }] };
  const writes = [];
  global.fetch = async (url, options = {}) => {
    if (String(url).endsWith('/file')) return new Response('pdf');
    let data = [];
    if (String(url).endsWith('/types')) data = [type];
    if (String(url).endsWith('/documents')) data = [doc];
    if (String(url).endsWith('/confirm')) { writes.push(JSON.parse(options.body)); data = { ...doc, status: 'Indexed' }; }
    return new Response(JSON.stringify(data), { headers: { 'Content-Type': 'application/json' } });
  };
  const result = await build({ entryPoints: [path.join(__dirname, '../src/main.tsx')], bundle: true, write: false, format: 'cjs', platform: 'node', jsx: 'automatic',
    external: ['react', 'react/jsx-runtime', 'react-dom/client'], loader: { '.css': 'empty' },
    plugins: [{ name: 'pdf-selection', setup(builder) { builder.onLoad({ filter: /\/PdfPicker\.tsx$/ }, () => ({ loader: 'jsx', contents: 'export default function Picker() { return null; }' })); } }],
  });
  const loaded = new Module(path.join(__dirname, 'app.cjs'), module);
  loaded.filename = path.join(__dirname, 'app.cjs'); loaded.paths = Module._nodeModulePaths(__dirname);
  await act(async () => loaded._compile(result.outputFiles[0].text, loaded.filename));
  const button = text => [...document.querySelectorAll('button')].find(el => el.textContent === text);
  async function click(text) { assert.ok(button(text), text); await act(async () => button(text).click()); }
  await click('Conferência'); await act(async () => document.querySelector('.review-queue .item').click());
  assert.equal(button('Ensinar leitura de Placa'), undefined);
  const input = document.querySelector('.pdf-field-label input');
  await act(async () => {
    Object.getOwnPropertyDescriptor(dom.window.HTMLInputElement.prototype, 'value').set.call(input, 'TST2A20');
    input.dispatchEvent(new dom.window.Event('input', { bubbles: true }));
  });
  await click('Ensinar este modelo'); await click('Ensinar leitura de Placa'); await click('Fechar'); await click('Encerrar ensino do modelo');
  assert.equal(document.querySelector('.pdf-field-label input').value, 'TST2A20');
  assert.equal(button('Ensinar leitura de Placa'), undefined); assert.equal(writes.length, 0);
  await click(' Salvar na biblioteca'); assert.equal(writes[0].values.placa, 'TST2A20');
  dom.window.close();
});
