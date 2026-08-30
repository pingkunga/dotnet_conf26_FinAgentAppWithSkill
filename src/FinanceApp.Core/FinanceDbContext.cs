using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Core;

public sealed class FinanceDbContext(
    DbContextOptions<FinanceDbContext> options,
    ICurrentUserAccessor currentUserAccessor)
    : IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>(options)
{
    // Captured once at construction time — HasQueryFilter lambdas below reference this field, not
    // currentUserAccessor.UserId directly (docs/spec.md §2a point 4: a live service call inside the
    // filter expression interacts badly with EF Core's model cache).
    private readonly Guid? _currentUserId = currentUserAccessor.UserId;

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Transaction> Transactions => Set<Transaction>();

    public DbSet<Budget> Budgets => Set<Budget>();

    public DbSet<SavingsGoal> SavingsGoals => Set<SavingsGoal>();

    public DbSet<Receipt> Receipts => Set<Receipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // --- Decimal precision (amounts are money: 18,2) ---
        modelBuilder.Entity<Transaction>().Property(t => t.Amount).HasPrecision(18, 2);
        modelBuilder.Entity<Budget>().Property(b => b.LimitAmount).HasPrecision(18, 2);
        modelBuilder.Entity<SavingsGoal>().Property(g => g.TargetAmount).HasPrecision(18, 2);
        modelBuilder.Entity<SavingsGoal>().Property(g => g.CurrentAmount).HasPrecision(18, 2);
        modelBuilder.Entity<SavingsGoal>().Property(g => g.MonthlyContribution).HasPrecision(18, 2);
        modelBuilder.Entity<Receipt>().Property(r => r.ExtractedAmount).HasPrecision(18, 2);

        // --- Enums stored as text for readability in psql (docs/spec.md §2) ---
        modelBuilder.Entity<Category>().Property(c => c.Kind).HasConversion<string>();
        modelBuilder.Entity<Transaction>().Property(t => t.Source).HasConversion<string>();
        modelBuilder.Entity<Receipt>().Property(r => r.OcrStatus).HasConversion<string>();

        // --- Relationships (FK-only nav, no back-collections needed for v1) ---
        modelBuilder.Entity<Transaction>()
            .HasOne(t => t.Category)
            .WithMany()
            .HasForeignKey(t => t.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Transaction>()
            .HasOne(t => t.Receipt)
            .WithMany()
            .HasForeignKey(t => t.ReceiptId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<Budget>()
            .HasOne(b => b.Category)
            .WithMany()
            .HasForeignKey(b => b.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Receipt>()
            .HasOne(r => r.ExtractedCategory)
            .WithMany()
            .HasForeignKey(r => r.ExtractedCategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // --- Indices ---
        modelBuilder.Entity<Budget>()
            .HasIndex(b => new { b.UserId, b.CategoryId, b.PeriodMonth })
            .IsUnique();

        modelBuilder.Entity<Transaction>().HasIndex(t => new { t.UserId, t.OccurredOn });

        // --- Defensive query filters scoped to the current authenticated user (docs/spec.md §2a) ---
        // Category: null UserId = global default category, visible to everyone.
        modelBuilder.Entity<Category>().HasQueryFilter(c => c.UserId == null || c.UserId == _currentUserId);
        // Everything else: no authenticated user (_currentUserId == null) means no rows — fail-closed.
        modelBuilder.Entity<Transaction>().HasQueryFilter(t => t.UserId == _currentUserId);
        modelBuilder.Entity<Budget>().HasQueryFilter(b => b.UserId == _currentUserId);
        modelBuilder.Entity<SavingsGoal>().HasQueryFilter(g => g.UserId == _currentUserId);
        modelBuilder.Entity<Receipt>().HasQueryFilter(r => r.UserId == _currentUserId);

        // --- Seed: global default category set, not tied to any user (docs/spec.md §2a point 2) ---
        modelBuilder.Entity<Category>().HasData(
            new Category { Id = SeedData.GroceriesCategoryId, UserId = null, Name = "Groceries", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.DiningCategoryId, UserId = null, Name = "Dining", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.TransportCategoryId, UserId = null, Name = "Transport", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.UtilitiesCategoryId, UserId = null, Name = "Utilities", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.EntertainmentCategoryId, UserId = null, Name = "Entertainment", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.IncomeCategoryId, UserId = null, Name = "Income", Kind = CategoryKind.Income, IsSystemDefault = true },
            new Category { Id = SeedData.OtherCategoryId, UserId = null, Name = "Other", Kind = CategoryKind.Expense, IsSystemDefault = true },
            // Expense, not a "Transfer" kind: a goal contribution genuinely removes money from spendable
            // funds this month, which keeps MonthlySummaryRepository.TotalExpense and
            // BudgetRepository.GetAllocationSummaryAsync's AllocatedTotal meaningful with no special-casing.
            new Category { Id = SeedData.SavingsCategoryId, UserId = null, Name = "Savings", Kind = CategoryKind.Expense, IsSystemDefault = true }
        );
    }
}
