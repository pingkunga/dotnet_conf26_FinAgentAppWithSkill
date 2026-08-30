# How the numbers are computed, in plain language

Both scripts in this skill use the same underlying idea — money changing value over time at a fixed monthly
rate — just pointed in opposite directions: `project-savings.py` grows a balance toward a target,
`project-debt-payoff.py` shrinks a balance toward zero. Read this when a user asks *how* a number was
derived, not just what it is — don't re-derive either formula by hand, this is for explaining the result
the script already gave you.

## Growth: `project-savings.py`

Each month, the balance earns interest on everything already in it, and each new contribution starts
earning interest from the month it lands. The script assumes a contribution lands at the **start** of its
month, so a contribution made in month 1 compounds for the full remaining period, while a contribution made
in the final month barely compounds at all. This is why contributing earlier matters more than contributing
the same total amount later — the earlier money has more months to compound.

## Payoff: `project-debt-payoff.py`

Each month, interest accrues on whatever balance is left, and the fixed payment first covers that month's
interest, then reduces the principal. As the balance shrinks, less of each payment goes to interest and more
goes to principal, so payoff accelerates over time even though the payment amount never changes.

**The one sharp edge worth naming explicitly**: if the monthly payment doesn't exceed that month's
interest-only amount (`balance × monthly rate`), the balance never shrinks — every payment just covers
interest and nothing more, so the debt is never paid off. The script detects this and returns an error
instead of a wrong or infinite answer. If a user hits this, the right response is "the payment needs to
increase" — not retrying the script with the same numbers, and not estimating a payoff date yourself.

## Not what this reference is for

This explains the *math*, not whether a payoff plan or savings plan is actually a good idea for the user's
situation — for that kind of qualitative guidance (is this realistic, how should I prioritize, what can I
afford), defer to the `savings-goals` skill instead.
