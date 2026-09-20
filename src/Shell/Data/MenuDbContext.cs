using EnterpriseWebPlatform.BSS.BFFWeb.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EnterpriseWebPlatform.BSS.BFFWeb.Data;

public sealed class MenuDbContext : DbContext
{
    public MenuDbContext(DbContextOptions<MenuDbContext> options)
        : base(options)
    {
    }

    // MenuDetail is a keyless read model populated by the authorized-menu query.
    public DbSet<MenuDetail> MenuDetails => Set<MenuDetail>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<MenuDetail>(entity =>
        {
            entity.HasNoKey();

            entity.Property(e => e.Microservice)
                .HasColumnName("Microservice");

            entity.Property(e => e.BaseURL)
                .HasColumnName("BaseURL");

            entity.Property(e => e.ManagementAreaName)
                .HasColumnName("ManagementAreaName");

            entity.Property(e => e.TaskName)
                .HasColumnName("TaskName");

            entity.Property(e => e.UrlRelativePath)
                .HasColumnName("UrlRelativePath");

            entity.Property(e => e.IconName)
                .HasColumnName("IconName");
        });
    }
}