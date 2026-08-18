namespace FinanceApp.Core.Entities;

public enum CategoryKind
{
    Expense,
    Income,
}

public enum TransactionSource
{
    Manual,
    Agent,
    ReceiptOcr,
}

public enum ReceiptOcrStatus
{
    Pending,
    Succeeded,
    Failed,
    Manual,
}
