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

        // FK de EF de una sola columna, igual que Conversacion → Mensajes: la
        // FK compuesta (TareaAsistenteId, TenantId) → TareasAsistente (Id,
        // TenantId) la crea en SQL la migración AgregarTareasAsistente, contra
        // la clave alternativa AK_TareasAsistente_Id_TenantId, invisible al
        // modelo Fluent para no disparar el fixup del ChangeTracker antes de
        // que TenantSelladoInterceptor selle el tenant.
        builder.HasMany(t => t.Turnos)
            .WithOne()
            .HasForeignKey(u => u.TareaAsistenteId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Turnos).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(t => t.Pasos)
            .WithOne()
            .HasForeignKey(p => p.TareaAsistenteId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(t => t.Pasos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
