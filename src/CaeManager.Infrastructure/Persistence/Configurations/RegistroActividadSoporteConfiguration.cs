using CaeManager.Domain.Soporte;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class RegistroActividadSoporteConfiguration : IEntityTypeConfiguration<RegistroActividadSoporte>
{
    public void Configure(EntityTypeBuilder<RegistroActividadSoporte> builder)
    {
        builder.ToTable("RegistrosActividadSoporte", t =>
        {
            // XOR: exactamente uno de los dos agrupadores, nunca los dos ni
            // ninguno. Un registro sin agrupador no se puede atribuir a una
            // visita, y una traza que no se agrupa no responde a la pregunta
            // para la que existe. Con los dos informados, la visita se contaria
            // dos veces al leer por cualquiera de las dos ramas.
            //
            // El dominio ya lo hace irrepresentable con sus dos fabricas; esto
            // lo sostiene tambien para lo que no pase por el dominio: siembras,
            // SQL directo y migraciones futuras.
            //
            // Mismo patron que CK_RelacionesEmpresariales_* y que
            // EstadoBootstrapPlataformaConfiguration.
            t.HasCheckConstraint(
                "CK_RegistrosActividadSoporte_UnSoloAgrupador",
                "(\"DelegacionTenantId\" IS NULL) <> (\"SesionPrivilegiadaId\" IS NULL)");
        });
        builder.HasKey(r => r.Id);

        builder.Property(r => r.UsuarioSoporteId).IsRequired();
        builder.Property(r => r.OcurridaEnUtc).IsRequired();

        // Nombre y no número, mismo criterio que PropositoDelegacion.
        builder.Property(r => r.Tipo).IsRequired().HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Detalle).HasMaxLength(RegistroActividadSoporte.LongitudMaximaDetalle);

        // La consulta que importa es "enséñame todo lo que hizo soporte en
        // esta visita, en orden" — para responder a un cliente que pregunta.
        //
        // Se CONSERVA el indice por delegacion aunque la columna pase a
        // nullable: el historico anterior a B se sigue consultando por el, y
        // retirarlo dejaria esa consulta sin indice justo cuando mas filas
        // tiene. La vía nueva estrena el suyo.
        builder.HasIndex(r => new { r.DelegacionTenantId, r.OcurridaEnUtc });
        builder.HasIndex(r => new { r.SesionPrivilegiadaId, r.OcurridaEnUtc });

        // El filtro global de tenant lo aplica CaeManagerDbContext como con
        // todas las entidades con TenantId, sin excepción.
    }
}
