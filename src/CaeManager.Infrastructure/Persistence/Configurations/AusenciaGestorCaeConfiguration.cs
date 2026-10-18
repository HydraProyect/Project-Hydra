using System.Text.Json;
using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AusenciaGestorCaeConfiguration : IEntityTypeConfiguration<AusenciaGestorCae>
{
    public void Configure(EntityTypeBuilder<AusenciaGestorCae> builder)
    {
        builder.ToTable("AusenciasGestorCae");
        builder.HasKey(a => a.Id);
        builder.HasIndex(a => new { a.TenantId, a.GestorUsuarioId }).IsUnique().HasFilter("\"CerradaEnUtc\" IS NULL");
        builder.Property(a => a.Coberturas).HasColumnType("jsonb")
            .HasConversion(v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<List<CoberturaEmpresaAusencia>>(v, (JsonSerializerOptions?)null)!)
            .Metadata.SetValueComparer(new ValueComparer<List<CoberturaEmpresaAusencia>>(
                (a, b) => a!.SequenceEqual(b!), v => v.Aggregate(0, (h, c) => HashCode.Combine(h, c.GetHashCode())), v => v.ToList()));
    }
}
