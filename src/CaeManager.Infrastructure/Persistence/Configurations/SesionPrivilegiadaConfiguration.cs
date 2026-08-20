using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class SesionPrivilegiadaConfiguration : IEntityTypeConfiguration<SesionPrivilegiada>
{
    public void Configure(EntityTypeBuilder<SesionPrivilegiada> builder)
    {
        builder.ToTable("SesionesPrivilegiadas");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.ConcesionPrivilegioId).IsRequired();
        builder.Property(s => s.TenantObjetivoId).IsRequired();
        builder.Property(s => s.Motivo).IsRequired().HasMaxLength(SesionPrivilegiada.LongitudMaximaMotivo);
        builder.Property(s => s.Ticket).HasMaxLength(SesionPrivilegiada.LongitudMaximaTicket);
        builder.Property(s => s.InicioEnUtc).IsRequired();
        builder.Property(s => s.ExpiraEnUtc).IsRequired();

        builder.HasOne<ConcesionPrivilegio>().WithMany()
            .HasForeignKey(s => s.ConcesionPrivilegioId)
            .OnDelete(DeleteBehavior.Restrict);

        // "¿Qué se hizo en MI tenant, y cuándo?" — es la pregunta que un
        // cliente tiene derecho a hacer sobre sus propios datos, y la que
        // justifica que esta tabla exista.
        builder.HasIndex(s => new { s.TenantObjetivoId, s.InicioEnUtc });

        // Las sesiones todavía abiertas: lo que consulta el barrido de
        // caducidad y lo que hay que poder enseñar en cualquier momento.
        builder.HasIndex(s => s.ExpiraEnUtc)
            .HasFilter($"\"{nameof(SesionPrivilegiada.CerradaEnUtc)}\" IS NULL")
            .HasDatabaseName("IX_SesionesPrivilegiadas_Abiertas");

        // Sin HasQueryFilter: catálogo global, igual que su concesión.
    }
}
