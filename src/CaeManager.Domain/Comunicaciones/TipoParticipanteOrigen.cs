namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Qué tipo de entidad de dominio representa un participante del hilo — ver
/// <see cref="ParticipanteConversacion.EntidadRelacionadaId"/> para el porqué
/// es polimórfico y sin FK real.
/// </summary>
public enum TipoParticipanteOrigen
{
    UsuarioCliente,
    Trabajador,
    Subcontrata,
    Empresa,
    Centro,
    Desconocido
}
