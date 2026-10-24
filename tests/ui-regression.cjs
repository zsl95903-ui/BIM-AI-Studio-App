const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const path = require('path');
const elements = new Map();
const sent = [];
let receive;
function element() {
  return {
    value: '', innerHTML: '', style: {}, dataset: {}, listeners: {},
    classList: { add() {}, remove() {}, toggle() {} },
    addEventListener(type, callback) { this.listeners[type] = callback; },
    setAttribute() {}, focus() {}, appendChild() {}, remove() {},
    querySelector() { return element(); }, querySelectorAll() { return []; }
  };
}
const context = vm.createContext({
  console,
  document: {
    getElementById(id) { if (!elements.has(id)) elements.set(id, element()); return elements.get(id); },
    querySelectorAll() { return []; }, createElement: element
  },
  window: { chrome: { webview: {
    postMessage(message) { sent.push(message); },
    addEventListener(type, callback) { receive = callback; }
  } } }
});
vm.runInContext(fs.readFileSync(path.join(__dirname, '../host/hybrid-ui/RevitAi.Addin/UI/app.js'), 'utf8'), context);
const submit = () => elements.get('composer').listeners.submit({ preventDefault() {} });
const event = data => receive({ data });
vm.runInContext('setBusy(true)', context);
sent.length = 0;
submit();
assert.equal(sent[0]?.type, 'chat.cancel');
sent.length = 0;
elements.get('prompt').value = 'new instruction';
submit();
assert.equal(sent[0]?.type, 'chat');
sent.length = 0;
elements.get('send').listeners.click({ preventDefault() {} });
assert.equal(sent[0]?.type, 'chat.cancel');
vm.runInContext('resetChat()', context);
elements.get('prompt').value = 'first';
submit();
event({ type: 'chat.started', runId: 1 });
const first = vm.runInContext('activeAssistant', context);
elements.get('prompt').value = 'second';
submit();
const second = vm.runInContext('pendingAssistants[0]', context);
assert.equal(vm.runInContext('activeAssistant', context), first, 'new prompt stole old run placeholder');
event({ type: 'chat.cancelled', runId: 1 });
event({ type: 'chat.started', runId: 2 });
assert.equal(vm.runInContext('activeAssistant', context), second, 'new run lost its placeholder');
event({ type: 'chat.response', runId: 1, message: 'stale response' });
assert.equal(vm.runInContext('activeAssistant', context), second, 'stale response changed new run');
console.log('UI: stop button, empty stop, prompt supersession, placeholder ownership and stale-run isolation passed.');
