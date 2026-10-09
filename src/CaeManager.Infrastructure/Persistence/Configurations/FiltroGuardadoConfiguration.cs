using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Domain.Configuracion;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class FiltroGuardadoConfiguration : IEntityTypeConfiguration<FiltroGuardado>
{
    public void Configure(EntityTypeBuilder<FiltroGuardado> builder)
    {
        builder.ToTable("FiltrosGuardados");
        builder.HasKey(f => f.Id);

        // Guarda la clave compuesta «pantalla@tenant» (32 hexadecimales), no el
        // nombre de la pantalla a secas: ver FiltroGuardado y
        // PantallasConFiltrosGuardados.ClaveAlmacenada. El límite es el mismo
        // que PantallasConFiltrosGuardados.LongitudMaximaDeLaClave — un test de
        // Application comprueba que toda pantalla admitida cabe.
        builder.Property(f => f.Pantalla).HasMaxLength(PantallasConFiltrosGuardados.LongitudMaximaDeLaClave).IsRequired();
        builder.Property(f => f.Nombre).HasMaxLength(100).IsRequired();
        builder.Property(f => f.ValoresJson).IsRequired();

        builder.HasIndex(f => new { f.UsuarioId, f.Pantalla });

        // Sin HasQueryFilter ni RLS de Tenant, aunque el filtro guardado SÍ es
        // de un Tenant (preferencia de un usuario dentro de un Tenant): el
        // Tenant va en la clave de Pantalla y lo compara Application, no la
        // base. Deuda declarada — falta la columna TenantId propia, con su
        // filtro y su política, que exige una migración. Hasta entonces este
        // índice (UsuarioId, Pantalla) sirve tal cual a la lectura, que iguala
        // la clave completa.
    }
}
