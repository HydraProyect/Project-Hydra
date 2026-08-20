using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TenantAlcanzadoPorConcesionConfiguration : IEntityTypeConfiguration<TenantAlcanzadoPorConcesion>
{
    public void Configure(EntityTypeBuilder<TenantAlcanzadoPorConcesion> builder)
    {
        builder.ToTable("TenantsAlcanzadosPorConcesion");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.ConcesionPrivilegioId).IsRequired();
        builder.Property(t => t.TenantId).IsRequired();

        builder.HasOne<ConcesionPrivilegio>().WithMany(c => c.TenantsAlcanzados)
            .HasForeignKey(t => t.ConcesionPrivilegioId)
            .OnDelete(DeleteBehavior.Cascade);

        // Un tenant no se repite dentro de la misma concesión: el alcance es un
        // conjunto, y una fila duplicada no añadiría permiso pero sí ruido a
        // cualquier auditoría de "quién puede entrar aquí".
        builder.HasIndex(t => new { t.ConcesionPrivilegioId, t.TenantId }).IsUnique();

        // "¿Quién puede entrar en este tenant?" — la consulta desde el lado del
        // cliente, que es la que da sentido a guardar el alcance en su tabla.
        builder.HasIndex(t => t.TenantId);

        // Sin HasQueryFilter: catálogo global, igual que su concesión.
    }
}
