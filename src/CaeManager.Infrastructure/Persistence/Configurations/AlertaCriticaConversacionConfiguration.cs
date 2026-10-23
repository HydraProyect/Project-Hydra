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
        // Como mucho una alerta Pendiente por conversación; las ya resueltas no impiden que un caso crítico nuevo del mismo hilo alerte.
        builder.HasIndex(a => new { a.TenantId, a.ConversacionId }).IsUnique()
            .HasFilter($"\"Estado\" = {(int)EstadoAlertaCritica.Pendiente}")
            .HasDatabaseName("IX_AlertasCriticasConversacion_TenantId_ConversacionId_Pendiente");
        // Relaciones declaradas para que EF inserte la Conversación y el Mensaje antes que la alerta en la
        // misma ingesta; la FK compuesta con TenantId sigue en la migración.
        builder.HasOne<Conversacion>().WithMany().HasForeignKey(a => a.ConversacionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Mensaje>().WithMany().HasForeignKey(a => a.MensajeId).OnDelete(DeleteBehavior.Cascade);
        // El filtro global de Tenant y el token IVersionable se aplican centralmente.
    }
}
