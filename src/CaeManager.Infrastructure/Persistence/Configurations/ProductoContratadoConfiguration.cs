using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Producto Contratado (plano 4, RQ-30, D-6): tabla con Tenant, el Tenant beneficiario. La migración que la
/// crea le pone RLS con FORCE y la política <c>aislamiento_tenant</c>, como a cualquier tabla con Tenant, y
/// concede solo SELECT a <c>cae_app_runtime</c> y a <c>cae_app_soporte</c>. La única vía de escritura es la de la
/// línea Inbound, toda en <c>cae_app_aprovisionamiento</c>: <c>ContrataLineaInboundPorAprovisionamiento</c> le concede
/// INSERT para <c>ContratarLineaInboundCommand</c> (B-04a, D-C, 2026-11-15), y
/// <c>FinalizaLineaInboundPorAprovisionamiento</c>, SELECT de las columnas del WHERE y UPDATE de <c>Estado</c> y
/// <c>VigenteHastaUtc</c> para la baja y la corrección (<c>DarDeBajaLineaInboundCommand</c> y
/// <c>CorregirLineaInboundCommand</c>, opción B del 2026-11-15). La de A6 desde <c>CompletarAltaAutonomaCommand</c>
/// sigue sin abrir.
/// <para>
/// <c>DeclaraSoloInbound</c> (opción A', DECISION_LOG e496f33) la escribe el INSERT de tabla entera de
/// <c>cae_app_aprovisionamiento</c> y la lee <c>cae_app_runtime</c> con su SELECT de tabla entera
/// (<c>TenantSoloInbound</c>). El SELECT por columnas del rol elevado no la incluye: ni la baja ni la corrección la leen.
/// La migración <c>AnadeDeclaraSoloInboundAProductosContratados</c> la crea con <c>DEFAULT false</c>, sin tocar
/// políticas ni privilegios.
/// </para>
/// <para>
/// Fuera de esas vías de la aplicación, dos siembras de Development insertan filas con el rol propietario y el contexto de
/// bootstrap: <c>LineaInboundDemoSeeder</c> (la línea Inbound del Tenant TCE de demostración) y
/// <c>LineaOutboundGestionPropiaDemoSeeder</c> (la línea Outbound de gestión propia de los Tenants de demostración de gestión
/// propia). Solo existen con <c>DatosPrueba:Activo</c> en Development, no son una vía de la aplicación y no abren la escritura
/// de esta tabla a <c>cae_app_runtime</c> ni a <c>cae_app_soporte</c>, que siguen solo con SELECT.
/// </para>
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

    /// <summary>CHECK de coherencia entre estado y vigencia: la baja lo traduce cuando el fin quedaría antes del inicio.</summary>
    public const string RestriccionEstadoYVigencia = "CK_ProductosContratados_EstadoYVigencia";

    /// <summary>CHECK: la declaración «solo Inbound» solo se marca en la línea Inbound (opción A', DECISION_LOG e496f33).</summary>
    public const string RestriccionDeclaraSoloInboundEnInbound = "CK_ProductosContratados_DeclaraSoloInboundEnInbound";

    public void Configure(EntityTypeBuilder<ProductoContratado> builder)
    {
        builder.ToTable("ProductosContratados", t =>
        {
            t.HasCheckConstraint("CK_ProductosContratados_Linea", SolicitudAltaConfiguration.EnValoresDe<LineaProductoContratado>("Linea"));
            t.HasCheckConstraint("CK_ProductosContratados_Tramo", SolicitudAltaConfiguration.EnValoresDe<TramoTrabajadoresActivos>("Tramo"));
            t.HasCheckConstraint("CK_ProductosContratados_Modalidad", SolicitudAltaConfiguration.EnValoresDe<ModalidadSuscripcionAlta>("Modalidad"));
            t.HasCheckConstraint("CK_ProductosContratados_Estado", SolicitudAltaConfiguration.EnValoresDe<EstadoProductoContratado>("Estado"));
            t.HasCheckConstraint("CK_ProductosContratados_PagadorFormato", "\"PagadorStripeCustomerId\" ~ '^[A-Za-z0-9_]+$'");
            t.HasCheckConstraint(RestriccionEstadoYVigencia,
                "(\"Estado\" = 'Vigente' AND \"VigenteHastaUtc\" IS NULL) OR " +
                "(\"Estado\" = 'Finalizado' AND \"VigenteHastaUtc\" IS NOT NULL AND \"VigenteHastaUtc\" >= \"VigenteDesdeUtc\")");
            // La declaración «solo Inbound» (opción A', DECISION_LOG e496f33) solo va marcada en una fila de la línea Inbound.
            t.HasCheckConstraint(RestriccionDeclaraSoloInboundEnInbound, "NOT \"DeclaraSoloInbound\" OR \"Linea\" = 'Inbound'");
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
        // una lectura previa: dos altas a la vez pasarían cualquier comprobación previa. La corrección finaliza la vigente
        // antes de insertar la nueva, en la misma transacción, así que no choca con su propia fila. Una fila finalizada o eliminada no cuenta, como en el gate DN-7.
        builder.HasIndex(p => p.TenantId)
            .IsUnique()
            .HasFilter("\"Linea\" = 'Inbound' AND \"Estado\" = 'Vigente' AND NOT \"EstaEliminado\"")
            .HasDatabaseName(IndiceInboundVigentePorTenant);

        // Filtro global de Tenant y de papelera centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
