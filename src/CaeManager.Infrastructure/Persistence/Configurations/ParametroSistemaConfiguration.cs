using CaeManager.Domain.Configuracion;
using CaeManager.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

/// <summary>Fila única por tenant — invariante impuesta en Postgres, no solo por el Id fijo de la semilla del tenant #1.</summary>
public class ParametroSistemaConfiguration : IEntityTypeConfiguration<ParametroSistema>
{
    public void Configure(EntityTypeBuilder<ParametroSistema> builder)
    {
        builder.ToTable("ParametrosSistema");
        builder.HasKey(p => p.Id);

        // El Id fijo de la semilla solo protege al tenant #1: cualquier otro
        // tenant siembra su fila con un Id nuevo (ver comentario de TenantId
        // más abajo) y nada en el modelo impedía una segunda fila para el
        // mismo tenant (auditoría Módulo 8).
        builder.HasIndex(p => p.TenantId).IsUnique();

        builder.HasData(new
        {
            Id = ParametroSistemaSeedData.IdUnico,
            UmbralAmbarDias = ParametroSistemaSeedData.UmbralAmbarDias,
            UmbralRojoDias = ParametroSistemaSeedData.UmbralRojoDias,
            HorasAvisoVisita = ParametroSistemaSeedData.HorasAvisoVisita,
            HorasCriticasVisita = ParametroSistemaSeedData.HorasCriticasVisita,
            HoraInicioJornada = ParametroSistemaSeedData.HoraInicioJornada,
            HoraFinJornada = ParametroSistemaSeedData.HoraFinJornada,
            HorasJornadaMensualGestor = ParametroSistemaSeedData.HorasJornadaMensualGestor,
            MedicionTiempoActiva = ParametroSistemaSeedData.MedicionTiempoActiva,
            SegundosInactividadPausa = ParametroSistemaSeedData.SegundosInactividadPausa,
            ExcluirFueraDeJornadaEnMetricas = ParametroSistemaSeedData.ExcluirFueraDeJornadaEnMetricas,
            // Un tenant nuevo siembra su propia fila al aprovisionarse (ver
            // docs/MULTITENANCY.md § 7) — esta es la del tenant #1.
            TenantId = TenantSeedData.IdPorDefecto
        });
    }
}
