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

        // La misma guarda por identidad estable del buzón: cierra el alta de un alias (UPN, proxy) de un
        // buzón que otro Tenant ya tiene reclamado con otra dirección. Parcial porque las reclamaciones
        // anteriores a la columna no la tienen hasta que la renovación la completa.
        builder.Property(r => r.IdentidadBuzon).HasMaxLength(ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon);
        builder.HasIndex(r => r.IdentidadBuzon).IsUnique()
            .HasFilter("\"IdentidadBuzon\" IS NOT NULL")
            .HasDatabaseName(ReclamacionBuzonIntegracionRepository.IndiceUnicoIdentidadBuzon);

        // Guarda transitoria del alta de buzones compartidos (ver ReclamacionBuzonIntegracion.CuentaParaGuardaTransitoria).
        // La restricción de exclusión que la aplica no la modela EF: la crea la migración 20261109210000 en SQL, con el
        // nombre que el repositorio compara al traducir su 23P01 (mismo caso que EX_Asignaciones_SinSolapeVigencia).
        builder.Property(r => r.EsBuzonCompartido);
        builder.Property(r => r.FueraDeGuardaTransitoriaUtc);

        // Cada conexión reclama como mucho un buzón — sostiene LiberarAsync/
        // ExisteReclamacionAsync como una relación 1:1 con ConexionIntegracion.
        builder.HasIndex(r => r.ConexionIntegracionId).IsUnique();

        // El aislamiento por Tenant propietario lo aplica RLS; el índice
        // único sigue garantizando la unicidad global del buzón.
    }
}
