import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import vm from "node:vm";

const indexUrl = new URL("../src/FamilyApp.Web/wwwroot/index.html", import.meta.url);
const scriptPath = fileURLToPath(new URL("configure-google-calendar.mjs", import.meta.url));
const calendarSource = await readFile(new URL("../src/FamilyApp.Web/wwwroot/google-calendar.js", import.meta.url), "utf8");

async function configure(clientId, html = undefined) {
  const directory = await mkdtemp(join(tmpdir(), "family-calendar-test-"));
  const index = join(directory, "index.html");
  try {
    const original = html ?? await readFile(indexUrl, "utf8");
    await writeFile(index, original);
    const result = spawnSync(process.execPath, [scriptPath, index], {
      env: { ...process.env, GOOGLE_CLIENT_ID: clientId }, encoding: "utf8"
    });
    if (result.error) throw result.error;
    return { status: result.status, html: await readFile(index, "utf8"), original };
  } finally { await rm(directory, { recursive: true, force: true }); }
}

test("published app remains unconfigured when the Netlify variable is absent", async () => {
  const result = await configure("");
  assert.equal(result.status, 0);
  assert.equal(result.html, result.original);
  assert.match(result.html, /name="google-client-id" content=""/);
});

test("configured Netlify build injects the trimmed web client ID into actual host HTML", async () => {
  const result = await configure("  123-family.apps.googleusercontent.com  ");
  assert.equal(result.status, 0);
  assert.match(result.html, /name="google-client-id" content="123-family.apps.googleusercontent.com"/);
  assert.equal(await readFile(indexUrl, "utf8"), result.original);
});

test("invalid and injection-shaped client IDs fail without modifying publish output", async () => {
  for (const clientId of ["not-an-oauth-client", '123.apps.googleusercontent.com\"><script>alert(1)</script>', "https://example.com"]) {
    const result = await configure(clientId);
    assert.notEqual(result.status, 0);
    assert.equal(result.html, result.original);
  }
});

test("configured build fails if the expected configuration tag is missing", async () => {
  const result = await configure("123-family.apps.googleusercontent.com", "<html></html>");
  assert.notEqual(result.status, 0);
  assert.equal(result.html, result.original);
});

function calendar(clientId, overrides = {}) {
  const context = vm.createContext({
    window: {}, document: { querySelector: () => clientId === null ? null : { content: clientId } },
    URLSearchParams, ...overrides
  });
  vm.runInContext(calendarSource, context);
  return context.window.familyGoogleCalendar;
}

test("browser runtime detects missing/blank configuration and rejects direct connect attempts", async () => {
  for (const id of [null, "", "  "]) {
    const api = calendar(id);
    assert.equal(api.isConfigured(), false);
    await assert.rejects(api.connect(), /Google Calendar is not configured yet/);
  }
});

test("configured runtime requests read-only OAuth, fetches calendars, and revokes on disconnect", async () => {
  const requests = [];
  const revocations = [];
  let tokenOptions;
  const google = { accounts: { oauth2: {
    initTokenClient(options) {
      tokenOptions = options;
      return { requestAccessToken(request) {
        requests.push(request);
        options.callback({ access_token: "fake-test-token" });
      } };
    },
    revoke(token, callback) { revocations.push(token); callback(); }
  } } };
  const api = calendar("123-family.apps.googleusercontent.com", {
    google, window: { google },
    fetch: async (url, options) => {
      assert.match(url, /users\/me\/calendarList/);
      assert.equal(options.headers.Authorization, "Bearer fake-test-token");
      return { ok: true, json: async () => ({ items: [{ id: "primary", summary: "Family", primary: true }] }) };
    }
  });
  assert.equal(api.isConfigured(), true);
  await api.connect();
  assert.equal(tokenOptions.client_id, "123-family.apps.googleusercontent.com");
  assert.equal(tokenOptions.scope, "https://www.googleapis.com/auth/calendar.readonly");
  assert.equal(requests[0].prompt, "consent");
  const calendars = await api.calendars();
  assert.equal(calendars[0].name, "Family");
  api.disconnect();
  assert.deepEqual(revocations, ["fake-test-token"]);
  await assert.rejects(api.calendars(), /Connect Google Calendar first/);
});

test("configured runtime handles identity-service and OAuth failures", async () => {
  await assert.rejects(calendar("123-family.apps.googleusercontent.com").connect(), /did not load/);
  const google = { accounts: { oauth2: { initTokenClient: options => ({
    requestAccessToken: () => options.callback({ error: "access_denied" })
  }) } } };
  await assert.rejects(calendar("123-family.apps.googleusercontent.com", { google, window: { google } }).connect(), /access_denied/);
});
