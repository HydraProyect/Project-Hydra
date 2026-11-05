using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// CIF o NIF del Tenant propietario (alta autónoma), en su propia tabla y no en <c>Tenants</c>, que leen
/// los tres roles de la aplicación: el NIF de una persona física es un dato personal (DA-11). La migración
/// que la crea le pone RLS con FORCE, ninguna política y ningún permiso para los roles de la aplicación,
/// como <c>SolicitudesAlta</c>: solo el rol propietario de las tablas la lee o la escribe.
/// <para>
/// El índice único sobre <c>Cif</c> aplica DM-5 en la base: un solo Tenant propietario por CIF entre
/// todos los Tenants. Un Tenant sin fila no tiene CIF declarado y no compite por la unicidad.
/// </para>
/// </summary>
public class CifPropietarioTenantConfiguration : IEntityTypeConfiguration<CifPropietarioTenant>
{
    public void Configure(EntityTypeBuilder<CifPropietarioTenant> builder)
    {
        builder.ToTable("CifsPropietarioTenant");
        builder.HasKey(c => c.TenantId);
        builder.Property(c => c.TenantId).ValueGeneratedNever();
        builder.Property(c => c.Cif).IsRequired().HasMaxLength(CifPropietarioTenant.LongitudMaxima);
        builder.HasIndex(c => c.Cif).IsUnique();

        // Borrar un Tenant borra su CIF: no queda un dato personal huérfano en una tabla que nadie lee.
        builder.HasOne<Tenant>().WithOne()
            .HasForeignKey<CifPropietarioTenant>(c => c.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Sin HasQueryFilter: identificador de plataforma, no un dato operativo del Tenant.
    }
}
