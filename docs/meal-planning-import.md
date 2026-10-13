# ChatGPT meal-planning import (stage 1)

Family App supports an explicit copy/paste import flow for meal-planning conversations.

## Supported integration

1. Start or upload your meal-planning conversation in ChatGPT.
2. Ask ChatGPT to return Family App meal-planning JSON contract version `1` with:
   - `version`
   - optional `weekStart` (`YYYY-MM-DD`)
   - `meals` (`date` or `day`, `mealSlot`, `dish`, optional `notes`, agreement state)
   - `shoppingItems` (`name`, optional `quantity`, optional `unit`, `source` = `explicit` or `inferred`)
   - optional `unresolvedQuestions`
3. In Family App, open **Meals** and paste the JSON in **ChatGPT import**.
4. Build the draft, review/correct meals and shopping items together, then save selected items.

This stage does not provide direct background audio capture from other apps and does not auto-sync live ChatGPT voice sessions into Family App.

## Privacy and service requirements

- Audio and transcript processing happens in the external service used to create the JSON export (for example ChatGPT account/API usage).
- Family App receives only the pasted JSON draft.
- Approved items are saved through existing local repositories and remain local-first.
- Existing household synchronization continues to use the current trusted-device sync flow.

## Safety rules in import

- Tentative/unclear meals are not auto-selected as agreed meals.
- Missing dates or invalid meal slots are flagged before save.
- Existing planner entries are not overwritten unless the user explicitly selects replacement.
- Inferred shopping items require explicit confirmation before save.
- Re-import of the same data skips existing matching meals/items to avoid accidental duplicates.
