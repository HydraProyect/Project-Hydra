using CaeManager.Domain.Seguridad;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SesionUsuarioConfiguration : IEntityTypeConfiguration<SesionUsuario>
{
    public void Configure(EntityTypeBuilder<SesionUsuario> builder)
    {
        builder.ToTable("SesionesUsuario");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.UsuarioId).IsRequired();
        builder.Property(s => s.Sid).IsRequired();
        builder.Property(s => s.EmitidaEnUtc).IsRequired();

        builder.Ignore(s => s.EstaRevocada);

        // La comprobación de cada revalidación busca por Sid (app_sesion_vigente).
        builder.HasIndex(s => s.Sid).IsUnique();
        // Cerrar en todos los dispositivos: las vivas de una cuenta.
        builder.HasIndex(s => new { s.TenantId, s.UsuarioId, s.RevocadaEnUtc });
        // La purga por antigüedad recorre todos los Tenants.
        builder.HasIndex(s => s.EmitidaEnUtc);

        // Sin FK hacia AspNetUsers, como RegistroInicioSesion: el rastro sobrevive a la baja de la
        // cuenta hasta su conservación. RLS, permisos, función de vigencia y purga: migración
        // AnadeSesionesUsuario.
    }
}
