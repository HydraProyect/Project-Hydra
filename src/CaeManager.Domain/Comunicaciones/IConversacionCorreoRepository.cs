namespace CaeManager.Domain.Comunicaciones;

public interface IConversacionCorreoRepository
{
    /// <summary>Incluye Mensajes y Participantes — es lo que necesita la pantalla de detalle/respuesta.</summary>
    Task<ConversacionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(ConversacionCorreo conversacion);
}
