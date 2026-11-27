# Browser smoke verification

This dependency-free script uses Node 24's built-in WebSocket and the Chrome DevTools Protocol. It tests React against in-memory API fixtures; the .NET suite separately verifies real middleware, Identity, controllers, and SQLite, and SchemaUpgrade tests real PostgreSQL migration behavior.

Run Vite on `http://127.0.0.1:5184`, then launch headless Chrome with a dedicated profile inside `artifacts`, remote debugging port 9338, and a blank tab. Run from the backend root:

```powershell
node tools\BrowserSmoke\crm-smoke.mjs
```

The script closes only its Chrome session. It checks contact-owner initialization, contact creation, seller-contact filtering, property save and reload, stage/opportunity creation, activity foreign keys and UTC dates, note authorship, stage updates, and desktop/mobile layout/navigation. Screenshots and validated requests are saved under `artifacts/browser-smoke` (ignored by Git). No live CRM data is written. The final step exports the standalone illustrated ERD guide to `docs/simplified-erd-guide.pdf`.

Use dedicated ports and a separate browser profile; never attach it to your regular browser, since it mocks API traffic and closes the session at completion.
