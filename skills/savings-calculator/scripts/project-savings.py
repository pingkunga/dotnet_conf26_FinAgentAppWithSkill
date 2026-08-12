#!/usr/bin/env python3
"""Deterministic savings-goal projector for the savings-calculator skill.

Positional args (all strings, per the file-skill run_skill_script contract):
    current_amount         e.g. "1000"
    monthly_contribution   e.g. "200"
    annual_rate_pct        e.g. "5" for 5%; use "0" if no growth rate is assumed
    months                 e.g. "12"

Prints the projected balance after `months` months of monthly contributions and monthly-compounding
interest at the given annual rate. Standard future-value-of-an-annuity-due-plus-lump-sum formula:
starting balance grows for the full period, each contribution is assumed to land at the start of its
month and grows for the months remaining after it.
"""
import sys


def project_savings(current_amount: float, monthly_contribution: float, annual_rate_pct: float, months: int) -> float:
    monthly_rate = (annual_rate_pct / 100.0) / 12.0

    if monthly_rate == 0:
        return current_amount + monthly_contribution * months

    future_value_of_current = current_amount * (1 + monthly_rate) ** months
    # Contribution at the start of month k (k = 1..months) compounds for (months - k + 1) months.
    future_value_of_contributions = monthly_contribution * (
        ((1 + monthly_rate) ** months - 1) / monthly_rate
    ) * (1 + monthly_rate)

    return future_value_of_current + future_value_of_contributions


def main() -> None:
    if len(sys.argv) != 5:
        print(
            "Error: expected exactly 4 arguments (current_amount monthly_contribution "
            "annual_rate_pct months), got " + str(len(sys.argv) - 1)
        )
        sys.exit(1)

    try:
        current_amount = float(sys.argv[1])
        monthly_contribution = float(sys.argv[2])
        annual_rate_pct = float(sys.argv[3])
        months = int(sys.argv[4])
    except ValueError as ex:
        print(f"Error: could not parse arguments as numbers: {ex}")
        sys.exit(1)

    if months < 0:
        print("Error: months must be zero or positive.")
        sys.exit(1)

    result = project_savings(current_amount, monthly_contribution, annual_rate_pct, months)
    print(f"Projected balance after {months} month(s): {result:.2f}")


if __name__ == "__main__":
    main()
