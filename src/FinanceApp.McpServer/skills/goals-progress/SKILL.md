---
name: goals-progress
description: Reports the current live status of every one of the user's savings/investment goals — how much saved so far, percent complete, and (when a monthly contribution is set) an estimate of months remaining at the current pace.
---
# Goals Progress

Use this skill when the user asks how their savings goals are doing, wants a status check across all
goals, or asks "am I on track" without naming a specific number to compute.

## Getting the numbers

Call `read_skill_resource` on this skill with `resourceName` set to exactly `current` — there's no
date/period to specify, this always returns every one of the user's goals as they stand right now. The
resource returns a JSON array, one entry per goal:

- `name`, `targetAmount`, `currentAmount`
- `percentComplete` — currentAmount / targetAmount, already computed
- `targetDate` (may be absent if the goal has none)
- `monthlyContribution` (may be absent if the goal has none)
- `projectedMonthsRemaining` — only present when a monthly contribution is set and the goal isn't
  complete yet; whole months at the current contribution pace, ignoring any investment growth (for a
  growth-aware projection, defer to the `savings-calculator` skill's `project-savings.py` instead — this
  number is a simple linear estimate, not compound-interest math)

## Giving advice

- If there are no goals yet, say so plainly and suggest creating one — don't invent goal names.
- Lead with goals furthest behind their target date (if a `targetDate` is set) or lowest `percentComplete`
  otherwise.
- For an exact growth-aware projection or "how many months until X", switch to the `savings-calculator`
  skill instead of estimating from `projectedMonthsRemaining` by hand.
- For qualitative guidance on prioritizing between goals, load the `savings-goals` skill's references
  rather than inventing advice yourself.
- Never mention another user's data under any circumstance — this skill only ever has access to the data
  of the single user who started this chat session.
