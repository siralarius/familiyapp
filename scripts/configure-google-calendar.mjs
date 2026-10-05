import { readFile, writeFile } from "node:fs/promises";

const indexPath = process.argv[2];
if (!indexPath) {
  throw new Error("Usage: configure-google-calendar.mjs <published-index.html>");
}

const clientId = process.env.GOOGLE_CLIENT_ID?.trim() ?? "";
if (!clientId) {
  console.log("GOOGLE_CLIENT_ID is not set; Google Calendar will remain disabled.");
  process.exit(0);
}

if (!/^[A-Za-z0-9-]+\.apps\.googleusercontent\.com$/.test(clientId)) {
  throw new Error("GOOGLE_CLIENT_ID must be a Google OAuth web client ID.");
}

const html = await readFile(indexPath, "utf8");
const tagPattern = /(<meta\s+name="google-client-id"\s+content=")[^"]*("\s*\/?>)/;
if (!tagPattern.test(html)) {
  throw new Error("The google-client-id meta tag is missing from the published app.");
}

await writeFile(indexPath, html.replace(tagPattern, (_tag, prefix, suffix) => `${prefix}${clientId}${suffix}`));
console.log("Google Calendar OAuth client ID configured for this build.");