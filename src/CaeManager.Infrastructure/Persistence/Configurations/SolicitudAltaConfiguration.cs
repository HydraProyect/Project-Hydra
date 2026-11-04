using CaeManager.Domain.AltaAutonoma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Solicitud de alta autónoma (DA-2): plano de Plataforma, sin <c>TenantId</c> y sin HasQueryFilter.
/// La migración que crea las dos tablas les pone RLS con FORCE, ninguna política y ningún permiso para
/// los roles de la aplicación: hasta que el incremento del registro abra su vía de acceso, solo el rol
/// propietario de las tablas (migraciones e identidad de arranque) las lee o escribe.
/// </summary>
public class SolicitudAltaConfiguration : IEntityTypeConfiguration<SolicitudAlta>
{
    public void Configure(EntityTypeBuilder<SolicitudAlta> builder)
    {
        builder.ToTable("SolicitudesAlta", t =>
        {
            t.HasCheckConstraint("CK_SolicitudesAlta_Estado",
                "\"Estado\" IN ('Iniciada', 'CorreoVerificado', 'DerivadaAAcceso', 'DatosCompletos', 'PendientePago', "
                + "'Pagada', 'Completada', 'Reembolsada', 'Caducada', 'Bloqueada')");
            t.HasCheckConstraint("CK_SolicitudesAlta_CorreoSalvoBorrado",
                "(\"Correo\" IS NOT NULL) OR (\"DatosPersonalesBorradosEnUtc\" IS NOT NULL)");
            t.HasCheckConstraint("CK_SolicitudesAlta_CompletadaConTenant",
                "(\"Estado\" <> 'Completada') OR (\"TenantIdCreado\" IS NOT NULL)");
        });
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Estado).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Correo).HasMaxLength(SolicitudAlta.LongitudMaximaCorreo);
        builder.Property(s => s.CorreoNormalizado).HasMaxLength(SolicitudAlta.LongitudMaximaCorreo);
        builder.Property(s => s.HashContrasena).HasMaxLength(SolicitudAlta.LongitudMaximaHashContrasena);
        builder.Property(s => s.HashTokenVerificacion).HasMaxLength(SolicitudAlta.LongitudHashToken);
        builder.Property(s => s.IpOrigen).HasMaxLength(SolicitudAlta.LongitudMaximaIp);
        builder.Property(s => s.UserAgentOrigen).HasMaxLength(SolicitudAlta.LongitudMaximaUserAgent);
        builder.Property(s => s.RazonSocial).HasMaxLength(SolicitudAlta.LongitudMaximaRazonSocial);
        builder.Property(s => s.CifNif).HasMaxLength(SolicitudAlta.LongitudMaximaCifNif);
        builder.Property(s => s.DomicilioFiscal).HasMaxLength(SolicitudAlta.LongitudMaximaDomicilioFiscal);
        builder.Property(s => s.Tramo).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Modalidad).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.CheckoutSessionId).HasMaxLength(SolicitudAlta.LongitudMaximaIdStripe);
        builder.Property(s => s.StripeCustomerId).HasMaxLength(SolicitudAlta.LongitudMaximaIdStripe);
        builder.Property(s => s.StripeSubscriptionId).HasMaxLength(SolicitudAlta.LongitudMaximaIdStripe);
        builder.Property(s => s.Motivo).HasMaxLength(SolicitudAlta.LongitudMaximaMotivo);
        builder.Property(s => s.VersionFila).IsConcurrencyToken();

        builder.HasMany(s => s.Aceptaciones).WithOne()
            .HasForeignKey(a => a.SolicitudAltaId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(s => s.Aceptaciones).HasField("_aceptaciones");

        // Sin HasQueryFilter: plano de Plataforma, no pertenece a un Tenant.
    }
}

public class AceptacionCondicionesAltaConfiguration : IEntityTypeConfiguration<AceptacionCondicionesAlta>
{
    public void Configure(EntityTypeBuilder<AceptacionCondicionesAlta> builder)
    {
        builder.ToTable("AceptacionesSolicitudAlta");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Documento).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudMaximaDocumento);
        builder.Property(a => a.Version).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudMaximaVersion);
        builder.Property(a => a.HashSha256).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudHash);
        builder.Property(a => a.Ip).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudMaximaIp);

        builder.HasIndex(a => new { a.SolicitudAltaId, a.Documento }).IsUnique();

        // Sin HasQueryFilter: plano de Plataforma, no pertenece a un Tenant.
    }
}
