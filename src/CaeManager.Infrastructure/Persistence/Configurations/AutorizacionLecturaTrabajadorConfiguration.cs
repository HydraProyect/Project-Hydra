using CaeManager.Domain.AutorizacionesLectura;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AutorizacionLecturaTrabajadorConfiguration : IEntityTypeConfiguration<AutorizacionLecturaTrabajador>
{
    public void Configure(EntityTypeBuilder<AutorizacionLecturaTrabajador> builder)
    {
        builder.ToTable("AutorizacionesLecturaTrabajadores");
        builder.HasKey(t => t.Id);

        builder.HasIndex(t => new { t.TenantId, t.AutorizacionId, t.TrabajadorId })
            .IsUnique()
            .HasDatabaseName("IX_AutorizacionesLecturaTrabajadores_Unico");
        builder.HasIndex(t => new { t.TenantId, t.TrabajadorId })
            .HasDatabaseName("IX_AutorizacionesLecturaTrabajadores_Trabajador");

        // Compuestas con TenantId, como ProyectoTecnico: la autorización y el Trabajador son siempre del mismo Tenant,
        // y un Id de otro Tenant es irrepresentable en la base, no solo «no encontrado» en el comando.
        builder.HasOne<AutorizacionLecturaDocumental>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.AutorizacionId })
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Trabajador>().WithMany()
            .HasForeignKey(t => new { t.TenantId, t.TrabajadorId })
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
