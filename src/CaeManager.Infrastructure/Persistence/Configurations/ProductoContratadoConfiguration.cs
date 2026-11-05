using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Producto Contratado (plano 4, RQ-30, D-6): tabla con Tenant, el Tenant beneficiario. La migración que la
/// crea le pone RLS con FORCE y la política <c>aislamiento_tenant</c>, como a cualquier tabla con Tenant, y
/// concede solo SELECT a <c>cae_app_runtime</c> y a <c>cae_app_soporte</c>: ningún rol de la aplicación la
/// escribe hasta que A6 abra la vía desde <c>CompletarAltaAutonomaCommand</c>.
/// <para>
/// Los enumerados se guardan por nombre, con un CHECK que repite la lista, como en <c>SolicitudesAlta</c>.
/// Sin índice único por Tenant: cuántos Productos Contratados vigentes admite un Tenant beneficiario no
/// está decidido, y añadirlo después es aditivo porque hasta A6 no hay filas.
/// </para>
/// </summary>
public class ProductoContratadoConfiguration : IEntityTypeConfiguration<ProductoContratado>
{
    public void Configure(EntityTypeBuilder<ProductoContratado> builder)
    {
        builder.ToTable("ProductosContratados", t =>
        {
            t.HasCheckConstraint("CK_ProductosContratados_Linea", SolicitudAltaConfiguration.EnValoresDe<LineaProductoContratado>("Linea"));
            t.HasCheckConstraint("CK_ProductosContratados_Tramo", SolicitudAltaConfiguration.EnValoresDe<TramoTrabajadoresActivos>("Tramo"));
            t.HasCheckConstraint("CK_ProductosContratados_Modalidad", SolicitudAltaConfiguration.EnValoresDe<ModalidadSuscripcionAlta>("Modalidad"));
            t.HasCheckConstraint("CK_ProductosContratados_Estado", SolicitudAltaConfiguration.EnValoresDe<EstadoProductoContratado>("Estado"));
            t.HasCheckConstraint("CK_ProductosContratados_PagadorNoVacio", "btrim(\"PagadorStripeCustomerId\") <> ''");
            t.HasCheckConstraint("CK_ProductosContratados_EstadoYVigencia",
                "(\"Estado\" = 'Vigente' AND \"VigenteHastaUtc\" IS NULL) OR " +
                "(\"Estado\" = 'Finalizado' AND \"VigenteHastaUtc\" IS NOT NULL AND \"VigenteHastaUtc\" >= \"VigenteDesdeUtc\")");
        });
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Linea).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(p => p.PagadorStripeCustomerId).IsRequired()
            .HasMaxLength(ProductoContratado.LongitudMaximaPagadorStripeCustomerId);
        builder.Property(p => p.Tramo).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Modalidad).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(p => new { p.TenantId, p.Estado })
            .HasDatabaseName("IX_ProductosContratados_TenantId_Estado");

        // Filtro global de Tenant y de papelera centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
