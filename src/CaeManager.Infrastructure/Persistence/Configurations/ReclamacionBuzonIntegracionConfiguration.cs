using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ReclamacionBuzonIntegracionConfiguration : IEntityTypeConfiguration<ReclamacionBuzonIntegracion>
{
    public void Configure(EntityTypeBuilder<ReclamacionBuzonIntegracion> builder)
    {
        builder.ToTable("ReclamacionesBuzonIntegracion");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.BuzonEmail).IsRequired().HasMaxLength(ReclamacionBuzonIntegracion.LongitudMaximaBuzonEmail);

        // La guarda de negocio: un mismo buzón no puede estar reclamado dos
        // veces, sin importar qué Tenant o conexión lo intente. Nombre
        // explícito porque el repositorio lo compara literalmente al
        // traducir la violación (23505) a un resultado de negocio.
        builder.HasIndex(r => r.BuzonEmail).IsUnique().HasDatabaseName(ReclamacionBuzonIntegracionRepository.IndiceUnicoBuzonEmail);

        // Cada conexión reclama como mucho un buzón — sostiene LiberarAsync/
        // ExisteReclamacionAsync como una relación 1:1 con ConexionIntegracion.
        builder.HasIndex(r => r.ConexionIntegracionId).IsUnique();

        // Sin HasQueryFilter: la unicidad que impone esta tabla cruza
        // Tenants por definición (ver el comentario de clase de
        // ReclamacionBuzonIntegracion) — un filtro de tenant o una política
        // de aislamiento la neutralizarían.
    }
}
