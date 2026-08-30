---
name: savings-calculator
description: Use this whenever a user needs an exact number for a savings plan — the projected balance a goal will reach by its target date, how many months a contribution plan needs to hit a target amount, or how many months it takes to pay off a debt at a given payment and interest rate. Always prefer this over estimating the math yourself.
license: MIT
compatibility: "Requires a Python 3 runtime available to the host process (python3 on PATH)."
metadata:
  author: pingkunga-finance
  version: "1.0"
---

# Savings Calculator

This skill computes savings-goal and debt-payoff numbers exactly, using monthly-compounding interest math,
instead of you approximating them. Use it any time a user's question needs a real number tied to a
`SavingsGoal` (current amount, monthly contribution, target amount, target date) or a debt payoff — do not
do this arithmetic yourself, run the appropriate script below.

## Script: `scripts/project-savings.py`

Call `run_skill_script` with `scriptName` set to exactly `scripts/project-savings.py` (the full relative
path, including the `scripts/` folder and the `.py` extension — the tool does not accept the bare name).

Run it with four **positional string arguments**, in this exact order:

1. `current_amount` — amount already saved, e.g. `"1000"`
2. `monthly_contribution` — planned monthly deposit, e.g. `"200"`
3. `annual_rate_pct` — expected annual interest/return rate as a percent, e.g. `"5"` for 5%. Use `"0"` if the
   user doesn't have a rate in mind — the script still gives a correct answer with no growth assumed.
4. `months` — number of months to project forward, e.g. `"12"`

The script prints the projected balance after that many months of monthly contributions and compounding.

Always pass every argument as a plain string, even numbers — the runner only accepts a JSON array of
strings. If the user gave you a target date instead of a month count, convert it to whole months from today
before calling the script.

When you get the result back, state the exact number the script returned — don't round more than the user's
question calls for, and don't re-derive or sanity-check it with your own mental math.

After stating the number, consider whether the user would also benefit from qualitative guidance — is this
goal realistic, how should they prioritize it against other goals, what could they change. If so, **load the
`savings-goals` skill** and read whichever of its two references fits, rather than inventing advice
yourself: `references/compound-interest.md` for growth intuition, `references/fifty-thirty-twenty.md` for
affordability questions.

## Script: `scripts/project-debt-payoff.py`

Call `run_skill_script` with `scriptName` set to exactly `scripts/project-debt-payoff.py` (the full relative
path — same rule as above, the tool does not accept the bare name).

Run it with three **positional string arguments**, in this exact order:

1. `current_balance` — remaining debt balance, e.g. `"50000"`
2. `monthly_payment` — planned fixed monthly payment, e.g. `"3000"`
3. `annual_rate_pct` — annual interest rate as a percent, e.g. `"18"` for 18%. Use `"0"` for an
   interest-free payoff.

The script prints how many whole months of payments are needed to pay the balance to zero, plus the
approximate total interest paid over that payoff. If the monthly payment doesn't even cover that month's
interest, the script returns an error instead of a number — tell the user the payment needs to increase,
don't retry with the same numbers or estimate a payoff date yourself.

Same rules as `project-savings.py` apply: pass every argument as a plain string, and state the script's
result exactly as returned.

## Reference available

- `references/formula.md` — plain-language explanation of both scripts' math (compounding growth and
  amortization payoff), including why an underpaid debt never gets paid off. Read this when the user asks
  *how* a number was derived, not just what it is — don't re-derive either formula by hand.
