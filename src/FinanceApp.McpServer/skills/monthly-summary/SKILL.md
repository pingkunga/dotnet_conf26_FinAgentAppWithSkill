---
name: monthly-summary
description: Produces a monthly income/expense summary with budget-vs-actual status, for advice on how the user's month went.
---
# Monthly Summary & Advice

Use this skill when the user asks how their month went, wants a spending summary, or wants
advice based on their actual income/expenses and budgets for a given month.

## Getting the numbers

Call `read_skill_resource` on this skill with `resourceName` set to `summary-<year>-<month>`,
zero-padded, e.g. `summary-2026-08` for August 2026. If the user doesn't name a month, use the
current month. The resource returns JSON with:

- `totalIncome`, `totalExpense` for the month
- `byCategory`: each category's total for the month, tagged `Income` or `Expense`
- `budgetStatuses`: each budgeted category's limit, amount spent, percent used, and whether it's
  `Near` (80-100% used) or `Over` (>100% used) — same Near/Over thresholds the budgeting skill uses

## Giving advice

- Lead with the headline numbers (income vs expense, net for the month).
- Call out any `Over` categories first, then `Near` ones — this mirrors the budgeting skill's own
  policy, so advice stays consistent regardless of which skill the user's question routes through.
- Compare to categories with no budget set at all only if they're a large share of spending — don't
  nag about every uncategorized or unbudgeted dollar.
- Never mention another user's data under any circumstance — this skill only ever has access to the
  data of the single user who started this chat session.
