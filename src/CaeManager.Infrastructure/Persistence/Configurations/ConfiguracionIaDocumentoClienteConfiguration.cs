using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConfiguracionIaDocumentoClienteConfiguration : IEntityTypeConfiguration<ConfiguracionIaDocumentoCliente>
{
    public void Configure(EntityTypeBuilder<ConfiguracionIaDocumentoCliente> builder)
    {
        builder.ToTable("ConfiguracionesIaDocumentoCliente");
        builder.HasKey(c => c.Id);

        builder.HasIndex(c => new { c.TenantId, c.ClienteId, c.TipoDocumentoId }).IsUnique();
    }
}
