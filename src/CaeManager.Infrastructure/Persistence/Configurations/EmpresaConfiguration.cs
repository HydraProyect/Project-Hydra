using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EmpresaConfiguration : IEntityTypeConfiguration<Empresa>
{
    public void Configure(EntityTypeBuilder<Empresa> builder)
    {
        builder.ToTable("Empresas");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.RazonSocial)
            .IsRequired()
            .HasMaxLength(Empresa.LongitudMaximaRazonSocial);

        builder.Property(e => e.Cif)
            .HasMaxLength(Empresa.LongitudCif);

        builder.Property(e => e.Cnae)
            .HasMaxLength(Empresa.LongitudMaximaCnae);

        builder.Property(e => e.ConvenioAplicable)
            .HasMaxLength(Empresa.LongitudMaximaConvenioAplicable);

        builder.Property(e => e.EsActividadAnexoI)
            .IsRequired();

        // F3 (verificación del modelo real, f3-diseno-fisico-empresa-unificada…
        // §3) — EsPropia distingue las filas Empresa originales (true) de las
        // incorporadas desde Cliente/Subcontrata (false). Los cuatro campos
        // siguientes son deuda transitoria explícita: NULL = "no aplica a
        // esta fila", nunca un valor inventado.
        builder.Property(e => e.EsPropia).IsRequired();
        builder.Property(e => e.EjecutivoUsuarioId);
        builder.Property(e => e.EsCritico);
        builder.Property(e => e.Notas).HasMaxLength(2000);
        builder.Property(e => e.NivelServicio).HasMaxLength(50);

        builder.HasIndex(e => new { e.TenantId, e.RazonSocial }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.Cif }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.EsPropia });
        builder.HasIndex(e => e.EjecutivoUsuarioId);

        // Prerequisito de FKs compuestas — ver P0-1 de docs/business/MATURITY_REVIEW.md.
        builder.HasIndex(e => new { e.TenantId, e.Id }).IsUnique();

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
