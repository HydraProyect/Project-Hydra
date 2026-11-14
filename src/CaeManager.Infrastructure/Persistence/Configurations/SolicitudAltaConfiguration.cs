using CaeManager.Domain.AltaAutonoma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Solicitud de alta autónoma (DA-2): plano de Plataforma, sin <c>TenantId</c> y sin HasQueryFilter.
/// La migración que crea las dos tablas les pone RLS con FORCE, ninguna política y ningún permiso para
/// los roles de la aplicación: hasta que el incremento del registro abra su vía de acceso, solo una
/// identidad que evite la RLS (superusuario o <c>BYPASSRLS</c>) las lee o escribe. Con FORCE, el rol
/// propietario de las tablas sin esa marca tampoco ve ninguna fila.
/// </summary>
public class SolicitudAltaConfiguration : IEntityTypeConfiguration<SolicitudAlta>
{
    public void Configure(EntityTypeBuilder<SolicitudAlta> builder)
    {
        builder.ToTable("SolicitudesAlta", t =>
        {
            t.HasCheckConstraint("CK_SolicitudesAlta_Estado", EnValoresDe<EstadoSolicitudAlta>("Estado"));
            t.HasCheckConstraint("CK_SolicitudesAlta_Tramo", EnValoresDe<TramoTrabajadoresActivos>("Tramo"));
            t.HasCheckConstraint("CK_SolicitudesAlta_Modalidad", EnValoresDe<ModalidadSuscripcionAlta>("Modalidad"));
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
        builder.Property(s => s.HashCredencialContinuacion).HasMaxLength(SolicitudAlta.LongitudHashToken);
        builder.Property(s => s.IpOrigen).HasMaxLength(SolicitudAlta.LongitudMaximaIp);
        builder.Property(s => s.UserAgentOrigen).HasMaxLength(SolicitudAlta.LongitudMaximaUserAgent);
        builder.Property(s => s.RazonSocial).HasMaxLength(SolicitudAlta.LongitudMaximaRazonSocial);
        builder.Property(s => s.CifNif).HasMaxLength(SolicitudAlta.LongitudMaximaCifNif);
        builder.Property(s => s.DomicilioFiscal).HasMaxLength(SolicitudAlta.LongitudMaximaDomicilioFiscal);
        builder.Property(s => s.Telefono).HasMaxLength(SolicitudAlta.LongitudMaximaTelefono);
        builder.Property(s => s.Tramo).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Modalidad).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.CheckoutSessionId).HasMaxLength(SolicitudAlta.LongitudMaximaIdStripe);
        builder.Property(s => s.StripeCustomerId).HasMaxLength(SolicitudAlta.LongitudMaximaIdStripe);
        builder.Property(s => s.StripeSubscriptionId).HasMaxLength(SolicitudAlta.LongitudMaximaIdStripe);
        builder.Property(s => s.Motivo).HasMaxLength(SolicitudAlta.LongitudMaximaMotivo);
        builder.Property(s => s.VersionFila).IsConcurrencyToken();

        // El enlace de verificación busca por el hash del token (endpoint anónimo) y el trabajo de
        // caducidad por estado y plazo: sin índice, los dos recorrerían la tabla entera.
        builder.HasIndex(s => s.HashTokenVerificacion).IsUnique().HasFilter("\"HashTokenVerificacion\" IS NOT NULL");
        // El paso «Empresa» (A4) busca la Solicitud de alta por el hash del credencial de continuación.
        builder.HasIndex(s => s.HashCredencialContinuacion).IsUnique().HasFilter("\"HashCredencialContinuacion\" IS NOT NULL");
        builder.HasIndex(s => new { s.Estado, s.CaducaEnUtc });

        builder.HasMany(s => s.Aceptaciones).WithOne()
            .HasForeignKey(a => a.SolicitudAltaId)
            .OnDelete(DeleteBehavior.Cascade);
        // AutoInclude: cargar una Solicitud de alta trae siempre sus aceptaciones. Sin ellas,
        // BorrarDatosPersonales vaciaría una colección en memoria vacía, marcaría el borrado como hecho
        // y dejaría en la base las filas con la IP.
        builder.Navigation(s => s.Aceptaciones).HasField("_aceptaciones").AutoInclude();

        // Sin HasQueryFilter: plano de Plataforma, no pertenece a un Tenant.
    }

    /// <summary>
    /// CHECK con los nombres del enum, que es como se guarda (<c>HasConversion&lt;string&gt;</c>). Un valor
    /// nuevo del enum cambia el modelo y obliga a una migración, en vez de fallar solo al insertar.
    /// </summary>
    internal static string EnValoresDe<TEnum>(string columna) where TEnum : struct, Enum =>
        $"\"{columna}\" IN ({string.Join(", ", Enum.GetNames<TEnum>().Select(n => $"'{n}'"))})";
}

public class AceptacionCondicionesAltaConfiguration : IEntityTypeConfiguration<AceptacionCondicionesAlta>
{
    public void Configure(EntityTypeBuilder<AceptacionCondicionesAlta> builder)
    {
        builder.ToTable("AceptacionesSolicitudAlta");
        builder.HasKey(a => a.Id);
        // El Id lo asigna el dominio. Sin esto, una aceptación añadida a una Solicitud de alta ya
        // cargada se clasificaría como Modified y SaveChanges intentaría un UPDATE de una fila inexistente.
        builder.Property(a => a.Id).ValueGeneratedNever();

        builder.Property(a => a.Documento).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudMaximaDocumento);
        builder.Property(a => a.Version).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudMaximaVersion);
        builder.Property(a => a.HashSha256).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudHash);
        builder.Property(a => a.Ip).IsRequired().HasMaxLength(AceptacionCondicionesAlta.LongitudMaximaIp);
        builder.Property(a => a.ImportePermanencia)
            .HasPrecision(AceptacionCondicionesAlta.PrecisionImporte, AceptacionCondicionesAlta.EscalaImporte);

        builder.HasIndex(a => new { a.SolicitudAltaId, a.Documento }).IsUnique();

        // Sin HasQueryFilter: plano de Plataforma, no pertenece a un Tenant.
    }
}
