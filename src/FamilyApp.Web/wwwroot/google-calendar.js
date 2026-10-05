window.familyGoogleCalendar = (() => {
  let tokenClient, accessToken = null;
  const scope = "https://www.googleapis.com/auth/calendar.readonly";

  function clientId() {
    return document.querySelector('meta[name="google-client-id"]')?.content?.trim() || "";
  }

  function isConfigured() {
    return clientId().length > 0;
  }

  async function connect() {
    const id = clientId();
    if (!id) throw new Error("Google Calendar is not configured yet.");
    if (!window.google?.accounts?.oauth2) throw new Error("Google Identity Services did not load.");
    return new Promise((resolve, reject) => {
      tokenClient ??= google.accounts.oauth2.initTokenClient({
        client_id: id,
        scope,
        callback: response => {
          if (response.error) { reject(new Error(response.error)); return; }
          accessToken = response.access_token;
          resolve(true);
        },
        error_callback: error => reject(new Error(error.type || "Google sign-in failed"))
      });
      tokenClient.requestAccessToken({ prompt: accessToken ? "" : "consent" });
    });
  }

  function disconnect() {
    if (accessToken) google.accounts.oauth2.revoke(accessToken, () => {});
    accessToken = null;
  }

  async function api(path) {
    if (!accessToken) throw new Error("Connect Google Calendar first.");
    const response = await fetch("https://www.googleapis.com/calendar/v3/" + path, {
      headers: { Authorization: "Bearer " + accessToken }
    });
    if (!response.ok) throw new Error("Google Calendar request failed (" + response.status + ").");
    return response.json();
  }

  async function calendars() {
    const data = await api("users/me/calendarList?minAccessRole=reader");
    return (data.items || []).map(x => ({ id:x.id, name:x.summary || x.id, primary:!!x.primary }));
  }

  async function events(calendarIds, start, end) {
    const all = [];
    for (const calendarId of calendarIds) {
      const q = new URLSearchParams({timeMin:start,timeMax:end,singleEvents:"true",orderBy:"startTime"});
      const data = await api("calendars/" + encodeURIComponent(calendarId) + "/events?" + q);
      for (const x of data.items || []) {
        all.push({ id:x.id, calendarId, title:x.summary || "(No title)", start:x.start?.dateTime || x.start?.date, end:x.end?.dateTime || x.end?.date, allDay:!!x.start?.date });
      }
    }
    return all;
  }

  return { isConfigured, connect, disconnect, calendars, events };
})();