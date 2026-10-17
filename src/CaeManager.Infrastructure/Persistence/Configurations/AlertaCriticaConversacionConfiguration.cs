using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class AlertaCriticaConversacionConfiguration : IEntityTypeConfiguration<AlertaCriticaConversacion>
{
    public void Configure(EntityTypeBuilder<AlertaCriticaConversacion> builder)
    {
        builder.ToTable("AlertasCriticasConversacion");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.TextoDisparador).HasMaxLength(4000).IsRequired();
        builder.Property(a => a.ExpresionDetectada).HasMaxLength(200).IsRequired();
        builder.HasIndex(a => new { a.TenantId, a.ConversacionId }).IsUnique();
        // FK compuesta física en la migración, igual que las notas internas.
        // El filtro global de Tenant y el token IVersionable se aplican centralmente.
    }
}
