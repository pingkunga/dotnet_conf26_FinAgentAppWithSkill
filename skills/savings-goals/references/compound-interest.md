# Compound interest, in plain language

A savings goal grows from two sources: what the user puts in, and what the balance itself earns over time.
The second part is compound interest — interest earned on interest already credited, not just on the
original amount.

Key intuitions worth explaining to a user, without doing the arithmetic by hand:

- **Time matters more than most people expect.** A contribution made early sits in the account longer and
  compounds more times before the target date than a contribution made later, even if the total amount
  contributed is the same. When a user asks "does it matter if I start now or in six months?", the answer is
  usually yes — meaningfully so, the longer the remaining time horizon is.
- **Rate matters, but consistency matters more for most personal-finance goals.** A savings goal with a
  modest, reliable monthly contribution and a modest interest rate typically beats an irregular contribution
  pattern chasing a slightly higher rate.
- **Monthly compounding vs. one big lump sum**: contributing steadily each month is not mathematically
  identical to contributing the same total once at the end — the earlier monthly contributions each get more
  time to compound than a single lump sum deposited at the end would.
- **A shortfall is easiest to close from two directions**: increasing the monthly contribution, or pushing
  out the target date. Both reduce how much compounding has to do the rest of the work.

When the user wants to know exactly how much a goal will be worth by its target date, or exactly how many
months a given contribution needs, do not estimate it here — hand off to the `savings-calculator` skill,
which computes the real number instead of an approximation.
