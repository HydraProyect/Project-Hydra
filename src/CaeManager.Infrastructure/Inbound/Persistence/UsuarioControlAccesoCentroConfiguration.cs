using CaeManager.Domain.Centros;
using CaeManager.Domain.Inbound.ControlAcceso;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Inbound.Persistence;

public class UsuarioControlAccesoCentroConfiguration : IEntityTypeConfiguration<UsuarioControlAccesoCentro>
{
    public void Configure(EntityTypeBuilder<UsuarioControlAccesoCentro> builder)
    {
        builder.ToTable("UsuariosControlAccesoCentro");
        builder.HasKey(a => a.Id);

        builder.HasIndex(a => new { a.TenantId, a.UsuarioId, a.CentroId })
            .IsUnique()
            .HasDatabaseName("IX_UsuariosControlAccesoCentro_Unico");
        builder.HasIndex(a => new { a.TenantId, a.CentroId })
            .HasDatabaseName("IX_UsuariosControlAccesoCentro_Centro");

        // Compuesta con TenantId: un Centro de otro Tenant es irrepresentable en la base, no solo «no encontrado» en el comando.
        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(a => new { a.TenantId, a.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
