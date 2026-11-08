using CaeManager.Domain.Integraciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConexionIntegracionConfiguration : IEntityTypeConfiguration<ConexionIntegracion>
{
    public void Configure(EntityTypeBuilder<ConexionIntegracion> builder)
    {
        // Un buzón compartido solo se abre con el token de aplicación de su directorio, y nunca es el buzón
        // personal de un Gestor CAE: la base de datos lo garantiza aunque una escritura salte la fábrica.
        builder.ToTable("ConexionesIntegracion", t => t.HasCheckConstraint(
            "CK_ConexionesIntegracion_BuzonCompartidoConDirectorio",
            $"NOT \"{nameof(ConexionIntegracion.EsBuzonCompartido)}\" OR " +
            $"(\"{nameof(ConexionIntegracion.DirectorioEntraId)}\" IS NOT NULL AND \"{nameof(ConexionIntegracion.GestorPropietarioId)}\" IS NULL)"));
        builder.HasKey(c => c.Id);

        builder.Property(c => c.BuzonEmail).IsRequired().HasMaxLength(ConexionIntegracion.LongitudMaximaBuzonEmail);
        builder.Property(c => c.DirectorioEntraId).HasMaxLength(36);
        builder.Property(c => c.Nombre).IsRequired().HasMaxLength(ConexionIntegracion.LongitudMaximaNombre);
        builder.Property(c => c.UltimoError).HasMaxLength(ConexionIntegracion.LongitudMaximaUltimoError);

        builder.Ignore(c => c.ResponsableEfectivoId);

        builder.HasIndex(c => new { c.TenantId, c.Nombre }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.ClienteId });

        // Un gestor solo puede tener un buzón personal conectado a la vez.
        builder.HasIndex(c => new { c.TenantId, c.GestorPropietarioId })
            .IsUnique()
            .HasFilter($"\"{nameof(ConexionIntegracion.GestorPropietarioId)}\" IS NOT NULL AND NOT \"{nameof(ConexionIntegracion.EstaEliminado)}\"");

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
