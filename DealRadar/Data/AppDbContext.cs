using DealRadar.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DealRadar.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<MasterProduct> MasterProducts => Set<MasterProduct>();
    public DbSet<ProductLink> ProductLinks => Set<ProductLink>();
    public DbSet<PriceHistory> PriceHistories => Set<PriceHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MasterProduct>(entity =>
        {
            entity.Property(e => e.TargetPrice).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<ProductLink>(entity =>
        {
            modelBuilder.Entity<ProductLink>(entity =>
            {
                entity.Property(e => e.CurrentPrice).HasColumnType("decimal(18,2)");
                entity.Property(e => e.CouponDiscount).HasColumnType("decimal(18,2)");
                entity.Property(e => e.MaxCardDiscount).HasColumnType("decimal(18,2)");
                entity.Property(e => e.EffectivePrice).HasColumnType("decimal(18,2)");
                entity.Property(e => e.MarginAmount).HasColumnType("decimal(18,2)");
                entity.Property(e => e.MarginPercentage).HasColumnType("decimal(18,2)");
                entity.Property(e => e.LastAlertedPrice).HasColumnType("decimal(18,2)");
            });
        });

        modelBuilder.Entity<PriceHistory>(entity =>
        {
            entity.Property(e => e.Price).HasColumnType("decimal(18,2)");
        });
    }
}