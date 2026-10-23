using CaeManager.Domain.ContratosAcceso;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EvidenciasInbound;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EvidenciaInboundConfiguration : IEntityTypeConfiguration<EvidenciaInbound>
{
    public void Configure(EntityTypeBuilder<EvidenciaInbound> builder)
    {
        builder.ToTable("EvidenciasInbound", t =>
        {
            // Restricción médica en la base (DN-5 (b)): una evidencia de solo conclusión de aptitud no lleva archivo ni
            // huella y sí la conclusión; cualquier otra lleva archivo y huella y ninguna conclusión. Repite
            // EvidenciaInbound.Registrar para una escritura que se salte el dominio.
            t.HasCheckConstraint(
                "CK_EvidenciasInbound_ArchivoSegunConclusion",
                "(\"SoloConclusionAptitud\" AND \"ArchivoUrl\" IS NULL AND \"HuellaSha256\" IS NULL AND \"ConclusionAptitud\" IS NOT NULL) " +
                "OR (NOT \"SoloConclusionAptitud\" AND \"ArchivoUrl\" IS NOT NULL AND \"HuellaSha256\" IS NOT NULL AND \"ConclusionAptitud\" IS NULL)");
            // La evidencia médica es de un Trabajador con su documento de identidad, y su conclusión es una de las tres
            // del certificado de aptitud. Repite EvidenciaInbound.Registrar y el enum ConclusionAptitud.
            t.HasCheckConstraint(
                "CK_EvidenciasInbound_SujetoYConclusionDeLaMedica",
                "NOT \"SoloConclusionAptitud\" OR (\"TrabajadorOrigenId\" IS NOT NULL AND \"SujetoDocumentoIdentidad\" IS NOT NULL " +
                "AND \"ConclusionAptitud\" IN ('Apto', 'AptoConRestricciones', 'NoApto'))");
            t.HasCheckConstraint(
                "CK_EvidenciasInbound_VencimientoNoAnteriorAEmision",
                "\"FechaVencimiento\" IS NULL OR \"FechaVencimiento\" >= \"FechaEmision\"");
            t.HasCheckConstraint(
                "CK_EvidenciasInbound_OrigenEnOtroTenant",
                "\"TenantOrigenId\" <> \"TenantId\"");
        });
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SujetoNombre).IsRequired().HasMaxLength(EvidenciaInbound.LongitudMaximaSujetoNombre);
        builder.Property(e => e.SujetoDocumentoIdentidad).HasMaxLength(EvidenciaInbound.LongitudMaximaDocumentoIdentidad);
        builder.Property(e => e.ArchivoUrl).HasMaxLength(EvidenciaInbound.LongitudMaximaArchivoUrl);
        builder.Property(e => e.HuellaSha256).HasMaxLength(EvidenciaInbound.LongitudHuellaSha256).IsFixedLength();
        builder.Property(e => e.MotivoRechazo).HasMaxLength(EvidenciaInbound.LongitudMaximaMotivoRechazo);
        builder.Property(e => e.ConclusionAptitud).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.Estado).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(e => e.CausaRechazo).HasConversion<string>().HasMaxLength(30);

        // Una evidencia por Documento original y contrato de acceso (RegistrarEvidenciaInboundCommand lo comprueba antes
        // con EvidenciaInbound.YaRegistrada). Cubre también la consulta por contrato.
        // Nombre explícito: el que genera EF tiene 64 caracteres y PostgreSQL trunca los identificadores a 63.
        builder.HasIndex(e => new { e.TenantId, e.ContratoAccesoId, e.DocumentoOrigenId }).IsUnique()
            .HasDatabaseName("IX_EvidenciasInbound_TenantId_Contrato_DocumentoOrigen");
        builder.HasIndex(e => new { e.TenantId, e.DocumentoOrigenId });

        builder.HasOne<ContratoAcceso>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.ContratoAccesoId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // El tipo es del catálogo del Tenant del Cliente empresarial, elegido por el validador: FK compuesta con
        // TenantId, como Documento hacia TipoDocumento. La correspondencia con el tipo del original es manual en M1.
        builder.HasOne<TipoDocumento>().WithMany()
            .HasForeignKey(e => new { e.TenantId, e.TipoDocumentoId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
