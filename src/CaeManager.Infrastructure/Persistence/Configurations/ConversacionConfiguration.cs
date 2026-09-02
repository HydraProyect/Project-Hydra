using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class ConversacionConfiguration : IEntityTypeConfiguration<Conversacion>
{
    public void Configure(EntityTypeBuilder<Conversacion> builder)
    {
        // Como mucho un ancla, y aquí sí "como mucho": una conversación sin
        // ninguna es la cola de triage, que es un estado legítimo y frecuente
        // (§ 12.4) — a diferencia de una reclamación, que siempre tiene a
        // quién se le mandó.
        builder.ToTable("Conversaciones", t => t.HasCheckConstraint(
            "CK_Conversaciones_AnclaUnica",
            "num_nonnulls(\"ClienteId\", \"EmpresaId\") <= 1"));
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Asunto).IsRequired().HasMaxLength(Conversacion.LongitudMaximaAsunto);
        builder.Property(c => c.Etiquetas).HasMaxLength(Conversacion.LongitudMaximaEtiquetas);
        builder.Property(c => c.HiloExternoId).HasMaxLength(Conversacion.LongitudMaximaHiloExternoId);

        builder.Property(c => c.TelefonoContacto).HasMaxLength(Conversacion.LongitudMaximaTelefonoContacto);

        builder.HasIndex(c => new { c.TenantId, c.Estado });
        builder.HasIndex(c => new { c.TenantId, c.ClienteId });

        // Ancla de Empresa contraparte (hoy solo la reclamación de ámbito
        // Empresa): mismo índice que el de Cliente, porque lo recorre el mismo
        // filtro de cartera en ObtenerConversacionesQuery.
        builder.HasIndex(c => new { c.TenantId, c.EmpresaId });

        // FK compuesta con TenantId, como en DocumentoConfiguration: el ancla
        // debe apuntar a una Empresa DEL MISMO tenant, no solo a una Empresa
        // que exista. Con la columna en null Postgres no la comprueba
        // (MATCH SIMPLE), así que un hilo de triage sigue siendo válido.
        // ClienteId no la tiene y se queda como estaba: añadírsela ahora sería
        // una corrección de integridad sobre datos históricos, ajena a este
        // incremento.
        builder.HasOne<Empresa>().WithMany()
            .HasForeignKey(c => new { c.TenantId, c.EmpresaId })
            .HasPrincipalKey(e => new { e.TenantId, e.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => c.FechaUltimoMensajeUtc);
        // Único por tenant Y por conexión (auditoría módulo 6, no solo por
        // tenant P3-33): Graph puede asignar el mismo conversationId a un
        // hilo en el que participan dos buzones conectados distintos del
        // mismo tenant (documentado por Microsoft — comparten
        // conversationId los participantes de Exchange Online de la misma
        // organización). Sin ConexionIntegracionId en el índice, el segundo
        // buzón no podría tener su propia fila para ese mismo hilo.
        builder.HasIndex(c => new { c.TenantId, c.ConexionIntegracionId, c.HiloExternoId }).IsUnique();
        // Canal WhatsApp: listado del Chat y lookup de hilo por teléfono en la ingesta.
        builder.HasIndex(c => new { c.TenantId, c.Canal, c.Estado });
        builder.HasIndex(c => new { c.TenantId, c.ConexionIntegracionId, c.TelefonoContacto });

        // Mensajes/Participantes son colecciones de solo lectura respaldadas
        // por un campo privado (ver Conversacion) — se le dice a EF Core
        // que lea/escriba directamente el campo, sin pasar por la propiedad
        // pública (que no expone Add). Mismo patrón estándar de EF Core para
        // agregados DDD con colecciones encapsuladas.
        //
        // FK de EF de una sola columna, NO compuesta con TenantId: al ser
        // una navegación de colección real (HasMany, a diferencia del
        // HasOne<T>().WithMany() sin navegación que usa
        // AsignacionConfiguration/DocumentoConfiguration), componer TenantId
        // aquí como relación de EF Core chocaba con el fixup del
        // ChangeTracker antes de que TenantSelladoInterceptor sellara el
        // tenant real (auditoría Módulo 8, intentado y revertido
        // 2026-08-30). La defensa en profundidad real vive fuera de este
        // fichero: la migración FkCompuestaTenantComunicaciones añade en SQL
        // crudo la FK compuesta (ConversacionId, TenantId) → Conversaciones
        // (Id, TenantId) para Mensajes y ParticipantesConversacion —
        // deliberadamente invisible al modelo Fluent para no volver a
        // disparar el mismo fixup.
        builder.HasMany(c => c.Mensajes)
            .WithOne()
            .HasForeignKey(m => m.ConversacionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Mensajes).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasMany(c => c.Participantes)
            .WithOne()
            .HasForeignKey(p => p.ConversacionId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(c => c.Participantes).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global (soft delete + tenant) centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
