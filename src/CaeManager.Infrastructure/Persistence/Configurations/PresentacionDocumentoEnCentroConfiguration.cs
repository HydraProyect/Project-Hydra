using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class PresentacionDocumentoEnCentroConfiguration : IEntityTypeConfiguration<PresentacionDocumentoEnCentro>
{
    public void Configure(EntityTypeBuilder<PresentacionDocumentoEnCentro> builder)
    {
        builder.ToTable("PresentacionesDocumentoEnCentro");
        builder.HasKey(p => p.Id);

        // Historial: la última presentación de un Documento en un Centro se lee por este índice.
        builder.HasIndex(p => new { p.TenantId, p.DocumentoId, p.CentroId });

        // Claves foráneas compuestas por Tenant (como AcreditacionDocumentoPlataforma): un Documento nunca se presenta a un
        // Centro de otro Tenant, ni siquiera por una fila escrita con un Id ajeno. Sin el Documento no hay historial que
        // conservar (cascada); un Centro con presentaciones no se borra en físico (restringido).
        builder.HasOne<Documento>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.DocumentoId })
            .HasPrincipalKey(d => new { d.TenantId, d.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Centro>().WithMany()
            .HasForeignKey(p => new { p.TenantId, p.CentroId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(p => p.Origen).HasConversion<int>();

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
