using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public sealed class PropagacionDocumentoConfiguration : IEntityTypeConfiguration<PropagacionDocumento>
{
    public void Configure(EntityTypeBuilder<PropagacionDocumento> builder)
    {
        builder.ToTable("PropagacionesDocumento");
        builder.HasKey(p => p.Id);
        builder.Ignore(p => p.Hecha);
        builder.HasIndex(p => new { p.TenantId, p.DocumentoId, p.CanalGestionDocumentalId }).IsUnique();
        builder.HasOne<Documento>().WithMany().HasForeignKey(p => new { p.TenantId, p.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<CanalGestionDocumental>().WithMany().HasForeignKey(p => new { p.TenantId, p.CanalGestionDocumentalId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}
