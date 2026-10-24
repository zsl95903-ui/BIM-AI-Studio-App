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
// History must survive opening, and untrusted model text must stay inert.
assert.match(vm.runInContext('renderMarkdown("| 标高 | 高度 |\\n| --- | --- |\\n| 一层 | 3000 |")', context), /<table>/);
assert.match(vm.runInContext('renderMarkdown("<img src=x onerror=alert(1)>")', context), /&lt;img/);
assert.doesNotMatch(vm.runInContext('renderMarkdown("[bad](javascript:alert(1))")', context), /<a/);
assert.match(vm.runInContext('renderMarkdown("[文档](https://example.com)")', context), /rel="noopener noreferrer"/);
assert.match(vm.runInContext('renderMarkdown("```html\\n<img src=x>\\n```")', context), /&lt;img/);
assert.match(vm.runInContext('renderToolPayload({levels:[{name:"<script>",elevation:3000}]})', context), /&lt;script&gt;/);
event({type:'sessions.transcript', messages:[{role:'user',content:'历史问题'},{role:'assistant',toolCalls:[{id:'historic',name:'get_all_levels',argumentsJson:'{}'}]},{role:'tool',toolCallId:'historic',content:JSON.stringify({toolName:'get_all_levels',success:true,payload:{levels:[{name:'一层'}]}})},{role:'assistant',content:'历史回答'}]});
assert.equal(vm.runInContext('toolCards.size',context),1);
assert.equal(vm.runInContext('state.busy',context),false);
assert.equal(vm.runInContext('pendingAssistants.length',context),0);
event({type:'host.status',agent:{configured:true,toolsReady:true,tools:[]}});
assert.equal(elements.get('connection-text').textContent,'已配置 · 未测试');
console.log('UI: transcript rehydration, tool tables, Markdown tables/links, HTML escaping and honest API status passed.');
