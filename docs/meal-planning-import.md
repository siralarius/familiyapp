# Meal planning with ChatGPT — stage 1

In the iPhone app open **More → Plan with ChatGPT**. Start your own ChatGPT voice
conversation or dictate a message, discuss meals and purchases, then use the page's
**Copy export instructions** button. Paste those instructions into the same ChatGPT
conversation. Copy the resulting JSON into Family App and select **Review plan**.
Spanish dish/product names are preserved. ChatGPT performs the extraction under
your own account; Family App does not capture audio or call an AI API in this stage.
The browser app does not offer this import yet: the native store provides one atomic
SQLite transaction for meals, shopping and the replay receipt.

Contract version 1:

```json
{
  "version": 1,
  "importId": "f0ee49a6-1a36-4e88-af9b-8e05d8dbcc71",
  "weekStart": "2026-10-12",
  "meals": [{"date":"2026-10-13","slot":"Dinner","dish":"Tortilla española","agreed":true}],
  "shopping": [{"name":"Huevos","quantity":"6","unit":null,"source":"explicit"}],
  "questions": []
}
```

Use a new UUID for a new plan; keep it unchanged when re-exporting the same plan.
Dates are ISO dates; unknown dates/slots remain null and need correction before save.
Slots are `Breakfast`, `Lunch`, `Dinner`. `agreed:false` meals and `source:inferred`
purchases start unselected. Inferred ingredients require the user's explicit selection.
Resolve every question, edit dates/dishes/quantities together, then confirm the selected
entries. A conflicting existing meal prevents the entire save; it is never overwritten.
Existing open purchases are skipped case-insensitively without changing quantities.
A skip also records a permanent tombstone for that import entry ID, so an offline peer
importing or editing the same draft cannot reintroduce it beside the existing product
during sync. The existing product keeps its own ID and remains editable/deletable.

The import receipt and deterministic entry IDs travel through existing encrypted sync.
Repeated imports do not create more entries after restart or receipt delivery. Concurrent
imports of the same ordered draft converge on the same entity IDs. Different devices
must sync before editing/re-importing a previously saved draft; the receipt prevents replay.

## Remaining acceptance

On iPhone 14: import a Spanish plan, resolve a missing date, correct a tentative choice,
confirm purchases, restart the app and verify a trusted peer receives both lists.
Retry the same export, including after deleting an imported entry: nothing is re-added.
Check that a conflicting existing dinner leaves shopping unchanged. No physical-device
acceptance is claimed by unit tests or a simulator build.

## Later live conversation stage

Issue #49 remains open. Its final goal is an explicitly started conversation inside
Family App that progressively updates this same reviewable draft. Implementing it needs
an agreed audio/AI service, secure backend authentication and an owner-approved budget.
No API key may be embedded in the distributed app, and ChatGPT account access is not
assumed to provide API billing. The versioned draft and atomic store are reusable for
that later stage; export/import alone does not complete the issue.

See the [live conversation connection design](live-meal-planning-design.md) for
progressive draft behavior, access/cost decisions and the remaining device checks.
