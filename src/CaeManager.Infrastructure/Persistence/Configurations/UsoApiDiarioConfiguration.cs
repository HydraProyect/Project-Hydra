using CaeManager.Domain.ApiKeys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class UsoApiDiarioConfiguration : IEntityTypeConfiguration<UsoApiDiario>
{
    public void Configure(EntityTypeBuilder<UsoApiDiario> builder)
    {
        builder.ToTable("UsosApiDiarios");
        builder.HasKey(u => u.Id);

        builder.Property(u => u.Endpoint).HasMaxLength(UsoApiDiario.LongitudMaximaEndpoint).IsRequired();

        // El árbitro del ON CONFLICT de RastroUsoApi: sin él, cada petición sería una fila.
        builder.HasIndex(u => new { u.TenantId, u.ApiKeyId, u.TipoClave, u.Dia, u.Endpoint }).IsUnique();
    }
}
