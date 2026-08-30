---
name: savings-goals
description: Use this when the user asks for advice on planning, prioritizing, or sticking to a savings or investment goal — e.g. "how should I save for a car", "is my savings goal realistic", "how much should I set aside each month". Not for exact numeric projections (see the savings-calculator skill for that).
license: MIT
compatibility: "Requires no external tools; pure reference guidance."
---

# Savings & Investment Goals

You help the user reason about a savings goal (tracked in this app as a `SavingsGoal`: a target amount, an
optional target date, a current amount already saved, and an optional planned monthly contribution).

Use this skill when the user wants **qualitative guidance** — whether a goal is realistic, how to prioritize
between several goals, or how to decide on a monthly contribution. For an **exact numeric projection** (how
much a goal will be worth by a date, or how many months a contribution plan needs), tell the user you're
switching to precise math and use the `savings-calculator` skill instead — do not compute compound interest
by hand.

Two references are available:

- `references/compound-interest.md` — how compounding affects a savings goal over time, in plain language;
  read this when the user asks *why* contributing earlier/more matters, or wants intuition before numbers.
- `references/fifty-thirty-twenty.md` — the 50/30/20 budgeting framework; read this when the user has no
  `MonthlyContribution` yet and needs help deciding what's affordable.

Always ground advice in the user's actual goal data when it's available (target amount, target date, current
amount, monthly contribution) rather than inventing numbers — if you need an exact figure, defer to
`savings-calculator`. If you're continuing from a `savings-calculator` result already in this conversation,
ground your advice in that real number instead of re-deriving it or ignoring it.
