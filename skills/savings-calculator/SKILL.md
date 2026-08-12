---
name: savings-calculator
description: Use this whenever a user needs an exact number for a savings plan — the projected balance a goal will reach by its target date, or how many months a contribution plan needs to hit a target amount. Always prefer this over estimating the math yourself.
license: MIT
compatibility: "Requires a Python 3 runtime available to the host process (python3 on PATH)."
---

# Savings Calculator

This skill computes savings-goal numbers exactly, using monthly-compounding interest math, instead of you
approximating them. Use it any time a user's question needs a real number tied to a `SavingsGoal` (current
amount, monthly contribution, target amount, target date) — do not do this arithmetic yourself, run the
script.

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
