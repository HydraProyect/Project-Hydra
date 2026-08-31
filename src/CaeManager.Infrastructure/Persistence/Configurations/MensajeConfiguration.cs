using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace CaeManager.Infrastructure.Persistence.Configurations;

public class MensajeConfiguration : IEntityTypeConfiguration<Mensaje>
{
    public void Configure(EntityTypeBuilder<Mensaje> builder)
    {
        builder.ToTable("Mensajes");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Remitente).IsRequired().HasMaxLength(320);
        // Sin HasMaxLength a propósito, a diferencia de los demás campos
        // acotados de esta entidad: forzar aquí un varchar(N) generaría una
        // migración que Postgres podría rechazar si ya existe en producción
        // alguna fila más larga que el nuevo tope (que este cambio introduce
        // solo hacia delante — ver Mensaje.LongitudMaximaCuerpoHtml, aplicado
        // en el constructor). La columna sigue siendo "text"; el límite real
        // lo impone el dominio, no el esquema.
        builder.Property(m => m.CuerpoHtml).IsRequired();
        builder.Property(m => m.MensajeExternoId).HasMaxLength(Mensaje.LongitudMaximaMensajeExternoId);
        builder.Property(m => m.ErrorEntrega).HasMaxLength(Mensaje.LongitudMaximaErrorEntrega);

        builder.HasIndex(m => m.ConversacionId);
        builder.HasIndex(m => m.FechaUtc);
        // Único por tenant: idempotencia ante reintentos de notificación de webhook (P3-33).
        //
        // Decisión (auditoría módulo 6): NO se compone con ConexionIntegracionId,
        // a diferencia de Conversaciones.HiloExternoId (ConversacionConfiguration).
        // Motivo doble:
        // 1) Mensaje no tiene FK directa a ConexionIntegracion (cuelga de
        //    Conversacion) y su FK a Conversacion es deliberadamente NO
        //    compuesta con TenantId (ver el comentario de
        //    ConversacionConfiguration.HasMany(c => c.Mensajes) — componerla
        //    rompe TenantSelladoInterceptor, reproducido y revertido en la
        //    auditoría Módulo 8). Añadir ConexionIntegracionId aquí exigiría
        //    denormalizar una columna nueva solo para este índice.
        // 2) A diferencia de HiloExternoId (conversationId, que Microsoft
        //    documenta como COMPARTIDO a propósito entre los buzones
        //    participantes de un mismo hilo), el Id inmutable de un Message
        //    de Graph identifica un único elemento en el almacén de UN
        //    buzón — no hay un comportamiento de Microsoft documentado que
        //    haga plausible la reutilización del mismo Id entre dos buzones
        //    distintos del mismo tenant (ni con wamid de WhatsApp, ligado a
        //    la numeración interna de Meta). El riesgo de colisión real es
        //    despreciable frente al coste de la migración.
        // El aislamiento por conexión que sí hace falta (dos buzones del
        // mismo tenant no deben deduplicarse ni resolverse el uno contra el
        // Mensaje del otro) se aplica en la CONSULTA, no en el índice — ver
        // IConversacionRepository.ExisteMensajeExternoAsync/ObtenerMensajePorExternoIdAsync.
        builder.HasIndex(m => new { m.TenantId, m.MensajeExternoId }).IsUnique();

        // Colección de solo lectura respaldada por campo privado — mismo
        // patrón que Conversacion.Mensajes/Participantes. FK de EF de una
        // sola columna, no compuesta con TenantId — ver el comentario de
        // ConversacionConfiguration sobre por qué (navegación de colección
        // real + TenantSelladoInterceptor, auditoría Módulo 8). La FK
        // compuesta (MensajeId, TenantId) → Mensajes (Id, TenantId) para
        // AdjuntosMensaje vive en SQL crudo en la migración
        // FkCompuestaTenantComunicaciones, fuera de este modelo Fluent.
        builder.HasMany(m => m.Adjuntos)
            .WithOne()
            .HasForeignKey(a => a.MensajeId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Adjuntos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
