# Google Calendar configuration

This document explains how to configure Google Calendar for the `FamilyApp.Web` host deployed on Netlify.

## 1. Create a Google OAuth web client

1. Open Google Cloud Console and select the project used for Family App.
2. Enable the Google Calendar API for that project.
3. Go to **APIs & Services** -> **Credentials** and create an **OAuth client ID**.
4. Choose **Web application** as the application type.
5. Add authorized JavaScript origins for every web origin that will host the app:
   - Production Netlify URL (for example: `https://familyapp.netlify.app`)
   - Netlify deploy-preview origins used during pull request reviews
6. Save and copy the generated client ID. It must look like:
   - `<id>.apps.googleusercontent.com`

## 2. Configure Netlify environment variable

1. Open the Netlify site settings for Family App.
2. Add a build environment variable named `GOOGLE_CLIENT_ID`.
3. Set the value to the OAuth web client ID from step 1.
4. Trigger a new deploy.

During build, `scripts/configure-google-calendar.mjs` injects the value into the published `index.html` meta tag.

## 3. Verify configuration in the deployed app

1. Open the deployed web app.
2. Open browser developer tools and inspect `<meta name="google-client-id" ...>` in the page `<head>`.
3. Confirm `content` is set to your `.apps.googleusercontent.com` client ID.
4. Open the calendar integration UI and connect Google Calendar.

## Troubleshooting

- If `GOOGLE_CLIENT_ID` is missing, Google Calendar stays disabled and the app shows a setup message.
- If the client ID does not match the required format, the build script fails.
- If users see origin-related OAuth errors, add the exact deployed origin to the OAuth client authorized JavaScript origins list and redeploy.
