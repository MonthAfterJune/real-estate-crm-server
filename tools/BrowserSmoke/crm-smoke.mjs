// Dependency-free browser smoke check: Node 24 WebSocket + Chrome DevTools.
// Uses in-memory API fixtures; actual .NET/relational behavior is covered separately.
import fs from 'node:fs/promises'
import assert from 'node:assert/strict'
const origin = 'http://127.0.0.1:5184'
const targets = await (await fetch('http://127.0.0.1:9338/json/list')).json()
const socket = new WebSocket(targets.find(target => target.type === 'page').webSocketDebuggerUrl)
await new Promise((resolve, reject) => { socket.onopen = resolve; socket.onerror = reject })
let sequence = 0
const pending = new Map()
const failures = []
let onPause
socket.onmessage = event => {
  const message = JSON.parse(event.data)
  if (message.id) {
    const pair = pending.get(message.id); pending.delete(message.id)
    message.error ? pair.reject(new Error(message.error.message)) : pair.resolve(message.result)
  } else if (message.method === 'Fetch.requestPaused') void onPause(message.params).catch(error => { if (!error.message.includes('Invalid InterceptionId')) failures.push(error.message) })
  else if (message.method === 'Runtime.exceptionThrown') failures.push(message.params.exceptionDetails.text)
}
function send(method, params = {}) {
  const id = ++sequence
  return new Promise((resolve, reject) => { pending.set(id, { resolve, reject }); socket.send(JSON.stringify({ id, method, params })) })
}
async function evaluate(expression) {
  const result = await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true })
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text)
  return result.result.value
}
async function wait(expression, description) {
  const until = Date.now() + 20000
  while (Date.now() < until) {
    if (failures.length) throw new Error(failures.join('\n'))
    if (await evaluate(`Boolean(${expression})`)) return
    await new Promise(resolve => setTimeout(resolve, 150))
  }
  throw new Error('Timed out: ' + description + '\n' + await evaluate('JSON.stringify({ text: document.body.innerText, fields: Array.from(document.querySelectorAll(\'input,select\')).map(el => ({ name: el.name, value: el.value, valid: el.checkValidity(), message: el.validationMessage })) })'))
}
async function click(text) {
  await wait(`Array.from(document.querySelectorAll('button,a')).some(el => el.textContent.trim() === ${JSON.stringify(text)} && !el.disabled)`, text)
  await evaluate(`Array.from(document.querySelectorAll('button,a')).find(el => el.textContent.trim() === ${JSON.stringify(text)} && !el.disabled).click()`)
}
async function set(selector, value) {
  await evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) throw new Error('Missing field'); const proto = el.tagName === 'SELECT' ? HTMLSelectElement.prototype : el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, ${JSON.stringify(value)}); el.dispatchEvent(new Event(el.tagName === 'SELECT' ? 'change' : 'input', { bubbles: true })); })()`)
}
async function labelInput(label, value) {
  const selector = await evaluate(`(() => { const el = Array.from(document.querySelectorAll('label')).find(el => el.textContent.trim().startsWith(${JSON.stringify(label)})).querySelector('input'); el.dataset.smokeField = 'current'; return '[data-smoke-field="current"]' })()`)
  await set(selector, value)
  await evaluate(`document.querySelector('[data-smoke-field="current"]').removeAttribute('data-smoke-field')`)
}
const agent = { id: 1, fullName: 'Taylor Verification', email: 'verification@example.com', roleId: 1, role: { id: 1, name: 'agent' } }
const contacts = [
  { id: 10, firstName: 'Sam', lastName: 'Owner', type: 'Seller', email: 'sam@example.com', status: 'Active', source: 'Referral', ownerId: 1, owner: agent },
  { id: 11, firstName: 'Pat', lastName: 'Buyer', type: 'Buyer', email: null, status: 'Qualified', source: 'Website', ownerId: 1, owner: agent },
]
const properties = [], deals = [], stages = [], activities = [], notes = [], writes = []
const lookup = (rows, id) => rows.find(row => row.id === id)
onPause = async ({ requestId, request }) => {
  const route = new URL(request.url).pathname.toLowerCase()
  const body = request.postData ? JSON.parse(request.postData) : null
  let result, status = 200
  if (route === '/api/auth/me') result = { id: 1, fullName: agent.fullName, email: agent.email, role: 'agent' }
  else if (route === '/api/auth/csrf') result = { token: 'browser-fixture-csrf' }
  else if (route === '/api/users') result = [agent, { id: 2, fullName: 'Seller Login Account', email: 'account@example.com', roleId: 3, role: { id: 3, name: 'seller' } }]
  else if (request.method === 'GET') result = ({ '/api/contacts': contacts, '/api/properties': properties, '/api/opportunities': deals, '/api/pipelinestages': stages, '/api/activities': activities, '/api/notes': notes })[route]
  else {
    assert.equal(request.headers['X-CSRF-TOKEN'], 'browser-fixture-csrf')
    writes.push({ route, method: request.method, body })
    status = request.method === 'POST' ? 201 : 200
    if (route === '/api/contacts') { assert.ok(body.ownerId > 0 && body.status && body.source); result = { id: 30, ...body, owner: agent }; contacts.push(result) }
    else if (route === '/api/properties') {
      assert.equal(body.sellerContactId, 30); assert.equal(body.listingAgentId, 1); assert.equal(body.bedrooms, 2); assert.ok(!('sellerUserId' in body))
      result = { id: 30, ...body, sellerContact: lookup(contacts, body.sellerContactId), listingAgent: agent }; properties.push(result)
    } else if (route === '/api/pipelinestages') { result = { id: 20 + stages.length, ...body }; stages.push(result) }
    else if (route.startsWith('/api/opportunities')) {
      assert.equal(body.buyerContactId, 11); assert.equal(body.propertyId, 30)
      result = { id: 30, ...body, buyerContact: lookup(contacts, body.buyerContactId), property: lookup(properties, body.propertyId), agent, stage: lookup(stages, body.stageId) }
      if (request.method === 'POST') deals.push(result); else deals.splice(0, 1, result)
    } else if (route === '/api/activities') { assert.equal(body.contactId, 11); assert.equal(body.opportunityId, 30); assert.ok(!('leadId' in body)); assert.ok(body.occurredAt.endsWith('Z')); result = { id: 30, ...body }; activities.push(result) }
    else if (route === '/api/notes') { assert.equal(body.authorId, 1); assert.equal(body.entityType, 'Opportunity'); assert.equal(body.entityId, 30); result = { id: 30, ...body, author: agent }; notes.push(result) }
  }
  if (result === undefined) throw new Error('Unexpected API call ' + request.method + ' ' + route)
  await send('Fetch.fulfillRequest', { requestId, responseCode: status, responseHeaders: [{ name: 'Content-Type', value: 'application/json' }], body: Buffer.from(JSON.stringify(result)).toString('base64') })
}
await send('Runtime.enable')
await send('Page.enable')
await send('Network.enable')
await send('Network.setCacheDisabled', { cacheDisabled: true })
await send('Fetch.enable', { patterns: [{ urlPattern: '*://*/api/*', requestStage: 'Request' }] })
await send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1000, deviceScaleFactor: 1, mobile: false })
try {
  await send('Page.navigate', { url: origin + '/agent/contacts?new=Seller' })
  await wait(`document.querySelector('input[name="firstName"]') && !document.querySelector('button[type="submit"]').disabled`, 'contact form loaded')
  assert.equal(await evaluate(`document.querySelector('select[name="ownerId"]').value`), '1')
  await set('input[name="firstName"]', 'Browser'); await set('input[name="lastName"]', 'Seller')
  await set('input[name="email"]', 'browser-seller@example.com'); await set('input[name="status"]', 'Active'); await set('input[name="source"]', 'Referral')
  await click('Save contact')
  await wait(`document.body.textContent.includes('Browser Seller') && !document.querySelector('input[name="firstName"]')`, 'contact saved')
  await click('Go to listings'); await click('Add property')
  await wait(`document.querySelector('select[name="sellerContactId"] option[value="30"]')`, 'seller contact dropdown loaded')
  assert.equal(await evaluate(`Array.from(document.querySelector('select[name="sellerContactId"]').options).some(option => option.textContent.includes('Seller Login Account'))`), false)
  await labelInput('Address', '123 Verification Lane'); await labelInput('Price', '2500000'); await labelInput('Bedrooms', '2')
  await set('select[name="sellerContactId"]', '30')
  await click('Save listing'); await wait(`document.querySelector('table')?.textContent.includes('123 Verification Lane')`, 'listing saved')
  await send('Page.reload'); await wait(`document.querySelector('table')?.textContent.includes('123 Verification Lane')`, 'listing persists after reload')
  await click('Pipeline'); await wait(`document.querySelector('input[name="name"]')`, 'pipeline loaded')
  await set('input[name="name"]', 'Inquiry'); await click('Add stage'); await click('New opportunity')
  await set('select[name="buyerContactId"]', '11'); await set('select[name="propertyId"]', '30'); await set('input[name="offerAmount"]', '2400000')
  await click('Save opportunity'); await wait(`document.querySelector('textarea[name="body"]')`, 'opportunity saved and detail opened')
  await set('input[name="type"]', 'Viewing'); await set('input[name="occurredAt"]', '2026-10-11T10:30'); await click('Log activity')
  await wait(`document.body.textContent.includes('Viewing · Pat Buyer')`, 'activity logged')
  await set('textarea[name="body"]', 'Viewing requested.'); await click('Add note')
  await wait(`document.body.textContent.includes('Viewing requested.')`, 'note saved')
  await set('input[name="name"]', 'Offer'); await click('Add stage')
  await wait(`Array.from(document.querySelectorAll('select')).some(el => el.getAttribute('aria-label')?.startsWith('Stage for') && el.options.length === 2)`, 'second stage saved')
  await set('select[aria-label^="Stage for"]', '21')
  await wait(`document.body.textContent.includes('Offer · 1')`, 'stage update saved')
  await fs.mkdir('artifacts/browser-smoke', { recursive: true })
  const desktop = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true })
  await fs.writeFile('artifacts/browser-smoke/pipeline-desktop.png', Buffer.from(desktop.data, 'base64'))
  await send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: true })
  await wait(`document.documentElement.scrollWidth <= 390`, 'mobile fits viewport')
  await wait(`document.querySelector('nav[aria-label="Mobile navigation"]') && getComputedStyle(document.querySelector('nav[aria-label="Mobile navigation"]')).display === 'flex'`, 'mobile navigation available')
  const mobile = await send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true })
  await fs.writeFile('artifacts/browser-smoke/pipeline-mobile.png', Buffer.from(mobile.data, 'base64'))
  assert.equal(failures.length, 0)
  assert.equal(writes.length, 8)
  await fs.writeFile('artifacts/browser-smoke/results.json', JSON.stringify({ checks: ['contact create', 'seller contact filtering', 'property save/reload', 'stage create', 'opportunity create', 'activity UTC/contact/opportunity', 'note authorship', 'stage update', 'desktop/mobile layout'], writes }, null, 2))
  console.log('PASS: CRM browser smoke flow; 8 validated writes; desktop and mobile screenshots saved.')
  await send('Fetch.disable')
  await send('Emulation.clearDeviceMetricsOverride')
  const guideUrl = new URL('file:///' + process.cwd().replaceAll('\\', '/') + '/docs/simplified-erd-guide.html').href
  await send('Page.navigate', { url: guideUrl })
  await wait(`document.readyState === 'complete' && document.title.includes('Simplified ERD')`, 'guide opened')
  const pdf = await send('Page.printToPDF', { printBackground: true, preferCSSPageSize: true })
  await fs.writeFile('docs/simplified-erd-guide.pdf', Buffer.from(pdf.data, 'base64'))
  console.log('PASS: illustrated ERD guide exported as PDF.')
} finally { await send('Browser.close'); socket.close() }


