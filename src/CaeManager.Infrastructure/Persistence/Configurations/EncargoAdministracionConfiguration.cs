using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EncargoAdministracionConfiguration : IEntityTypeConfiguration<EncargoAdministracion>
{
    /// <summary>Nombre del índice único de encargos sin retirar: el Command traduce su 23505 a «ya hay uno».</summary>
    public const string IndiceVigentePorOperacion = "IX_EncargosAdministracion_VigentePorOperacion";

    public void Configure(EntityTypeBuilder<EncargoAdministracion> builder)
    {
        builder.ToTable("EncargosAdministracion");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PropietarioTenantId).IsRequired();
        builder.Property(e => e.OperadorTenantId).IsRequired();
        builder.Property(e => e.AsignacionOperacionId).IsRequired();
        builder.Property(e => e.ClausulaContrato).IsRequired().HasMaxLength(EncargoAdministracion.LongitudMaximaClausula);
        builder.Property(e => e.VersionTexto).IsRequired().HasMaxLength(EncargoAdministracion.LongitudMaximaVersionTexto);
        builder.Property(e => e.Origen).IsRequired().HasConversion<string>().HasMaxLength(40);
        builder.Property(e => e.RegistradoPorUsuarioId).IsRequired();
        builder.Property(e => e.RegistradoEnUtc).IsRequired();
        builder.Property(e => e.VigenciaDesde).IsRequired();

        // «¿Qué encargos tiene este Tenant propietario?» y «¿cuáles ha recibido
        // este Operador CAE?»: las dos posiciones desde las que se lee.
        builder.HasIndex(e => e.PropietarioTenantId);
        builder.HasIndex(e => e.OperadorTenantId);

        // Un solo encargo sin retirar por operación. Sin él, dos registros
        // simultáneos dejarían dos cláusulas vivas para el mismo techo y
        // retirar una no bajaría nada.
        builder.HasIndex(e => e.AsignacionOperacionId, IndiceVigentePorOperacion)
            .IsUnique()
            .HasFilter($"\"{nameof(EncargoAdministracion.RetiradoEnUtc)}\" IS NULL");

        // Misma FK compuesta que la cartera y la solicitud: un encargo cuyo
        // Tenant propietario no sea el de su operación no se puede ni escribir.
        builder.HasOne<AsignacionOperacion>().WithMany()
            .HasForeignKey(e => new { e.AsignacionOperacionId, e.PropietarioTenantId })
            .HasPrincipalKey(o => new { o.Id, o.PropietarioTenantId })
            .OnDelete(DeleteBehavior.Restrict);

        // Sin HasQueryFilter: catálogo como las asignaciones. Lo acota la
        // política RLS posicion_en_el_encargo (migración EncargosAdministracion).
    }
}
