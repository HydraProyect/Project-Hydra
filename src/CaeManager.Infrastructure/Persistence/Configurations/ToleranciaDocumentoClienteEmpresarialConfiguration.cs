using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ToleranciaDocumentoClienteEmpresarialConfiguration : IEntityTypeConfiguration<ToleranciaDocumentoClienteEmpresarial>
{
    public void Configure(EntityTypeBuilder<ToleranciaDocumentoClienteEmpresarial> builder)
    {
        builder.ToTable("ToleranciasDocumentoClienteEmpresarial", t =>
            t.HasCheckConstraint(
                "CK_ToleranciasDocumentoClienteEmpresarial_ToleranciaDias",
                $"\"ToleranciaDias\" >= 0 AND \"ToleranciaDias\" <= {TipoDocumentoCentro.ToleranciaMaximaDias}"));
        builder.HasKey(t => t.Id);

        builder.HasIndex(t => new { t.TenantId, t.ClienteEmpresarialId, t.TipoDocumentoId }).IsUnique();
    }
}
