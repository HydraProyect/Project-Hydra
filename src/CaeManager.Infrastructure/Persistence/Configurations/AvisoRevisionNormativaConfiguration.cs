using CaeManager.Domain.VigilanciaNormativa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AvisoRevisionNormativaConfiguration : IEntityTypeConfiguration<AvisoRevisionNormativa>
{
    public void Configure(EntityTypeBuilder<AvisoRevisionNormativa> builder)
    {
        builder.ToTable("AvisosRevisionNormativa");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.IdentificadorBoe).IsRequired().HasMaxLength(50);
        builder.Property(a => a.Titulo).IsRequired().HasMaxLength(AvisoRevisionNormativa.LongitudMaximaTitulo);
        builder.Property(a => a.UrlHtml).IsRequired().HasMaxLength(500);
        builder.Property(a => a.NormaVigilada).IsRequired().HasMaxLength(AvisoRevisionNormativa.LongitudMaximaNormaVigilada);
        builder.Property(a => a.NotasRevision).HasMaxLength(AvisoRevisionNormativa.LongitudMaximaNotasRevision);

        // Idempotencia del sondeo: una publicación del BOE no genera dos avisos aunque el ciclo se repita.
        builder.HasIndex(a => a.IdentificadorBoe).IsUnique();
        builder.HasIndex(a => a.Revisado);

        // Sin HasQueryFilter: catálogo global, el BOE es el mismo para todos
        // los tenants — mismo tratamiento que DelegacionTenant.
    }
}
