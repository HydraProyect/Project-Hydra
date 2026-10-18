using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class IntencionConversacionConfiguration : IEntityTypeConfiguration<IntencionConversacion>
{
    public void Configure(EntityTypeBuilder<IntencionConversacion> builder)
    {
        builder.ToTable("IntencionesConversacion");
        builder.HasKey(i => i.Id);
        builder.HasIndex(i => new { i.TenantId, i.ConversacionId }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.IntencionActual });
        builder.HasOne<Conversacion>().WithMany().HasForeignKey(i => i.ConversacionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Mensaje>().WithMany().HasForeignKey(i => i.MensajeId).OnDelete(DeleteBehavior.Cascade);
        builder.Ignore(i => i.CuentaParaMetricas);
        builder.Ignore(i => i.CambiadaPorPersona);
    }
}
