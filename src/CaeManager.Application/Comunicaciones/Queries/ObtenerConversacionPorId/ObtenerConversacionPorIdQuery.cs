using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using CaeManager.Application.Comunicaciones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;

public record ObtenerConversacionPorIdQuery(Guid Id) : IRequest<ConversacionDetalleDto?>;

/// <summary>
/// <paramref name="CuerpoHtml"/> sale ya saneado por
/// <see cref="ISanitizadorHtmlService"/> — es la única forma en que el cuerpo
/// de un mensaje abandona la capa de aplicación, y por eso puede renderizarse
/// como <c>MarkupString</c>. Quien añada otra ruta de lectura del cuerpo tiene
/// que sanear igual (hallazgo N-1 de INFORME-AUDITORIA-2.md).
/// </summary>
public record AdjuntoDetalleDto(Guid Id, string NombreArchivo, string TipoContenido, long TamanoBytes);

public record MensajeDetalleDto(
    Guid Id, DireccionMensaje Direccion, string RemitenteEmail, string CuerpoHtml, DateTime FechaUtc,
    IReadOnlyList<AdjuntoDetalleDto> Adjuntos);

public record ParticipanteDetalleDto(
    Guid Id, string Email, RolParticipante Rol, TipoParticipanteOrigen TipoOrigen, Guid? EntidadRelacionadaId);

public record ConversacionDetalleDto(
    Guid Id,
    Guid? ClienteId,
    string? ClienteRazonSocial,
    string Asunto,
    EstadoConversacion Estado,
    Guid? EjecutivoAsignadoId,
    string? Etiquetas,
    DateTime FechaUltimoMensajeUtc,
    IReadOnlyList<MensajeDetalleDto> Mensajes,
    IReadOnlyList<ParticipanteDetalleDto> Participantes);

public class ObtenerConversacionPorIdQueryHandler(
    IClientesQueryContext clientesContext, IComunicacionesQueryContext comunicacionesContext, IAlcanceDatosService alcanceDatos, ISanitizadorHtmlService sanitizadorHtml,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerConversacionPorIdQuery, ConversacionDetalleDto?>
{
    // Ver ObtenerConversacionesQueryHandler: mismo literal, mismo motivo.
    private const string RolCliente = "Cliente";

    public async Task<ConversacionDetalleDto?> Handle(ObtenerConversacionPorIdQuery request, CancellationToken cancellationToken)
    {
        var conversacion = await (
            from c in comunicacionesContext.ConversacionesCorreo
            join cliente in clientesContext.Clientes on c.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            where c.Id == request.Id
            select new
            {
                c.Id,
                c.ClienteId,
                ClienteRazonSocial = cliente != null ? cliente.RazonSocial : null,
                c.Asunto,
                c.Estado,
                c.EjecutivoAsignadoId,
                c.Etiquetas,
                c.FechaUltimoMensajeUtc
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacion is null) return null;

        if (conversacion.ClienteId is not null)
        {
            if (!await alcanceDatos.ClienteVisibleAsync(conversacion.ClienteId.Value, cancellationToken))
                return null;
        }
        // Un hilo sin cliente resuelto es de la cola de triage: mismo criterio
        // que la lista (ObtenerConversacionesQueryHandler), porque si no,
        // acotar solo el listado dejaba el hilo accesible a quien tuviera el
        // Guid.
        else if (await currentUserService.ObtenerRolActualAsync() == RolCliente)
        {
            return null;
        }

        // Se materializa antes de proyectar al DTO porque el saneado corre en
        // memoria: no hay forma de traducir el sanitizador a SQL.
        var mensajesCrudos = await comunicacionesContext.MensajesCorreo
            .Where(m => m.ConversacionCorreoId == request.Id)
            .OrderBy(m => m.FechaUtc)
            .Select(m => new { m.Id, m.Direccion, m.RemitenteEmail, m.CuerpoHtml, m.FechaUtc })
            .ToListAsync(cancellationToken);

        var adjuntosPorMensaje = (await comunicacionesContext.AdjuntosMensajeCorreo
            .Where(a => mensajesCrudos.Select(m => m.Id).Contains(a.MensajeCorreoId))
            .Select(a => new { a.MensajeCorreoId, a.Id, a.NombreArchivo, a.TipoContenido, a.TamanoBytes })
            .ToListAsync(cancellationToken))
            .GroupBy(a => a.MensajeCorreoId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AdjuntoDetalleDto(a.Id, a.NombreArchivo, a.TipoContenido, a.TamanoBytes)).ToList());

        var mensajes = mensajesCrudos
            .Select(m => new MensajeDetalleDto(
                m.Id, m.Direccion, m.RemitenteEmail, sanitizadorHtml.Sanear(m.CuerpoHtml), m.FechaUtc,
                adjuntosPorMensaje.GetValueOrDefault(m.Id, [])))
            .ToList();

        var participantes = await comunicacionesContext.ParticipantesConversacion
            .Where(p => p.ConversacionCorreoId == request.Id)
            .Select(p => new ParticipanteDetalleDto(p.Id, p.Email, p.Rol, p.TipoOrigen, p.EntidadRelacionadaId))
            .ToListAsync(cancellationToken);

        return new ConversacionDetalleDto(
            conversacion.Id, conversacion.ClienteId, conversacion.ClienteRazonSocial, conversacion.Asunto,
            conversacion.Estado, conversacion.EjecutivoAsignadoId, conversacion.Etiquetas, conversacion.FechaUltimoMensajeUtc,
            mensajes, participantes);
    }
}
