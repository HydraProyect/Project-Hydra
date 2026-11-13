using System.Text.RegularExpressions;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// Quién es el interlocutor de la conversación para la nota fija de la Agenda (E21): la dirección del último mensaje entrante
/// y la entidad (Cliente empresarial, Empresa, Subcontrata o Centro) cuya Agenda la contiene. La consulta
/// (<c>ObtenerNotaContactoQuery</c>) busca por correo dentro de la Agenda de esa entidad y decide el alcance; esto solo
/// resuelve a quién preguntar, y devuelve <c>null</c> si no hay de quién (WhatsApp sin correo, hilo sin ancla).
/// </summary>
public static partial class InterlocutorNotaFija
{
    public readonly record struct Interlocutor(TipoPropietarioAgenda Tipo, Guid PropietarioId, string Email);

    public static Interlocutor? De(ConversacionDetalleDto detalle)
    {
        var correo = CorreoDelUltimoEntrante(detalle);
        if (correo is null) return null;

        var participante = detalle.Participantes.FirstOrDefault(
            p => string.Equals(p.Email?.Trim(), correo, StringComparison.OrdinalIgnoreCase));

        // La entidad que la propia conversación sabe del participante manda; si no, el Cliente empresarial del hilo.
        if (participante is { EntidadRelacionadaId: { } entidadId })
        {
            var tipo = participante.TipoOrigen switch
            {
                TipoParticipanteOrigen.Empresa => (TipoPropietarioAgenda?)TipoPropietarioAgenda.Empresa,
                TipoParticipanteOrigen.Subcontrata => TipoPropietarioAgenda.Subcontrata,
                TipoParticipanteOrigen.Centro => TipoPropietarioAgenda.Centro,
                _ => null
            };
            if (tipo is { } t) return new Interlocutor(t, entidadId, correo);
        }

        return detalle.ClienteId is { } clienteId ? new Interlocutor(TipoPropietarioAgenda.Cliente, clienteId, correo) : null;
    }

    private static string? CorreoDelUltimoEntrante(ConversacionDetalleDto detalle)
    {
        var remitente = detalle.Mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante)
            .OrderByDescending(m => m.FechaUtc)
            .Select(m => m.Remitente)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(remitente)) return null;

        var coincidencia = DireccionDeCorreo().Match(remitente);
        return coincidencia.Success ? coincidencia.Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"[^\s<>""',;]+@[^\s<>""',;]+")]
    private static partial Regex DireccionDeCorreo();
}
