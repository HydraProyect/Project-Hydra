using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Producto Contratado (plano 4, RQ-30, D-6): tabla con Tenant, el Tenant beneficiario. La migración que la
/// crea le pone RLS con FORCE y la política <c>aislamiento_tenant</c>, como a cualquier tabla con Tenant, y
/// concede solo SELECT a <c>cae_app_runtime</c> y a <c>cae_app_soporte</c>. La única vía de escritura es la de la
/// línea Inbound (B-04a, <c>ContratarLineaInboundCommand</c>): <c>ContrataLineaInboundPorAprovisionamiento</c> concede
/// INSERT, y nada más, a <c>cae_app_aprovisionamiento</c> (D-C, 2026-11-15). La de A6 desde
/// <c>CompletarAltaAutonomaCommand</c> sigue sin abrir.
/// <para>
/// Los enumerados se guardan por nombre, con un CHECK que repite la lista, como en <c>SolicitudesAlta</c>.
/// Un solo índice único, parcial: un Producto Contratado de la línea Inbound vigente y no eliminado por Tenant
/// beneficiario (D-B, 2026-11-15). Cuántos admite de las demás líneas no está decidido.
/// </para>
/// <para>
/// Una fila de la línea Inbound vigente es condición necesaria, no suficiente, de la lectura entre Tenants de su
/// Tenant beneficiario (gate DN-7 en <c>app_inbound_tenant_contratista_del_contrato</c>): hacen falta además un
/// contrato de acceso vigente y vinculado al Tenant de la Empresa contratista y una autorización vigente de ese Tenant.
/// Aun así, ninguna vía de escritura puede dejar que <c>cae_app_runtime</c> escriba <c>"Linea" = 'Inbound'</c>: la
/// política <c>aislamiento_tenant</c> solo exige <c>"TenantId" = app_ctx_tenant_id()</c>, así que un INSERT o UPDATE
/// del propio Tenant bajo ella le dejaría concederse su línea Inbound. Por eso el runtime sigue en SELECT y la escribe
/// solo <c>cae_app_aprovisionamiento</c>, el rol que adopta una Sesión Privilegiada de Aprovisionamiento sobre ese
/// Tenant. Cuando A6 abra su vía, tendrá que mantener esta propiedad.
/// </para>
/// </summary>
public class ProductoContratadoConfiguration : IEntityTypeConfiguration<ProductoContratado>
{
    /// <summary>Índice único parcial: un Producto Contratado de la línea Inbound vigente por Tenant beneficiario (D-B).</summary>
    public const string IndiceInboundVigentePorTenant = "UX_ProductosContratados_InboundVigentePorTenant";

    public void Configure(EntityTypeBuilder<ProductoContratado> builder)
    {
        builder.ToTable("ProductosContratados", t =>
        {
            t.HasCheckConstraint("CK_ProductosContratados_Linea", SolicitudAltaConfiguration.EnValoresDe<LineaProductoContratado>("Linea"));
            t.HasCheckConstraint("CK_ProductosContratados_Tramo", SolicitudAltaConfiguration.EnValoresDe<TramoTrabajadoresActivos>("Tramo"));
            t.HasCheckConstraint("CK_ProductosContratados_Modalidad", SolicitudAltaConfiguration.EnValoresDe<ModalidadSuscripcionAlta>("Modalidad"));
            t.HasCheckConstraint("CK_ProductosContratados_Estado", SolicitudAltaConfiguration.EnValoresDe<EstadoProductoContratado>("Estado"));
            t.HasCheckConstraint("CK_ProductosContratados_PagadorFormato", "\"PagadorStripeCustomerId\" ~ '^[A-Za-z0-9_]+$'");
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

        // D-B: un solo Producto Contratado de la línea Inbound vigente por Tenant beneficiario. Lo impone la base, no
        // una lectura previa: cae_app_aprovisionamiento no tiene SELECT sobre la tabla (D-C), y dos altas a la vez
        // pasarían cualquier comprobación previa. Una fila finalizada o eliminada no cuenta, como en el gate DN-7.
        builder.HasIndex(p => p.TenantId)
            .IsUnique()
            .HasFilter("\"Linea\" = 'Inbound' AND \"Estado\" = 'Vigente' AND NOT \"EstaEliminado\"")
            .HasDatabaseName(IndiceInboundVigentePorTenant);

        // Filtro global de Tenant y de papelera centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
