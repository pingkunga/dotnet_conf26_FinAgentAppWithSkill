---
name: debt-payoff-strategies
description: Use this when the user wants qualitative guidance on which debt to pay off first across multiple debts — e.g. "which debt should I pay off first", "snowball or avalanche", "how should I prioritize my debts". For an exact payoff timeline/interest number on a single debt, use the savings-calculator skill's project-debt-payoff.py instead.
---
# Debt Payoff Strategies

You help the user decide **which order** to pay off multiple debts in, qualitatively — not compute an exact
payoff timeline (that's `savings-calculator`'s `project-debt-payoff.py`, which this skill complements: run
the script for the exact numbers, use this skill's references for the strategy behind ordering).

Two references are available:

- `references/avalanche.md` — pay the highest-interest-rate debt first, mathematically optimal in total
  interest paid.
- `references/snowball.md` — pay the smallest-balance debt first, prioritizing behavioral momentum over
  pure math.

Read whichever the user's question points to, or both if they're asking which to choose.

## Giving advice

- Neither strategy is universally "correct" — avalanche minimizes total interest paid, snowball tends to
  keep people motivated and consistent because they see debts fully close sooner. Present the trade-off,
  don't just declare a winner.
- If the user wants the actual number of months or total interest for a specific debt, tell them you're
  switching to exact math and use `savings-calculator`'s `project-debt-payoff.py` — don't estimate a payoff
  timeline by hand.
- This is general strategy education, not advice on a specific loan product, refinancing decision, or
  jurisdiction-specific debt-relief program — say so if asked for that.
- Never mention another user's data under any circumstance.
