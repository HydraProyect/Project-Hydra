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

        // El Id lo asigna el constructor de Entity, no la base, y aquí hay que
        // declararlo: Mensaje se descubre por la navegación Conversacion.Mensajes
        // y a la vez es principal de FKs compuestas con TenantId (AdjuntoMensaje,
        // AlertaCriticaConversacion, IntencionConversacion). Con el valor generado
        // por defecto, EF clasifica como Modified el mensaje nuevo de un hilo
        // cargado, el fixup le copia el TenantId del hilo y DetectChanges lanza
        // «is part of a key» antes de que TenantSelladoInterceptor lo reclasifique.
        // Con ValueGeneratedNever entra como Added. Lo vigila
        // ClavesForaneasCompuestasConTenantTests.
        builder.Property(m => m.Id).ValueGeneratedNever();

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
        //    Conversacion). Añadir ConexionIntegracionId aquí exigiría
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
        // patrón que Conversacion.Mensajes/Participantes, con FK compuesta.
        builder.HasMany(m => m.Adjuntos)
            .WithOne()
            .HasForeignKey(a => new { a.TenantId, a.MensajeId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(m => m.Adjuntos).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Filtro global de tenant centralizado en CaeManagerDbContext.OnModelCreating.
    }
}
