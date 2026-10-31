using CaeManager.Domain.AsistenteIa;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class TareaAsistenteConfiguration : IEntityTypeConfiguration<TareaAsistente>
{
    public void Configure(EntityTypeBuilder<TareaAsistente> builder)
    {
        // La base repite la regla del dominio «nada se ejecuta sin plan
        // confirmado»: una tarea confirmada o terminada lleva siempre quién y
        // cuándo confirmó.
        builder.ToTable("TareasAsistente", t => t.HasCheckConstraint(
            "CK_TareasAsistente_ConfirmadaConConfirmacion",
            "\"Estado\" NOT IN ('Confirmada', 'Terminada') OR (\"PlanConfirmadoEnUtc\" IS NOT NULL AND \"PlanConfirmadoPorActorRealUsuarioId\" IS NOT NULL)"));
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Estado).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.ViaAcceso).HasConversion<string>().HasMaxLength(32);

        // Mis tareas: la lista de la persona dentro del Tenant, las más recientes primero.
        builder.HasIndex(t => new { t.TenantId, t.ActorRealUsuarioId, t.ActualizadaEnUtc });

        // FK compuesta con TenantId, igual que Conversacion → Mensajes.
        builder.HasMany(t => t.Turnos)
            .WithOne()
            .HasForeignKey(u => new { u.TenantId, u.TareaAsistenteId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Turnos).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(t => t.Pasos)
            .WithOne()
            .HasForeignKey(p => new { p.TenantId, p.TareaAsistenteId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Pasos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
