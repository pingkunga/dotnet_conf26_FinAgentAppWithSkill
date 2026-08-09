using FinanceApp.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace FinanceApp.Core;

public sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

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

        // --- Single-user v1: defensive query filter scoped to the seeded default user (docs/spec.md §0/§2) ---
        modelBuilder.Entity<User>().HasQueryFilter(u => u.Id == SeedData.DefaultUserId);
        modelBuilder.Entity<Category>().HasQueryFilter(c => c.UserId == SeedData.DefaultUserId);
        modelBuilder.Entity<Transaction>().HasQueryFilter(t => t.UserId == SeedData.DefaultUserId);
        modelBuilder.Entity<Budget>().HasQueryFilter(b => b.UserId == SeedData.DefaultUserId);
        modelBuilder.Entity<SavingsGoal>().HasQueryFilter(g => g.UserId == SeedData.DefaultUserId);
        modelBuilder.Entity<Receipt>().HasQueryFilter(r => r.UserId == SeedData.DefaultUserId);

        // --- Seed: single user + default category set (docs/spec.md §2) ---
        modelBuilder.Entity<User>().HasData(new User
        {
            Id = SeedData.DefaultUserId,
            DisplayName = "Default User",
            Email = "user@localhost",
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });

        modelBuilder.Entity<Category>().HasData(
            new Category { Id = SeedData.GroceriesCategoryId, UserId = SeedData.DefaultUserId, Name = "Groceries", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.DiningCategoryId, UserId = SeedData.DefaultUserId, Name = "Dining", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.TransportCategoryId, UserId = SeedData.DefaultUserId, Name = "Transport", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.UtilitiesCategoryId, UserId = SeedData.DefaultUserId, Name = "Utilities", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.EntertainmentCategoryId, UserId = SeedData.DefaultUserId, Name = "Entertainment", Kind = CategoryKind.Expense, IsSystemDefault = true },
            new Category { Id = SeedData.IncomeCategoryId, UserId = SeedData.DefaultUserId, Name = "Income", Kind = CategoryKind.Income, IsSystemDefault = true },
            new Category { Id = SeedData.OtherCategoryId, UserId = SeedData.DefaultUserId, Name = "Other", Kind = CategoryKind.Expense, IsSystemDefault = true }
        );
    }
}
