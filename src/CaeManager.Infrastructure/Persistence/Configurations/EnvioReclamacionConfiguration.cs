using CaeManager.Domain.Reclamaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EnvioReclamacionConfiguration : IEntityTypeConfiguration<EnvioReclamacion>
{
    public void Configure(EntityTypeBuilder<EnvioReclamacion> builder)
    {
        builder.ToTable("EnviosReclamacion");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Huella).IsRequired().HasMaxLength(EnvioReclamacion.LongitudHuella);
        builder.Property(e => e.UsuarioId).IsRequired();
        builder.Property(e => e.IniciadoEnUtc).IsRequired();

        builder.Property<List<string>>("_entregados")
            .HasColumnName("Entregados")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(e => e.Entregados);

        builder.Property<List<string>>("_entregadosEnCopia")
            .HasColumnName("EntregadosEnCopia")
            .HasColumnType("text[]")
            .IsRequired();
        builder.Ignore(e => e.EntregadosEnCopia);

        builder.Ignore(e => e.EstaRegistrado);

        // Sin FK a ReclamacionesDocumentales ni a Conversaciones: es un registro técnico del envío, no un ancla de
        // negocio; la reclamación registrada se lee siempre desde su propia tabla.
        //
        // La consulta del reintento (EnvioReclamacionRepository.ObtenerPendienteAsync).
        builder.HasIndex(e => new { e.TenantId, e.UsuarioId, e.Huella });

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
