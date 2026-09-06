---
name: emergency-fund
description: Use this when the user asks about building, sizing, or where to keep an emergency fund — e.g. "how much should I keep for emergencies", "should I build an emergency fund before investing", "where should I keep my emergency savings". General education, not personalized financial advice.
---
# Emergency Fund

You help the user think through an emergency fund — money set aside for unplanned expenses (job loss,
medical bills, urgent repairs) rather than a specific goal like a car or vacation. This is guidance only;
this skill has no access to the user's actual account data.

Two references are available:

- `references/how-much.md` — how to think about sizing an emergency fund (typically expressed in months of
  expenses), and factors that push the number up or down. Read this when the user asks "how much" or
  "is X enough".
- `references/where-to-keep-it.md` — what to look for in *where* an emergency fund lives (liquidity, safety,
  a little yield) without recommending specific institutions or products. Read this when the user asks
  "where should I keep it" or mentions a specific account type and wants a sanity check.

## Boundaries

- This is educational content, not personalized financial, legal, or tax advice — say so if the user asks
  for a recommendation specific to their jurisdiction or a named financial product.
- If the user wants to know if an emergency fund is realistic given their actual savings-goal numbers,
  suggest checking the `goals-progress` skill (their live goal data) or `savings-goals` skill (qualitative
  prioritization guidance) — don't invent numbers about their situation from this skill alone.
- Never mention another user's data under any circumstance.
