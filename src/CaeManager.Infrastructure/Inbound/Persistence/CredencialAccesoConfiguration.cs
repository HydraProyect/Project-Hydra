using CaeManager.Domain.Inbound.AccesoFisico;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Inbound.Persistence;

public class CredencialAccesoConfiguration : IEntityTypeConfiguration<CredencialAcceso>
{
    public void Configure(EntityTypeBuilder<CredencialAcceso> builder)
    {
        builder.ToTable("CredencialesAcceso", t =>
        {
            // El origen siempre es otro Tenant: la credencial es del Tenant TCE y el Trabajador es del Tenant TEC. Como
            // CK_EvidenciasInbound_OrigenEnOtroTenant.
            t.HasCheckConstraint("CK_CredencialesAcceso_OrigenEnOtroTenant", "\"TenantOrigenId\" <> \"TenantId\"");
            t.HasCheckConstraint("CK_CredencialesAcceso_SerieDesdeUno", "\"Serie\" >= 1");
            // Repiten el enum EstadoCredencialAcceso y MotivoRevocacionCredencial para una escritura que se salte el dominio.
            t.HasCheckConstraint("CK_CredencialesAcceso_EstadoValido", "\"Estado\" IN ('Emitida', 'Revocada', 'Caducada')");
            t.HasCheckConstraint(
                "CK_CredencialesAcceso_MotivoRevocacionCerrado",
                "\"MotivoRevocacion\" IS NULL OR \"MotivoRevocacion\" IN ('PerdidaOExtravio', 'UsoIndebido', 'FinDeLaRelacion', 'Otro')");
            // Revocada lleva motivo, momento y usuario; ninguna otra los lleva. Caducada lleva su momento; ninguna otra.
            t.HasCheckConstraint(
                "CK_CredencialesAcceso_RevocacionCoherente",
                "(\"Estado\" = 'Revocada' AND \"MotivoRevocacion\" IS NOT NULL AND \"RevocadaEnUtc\" IS NOT NULL AND \"RevocadaPorUsuarioId\" IS NOT NULL) " +
                "OR (\"Estado\" <> 'Revocada' AND \"MotivoRevocacion\" IS NULL AND \"RevocadaEnUtc\" IS NULL AND \"RevocadaPorUsuarioId\" IS NULL)");
            t.HasCheckConstraint(
                "CK_CredencialesAcceso_CaducidadCoherente",
                "(\"Estado\" = 'Caducada') = (\"CaducadaEnUtc\" IS NOT NULL)");
        });
        builder.HasKey(c => c.Id);

        builder.Property(c => c.SujetoNombre).IsRequired().HasMaxLength(CredencialAcceso.LongitudMaximaSujetoNombre);
        builder.Property(c => c.SujetoDocumentoIdentidad).HasMaxLength(CredencialAcceso.LongitudMaximaDocumentoIdentidad);
        builder.Property(c => c.Estado).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.MotivoRevocacion).HasConversion<string>().HasMaxLength(30);

        // Una credencial Emitida por Trabajador y Tenant TCE (M8). Parcial: las revocadas y caducadas quedan como historia y no
        // estorban a la nueva. Es la barrera real contra dos emisiones simultáneas (el comando comprueba antes y captura el 23505).
        builder.HasIndex(c => new { c.TenantId, c.TenantOrigenId, c.TrabajadorOrigenId }).IsUnique()
            .HasFilter("\"Estado\" = 'Emitida'")
            .HasDatabaseName("IX_CredencialesAcceso_TenantId_Trabajador_Emitida");

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating. Sin FK hacia el otro Tenant:
        // TenantOrigenId y TrabajadorOrigenId son ids de procedencia.
    }
}
