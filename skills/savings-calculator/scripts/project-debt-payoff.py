#!/usr/bin/env python3
"""Deterministic debt-payoff projector for the savings-calculator skill — the amortization counterpart to
project-savings.py's compounding-growth projection: balance decreases toward zero instead of growing.

Positional args (all strings, per the file-skill run_skill_script contract):
    current_balance      e.g. "50000"
    monthly_payment       e.g. "3000"
    annual_rate_pct       e.g. "18" for 18%; use "0" for an interest-free payoff

Prints the number of whole months of fixed payments needed to pay the balance to zero, and the approximate
total interest paid over that payoff, using the standard fixed-payment amortization formula.
"""
import math
import sys


def months_to_payoff(current_balance: float, monthly_payment: float, annual_rate_pct: float) -> int:
    if current_balance <= 0:
        return 0

    monthly_rate = (annual_rate_pct / 100.0) / 12.0

    if monthly_rate == 0:
        return math.ceil(current_balance / monthly_payment)

    interest_only_payment = current_balance * monthly_rate
    if monthly_payment <= interest_only_payment:
        raise ValueError(
            f"monthly payment ({monthly_payment:.2f}) does not cover the monthly interest "
            f"({interest_only_payment:.2f}); the balance will never be paid off at this payment amount."
        )

    months = -math.log(1 - (monthly_rate * current_balance) / monthly_payment) / math.log(1 + monthly_rate)
    return math.ceil(months)


def main() -> None:
    if len(sys.argv) != 4:
        print(
            "Error: expected exactly 3 arguments (current_balance monthly_payment annual_rate_pct), "
            "got " + str(len(sys.argv) - 1)
        )
        sys.exit(1)

    try:
        current_balance = float(sys.argv[1])
        monthly_payment = float(sys.argv[2])
        annual_rate_pct = float(sys.argv[3])
    except ValueError as ex:
        print(f"Error: could not parse arguments as numbers: {ex}")
        sys.exit(1)

    if current_balance < 0:
        print("Error: current_balance must be zero or positive.")
        sys.exit(1)
    if monthly_payment <= 0:
        print("Error: monthly_payment must be positive.")
        sys.exit(1)
    if annual_rate_pct < 0:
        print("Error: annual_rate_pct must be zero or positive.")
        sys.exit(1)

    if current_balance == 0:
        print("Already paid off (balance is zero).")
        return

    try:
        months = months_to_payoff(current_balance, monthly_payment, annual_rate_pct)
    except ValueError as ex:
        print(f"Error: {ex}")
        sys.exit(1)

    total_paid = months * monthly_payment
    interest_paid = total_paid - current_balance
    print(
        f"Months to pay off: {months} month(s); approximate total interest paid: {interest_paid:.2f}"
    )


if __name__ == "__main__":
    main()
