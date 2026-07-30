using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Participante (De/Para/Cc) de una ConversacionCorreo. Permite que un hilo
/// con, por ejemplo, el Trabajador, la Subcontrata y el Centro de una visita
/// en copia siga siendo una única conversación.
/// </summary>
public class ParticipanteConversacion : EntidadConTenant
{
    public Guid ConversacionCorreoId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public RolParticipante Rol { get; private set; }
    public TipoParticipanteOrigen TipoOrigen { get; private set; }

    /// <summary>
    /// Apunta a Trabajador/Subcontrata/Empresa/Centro según <see cref="TipoOrigen"/>.
    /// Sin FK real a propósito: es polimórfico y cada entidad relacionada
    /// vive en su propio agregado (ver ARQUITECTURA-INTEGRACIONES.md § 12.3).
    /// Null cuando TipoOrigen es Desconocido o UsuarioCliente sin resolver.
    /// </summary>
    public Guid? EntidadRelacionadaId { get; private set; }

    private ParticipanteConversacion()
    {
    }

    public ParticipanteConversacion(
        Guid conversacionCorreoId, string email, RolParticipante rol, TipoParticipanteOrigen tipoOrigen, Guid? entidadRelacionadaId = null)
    {
        if (conversacionCorreoId == Guid.Empty)
            throw new ArgumentException("El participante debe pertenecer a una conversación.", nameof(conversacionCorreoId));
        if (string.IsNullOrWhiteSpace(email))
            throw new ArgumentException("El participante debe tener un email.", nameof(email));

        ConversacionCorreoId = conversacionCorreoId;
        Email = email.Trim();
        Rol = rol;
        TipoOrigen = tipoOrigen;
        EntidadRelacionadaId = entidadRelacionadaId;
    }
}
