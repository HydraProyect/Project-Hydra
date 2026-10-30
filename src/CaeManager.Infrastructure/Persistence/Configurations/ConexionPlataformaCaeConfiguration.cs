using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConexionPlataformaCaeConfiguration : IEntityTypeConfiguration<ConexionPlataformaCae>
{
    public void Configure(EntityTypeBuilder<ConexionPlataformaCae> builder)
    {
        builder.ToTable("ConexionesPlataformaCae");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Nombre).IsRequired().HasMaxLength(ConexionPlataformaCae.LongitudMaximaNombre);

        // Retirada una conexión, su nombre se puede volver a usar.
        builder.HasIndex(c => new { c.TenantId, c.Nombre }).IsUnique().HasFilter("\"EstaEliminado\" = false");

        builder.HasOne<ProveedorPlataformaCae>()
            .WithMany()
            .HasForeignKey(c => c.ProveedorPlataformaCaeId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class CredencialConexionPlataformaCaeConfiguration : IEntityTypeConfiguration<CredencialConexionPlataformaCae>
{
    public void Configure(EntityTypeBuilder<CredencialConexionPlataformaCae> builder)
    {
        builder.ToTable("CredencialesConexionPlataformaCae");
        builder.HasKey(c => c.Id);

        // El cifrado de SecretoApi va en CaeManagerDbContext.OnModelCreating, que tiene el protector.
        // Sin HasMaxLength a propósito: el texto cifrado es más largo que el plano.

        builder.HasIndex(c => new { c.TenantId, c.ConexionPlataformaCaeId }).IsUnique();

        // Clave foránea con el Tenant dentro: una FK no pasa por RLS, y solo así la base rechaza
        // colgar una credencial de la conexión de otro Tenant.
        builder.HasOne<ConexionPlataformaCae>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.ConexionPlataformaCaeId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class UsoConexionPlataformaCaeConfiguration : IEntityTypeConfiguration<UsoConexionPlataformaCae>
{
    public void Configure(EntityTypeBuilder<UsoConexionPlataformaCae> builder)
    {
        builder.ToTable("UsosConexionPlataformaCae");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Operacion).HasConversion<string>().IsRequired().HasMaxLength(40);
        builder.Property(u => u.Resultado).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(u => u.Motivo).HasConversion<string>().HasMaxLength(40);

        builder.HasIndex(u => new { u.TenantId, u.ConexionPlataformaCaeId, u.IniciadoEnUtc });

        // Con el Tenant dentro, por lo mismo que la credencial.
        builder.HasOne<ConexionPlataformaCae>()
            .WithMany()
            .HasForeignKey(u => new { u.TenantId, u.ConexionPlataformaCaeId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
