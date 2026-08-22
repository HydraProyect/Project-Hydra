using CaeManager.Domain.Plataforma;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class EstadoBootstrapPlataformaConfiguration : IEntityTypeConfiguration<EstadoBootstrapPlataforma>
{
    public void Configure(EntityTypeBuilder<EstadoBootstrapPlataforma> builder)
    {
        builder.ToTable("EstadoBootstrapPlataforma");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.UsuarioRaizId).IsRequired();
        builder.Property(e => e.DesignadaEnUtc).IsRequired();
        builder.Property(e => e.Consumido).IsRequired();
        builder.Property(e => e.ConsumidoEnUtc);

        builder.Property(e => e.Version).IsConcurrencyToken();

        // Fila única del despliegue. La clave primaria con Id canónico ya lo
        // garantiza —un segundo INSERT choca contra ella—, pero el CHECK deja la
        // intención escrita en el esquema en vez de en un comentario: cualquier
        // fila con otro Id es un error de programación, no un caso de negocio.
        builder.ToTable(t => t.HasCheckConstraint(
            "CK_EstadoBootstrapPlataforma_FilaUnica",
            $@"""Id"" = '{EstadoBootstrapPlataforma.ClaveCanonica}'"));
    }
}
