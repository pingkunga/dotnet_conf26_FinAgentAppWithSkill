# The 50/30/20 framework

A simple starting point when a user has a savings goal but no `MonthlyContribution` decided yet, or wants a
sanity check on one they already picked.

Split take-home income into three buckets:

- **50% — Needs**: rent/mortgage, groceries, utilities, minimum debt payments, insurance. Costs that don't
  disappear if the user tightens their budget.
- **30% — Wants**: dining out, entertainment, subscriptions, discretionary shopping. The most flexible
  bucket, and usually where a stalled savings goal finds its extra contribution room.
- **20% — Savings & debt payoff**: this is the bucket a `SavingsGoal`'s `MonthlyContribution` should come
  from, alongside any other savings goals or extra debt payments the user is already making.

How to use this with a user's actual numbers:

- If the user shares an income figure, 20% of it is a reasonable **starting point** for total monthly
  savings across all goals combined — not a hard rule, and it needs splitting further if the user has more
  than one active goal.
- If a proposed `MonthlyContribution` for a single goal already exceeds roughly 20% of income on its own,
  say so plainly — that's usually a sign the target date is too aggressive, not that the user is bad at
  saving.
- 50/30/20 is a starting heuristic, not a diagnosis — if the user describes a situation where "Needs" already
  exceeds 50% (high rent market, dependents, medical costs), don't force the framework; help them find
  savings room within their actual constraints instead.

This framework tells you *how much room* the user plausibly has for `MonthlyContribution`. Once a number is
chosen, use `savings-calculator` to show exactly what that contribution achieves by the target date.
