using CaeManager.Domain.Configuracion;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>Fila única — su unicidad la garantiza el Id fijo de la semilla, no una constraint de BD adicional.</summary>
public class ParametroSistemaConfiguration : IEntityTypeConfiguration<ParametroSistema>
{
    public void Configure(EntityTypeBuilder<ParametroSistema> builder)
    {
        builder.ToTable("ParametrosSistema");
        builder.HasKey(p => p.Id);

        builder.HasData(new
        {
            Id = ParametroSistemaSeedData.IdUnico,
            UmbralAmbarDias = ParametroSistemaSeedData.UmbralAmbarDias,
            UmbralRojoDias = ParametroSistemaSeedData.UmbralRojoDias
        });
    }
}
