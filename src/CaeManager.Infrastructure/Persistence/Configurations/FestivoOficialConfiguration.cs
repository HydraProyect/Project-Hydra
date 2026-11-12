using CaeManager.Domain.CalendarioLaboral;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Catálogo global de festivos oficiales: sin <c>TenantId</c> ni filtro de Tenant (extiende <c>Entity</c>), igual que
/// <c>AvisosRevisionNormativa</c>. Lo siembra la migración con <see cref="CatalogoFestivosOficiales.Todos"/> y la
/// aplicación solo lo lee: la migración deja a <c>cae_app_runtime</c> con SELECT y nada más.
/// </summary>
public class FestivoOficialConfiguration : IEntityTypeConfiguration<FestivoOficial>
{
    public void Configure(EntityTypeBuilder<FestivoOficial> builder)
    {
        builder.ToTable("FestivosOficiales", t =>
        {
            t.HasCheckConstraint("CK_FestivosOficiales_ComunidadSegunAmbito",
                "(\"Ambito\" = 1 AND \"Comunidad\" IS NULL) OR (\"Ambito\" IN (2, 3) AND \"Comunidad\" IS NOT NULL)");
            t.HasCheckConstraint("CK_FestivosOficiales_MunicipioSoloLocal",
                "(\"Ambito\" = 3) = (\"Municipio\" IS NOT NULL)");
        });
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Nombre).HasMaxLength(FestivoOficial.LongitudMaximaNombre).IsRequired();
        builder.Property(f => f.Municipio).HasMaxLength(FestivoOficial.LongitudMaximaMunicipio);

        builder.HasIndex(f => f.Fecha);

        builder.HasData(CatalogoFestivosOficiales.Todos);
    }
}
