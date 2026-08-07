using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
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

/// <summary>Solo se expone mientras está pendiente (Resuelta = false) — ver ObtenerConversacionPorIdQueryHandler.</summary>
public record SugerenciaVisitaDetalleDto(
    Guid Id, Guid? CentroId, string? CentroNombre, DateOnly? FechaInicioSugerida, DateOnly? FechaFinSugerida, string Resumen);

/// <summary>Solo se expone mientras está pendiente (Resuelta = false) — mismo criterio que SugerenciaVisitaDetalleDto.</summary>
public record SugerenciaGestionDetalleDto(
    Guid Id, Guid? TrabajadorId, string? TrabajadorNombre, Guid? TipoDocumentoId, string? TipoDocumentoNombre, string Resumen);

public record MensajeDetalleDto(
    Guid Id, DireccionMensaje Direccion, CanalConversacion Canal, string Remitente, string CuerpoHtml, DateTime FechaUtc,
    IReadOnlyList<AdjuntoDetalleDto> Adjuntos, SugerenciaVisitaDetalleDto? SugerenciaVisita,
    SugerenciaGestionDetalleDto? SugerenciaGestion, EstadoEntregaMensaje? EstadoEntrega = null, string? ErrorEntrega = null);

public record ParticipanteDetalleDto(
    Guid Id, string Email, RolParticipante Rol, TipoParticipanteOrigen TipoOrigen, Guid? EntidadRelacionadaId);

/// <summary>
/// Entrada "Sistema/Evento" del Unified Timeline (docs/COMUNICACIONES.md
/// § 12.3/§ 16.7). <paramref name="Descripcion"/> se calcula aquí, en el
/// momento de leer — EventoConversacion no la guarda para no quedar
/// desfasada si la entidad referenciada cambia después.
/// </summary>
public record EventoDetalleDto(Guid Id, TipoEventoConversacion Tipo, Guid ReferenciaId, DateTime FechaUtc, string Descripcion);

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
    IReadOnlyList<ParticipanteDetalleDto> Participantes,
    IReadOnlyList<EventoDetalleDto> Eventos,
    CanalConversacion Canal = CanalConversacion.Correo,
    string? TelefonoContacto = null,
    DateTime? FechaUltimoMensajeEntranteUtc = null);

public class ObtenerConversacionPorIdQueryHandler(
    IClientesQueryContext clientesContext, ICentrosQueryContext centrosContext, IComunicacionesQueryContext comunicacionesContext,
    ITrabajadoresQueryContext trabajadoresContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IVisitasQueryContext visitasContext,
    IAlcanceDatosService alcanceDatos, ISanitizadorHtmlService sanitizadorHtml,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerConversacionPorIdQuery, ConversacionDetalleDto?>
{
    // Ver ObtenerConversacionesQueryHandler: mismo literal, mismo motivo.
    private const string RolCliente = "Cliente";

    public async Task<ConversacionDetalleDto?> Handle(ObtenerConversacionPorIdQuery request, CancellationToken cancellationToken)
    {
        var conversacion = await (
            from c in comunicacionesContext.Conversaciones
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
                c.FechaUltimoMensajeUtc,
                c.Canal,
                c.TelefonoContacto,
                c.FechaUltimoMensajeEntranteUtc
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
        var mensajesCrudos = await comunicacionesContext.Mensajes
            .Where(m => m.ConversacionId == request.Id)
            .OrderBy(m => m.FechaUtc)
            .Select(m => new { m.Id, m.Direccion, m.Canal, m.Remitente, m.CuerpoHtml, m.FechaUtc, m.EstadoEntrega, m.ErrorEntrega })
            .ToListAsync(cancellationToken);

        var adjuntosPorMensaje = (await comunicacionesContext.AdjuntosMensaje
            .Where(a => mensajesCrudos.Select(m => m.Id).Contains(a.MensajeId))
            .Select(a => new { a.MensajeId, a.Id, a.NombreArchivo, a.TipoContenido, a.TamanoBytes })
            .ToListAsync(cancellationToken))
            .GroupBy(a => a.MensajeId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AdjuntoDetalleDto(a.Id, a.NombreArchivo, a.TipoContenido, a.TamanoBytes)).ToList());

        var sugerenciasPendientes = await comunicacionesContext.SugerenciasVisitaCorreo
            .Where(s => mensajesCrudos.Select(m => m.Id).Contains(s.MensajeId) && !s.Resuelta)
            .ToListAsync(cancellationToken);

        var centroIdsSugeridos = sugerenciasPendientes.Where(s => s.CentroId is not null).Select(s => s.CentroId!.Value).ToList();
        var nombresCentro = await centrosContext.Centros
            .Where(c => centroIdsSugeridos.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToDictionaryAsync(c => c.Id, c => c.Nombre, cancellationToken);

        var sugerenciasPorMensaje = sugerenciasPendientes.ToDictionary(
            s => s.MensajeId,
            s => new SugerenciaVisitaDetalleDto(
                s.Id, s.CentroId, s.CentroId is not null ? nombresCentro.GetValueOrDefault(s.CentroId.Value) : null,
                s.FechaInicioSugerida, s.FechaFinSugerida, s.Resumen));

        var sugerenciasGestionPendientes = await comunicacionesContext.SugerenciasGestionCorreo
            .Where(s => mensajesCrudos.Select(m => m.Id).Contains(s.MensajeId) && !s.Resuelta)
            .ToListAsync(cancellationToken);

        var trabajadorIdsSugeridos = sugerenciasGestionPendientes.Where(s => s.TrabajadorId is not null).Select(s => s.TrabajadorId!.Value).ToList();
        var nombresTrabajador = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIdsSugeridos.Contains(t.Id))
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var tipoDocumentoIdsSugeridos = sugerenciasGestionPendientes.Where(s => s.TipoDocumentoId is not null).Select(s => s.TipoDocumentoId!.Value).ToList();
        var nombresTipoDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoDocumentoIdsSugeridos.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var sugerenciasGestionPorMensaje = sugerenciasGestionPendientes.ToDictionary(
            s => s.MensajeId,
            s => new SugerenciaGestionDetalleDto(
                s.Id, s.TrabajadorId, s.TrabajadorId is not null ? nombresTrabajador.GetValueOrDefault(s.TrabajadorId.Value) : null,
                s.TipoDocumentoId, s.TipoDocumentoId is not null ? nombresTipoDocumento.GetValueOrDefault(s.TipoDocumentoId.Value) : null,
                s.Resumen));

        var mensajes = mensajesCrudos
            .Select(m => new MensajeDetalleDto(
                m.Id, m.Direccion, m.Canal, m.Remitente, sanitizadorHtml.Sanear(m.CuerpoHtml), m.FechaUtc,
                adjuntosPorMensaje.GetValueOrDefault(m.Id, []), sugerenciasPorMensaje.GetValueOrDefault(m.Id),
                sugerenciasGestionPorMensaje.GetValueOrDefault(m.Id), m.EstadoEntrega, m.ErrorEntrega))
            .ToList();

        var participantes = await comunicacionesContext.ParticipantesConversacion
            .Where(p => p.ConversacionId == request.Id)
            .Select(p => new ParticipanteDetalleDto(p.Id, p.Email, p.Rol, p.TipoOrigen, p.EntidadRelacionadaId))
            .ToListAsync(cancellationToken);

        var eventos = await ObtenerEventosAsync(request.Id, cancellationToken);

        return new ConversacionDetalleDto(
            conversacion.Id, conversacion.ClienteId, conversacion.ClienteRazonSocial, conversacion.Asunto,
            conversacion.Estado, conversacion.EjecutivoAsignadoId, conversacion.Etiquetas, conversacion.FechaUltimoMensajeUtc,
            mensajes, participantes, eventos, conversacion.Canal, conversacion.TelefonoContacto, conversacion.FechaUltimoMensajeEntranteUtc);
    }

    /// <summary>
    /// Solo <see cref="TipoEventoConversacion.VisitaCreada"/> existe hoy — el
    /// switch crece cuando el flujo de "Actualizar documentación" (§ 16.7)
    /// publique su propio tipo, no antes.
    /// </summary>
    private async Task<IReadOnlyList<EventoDetalleDto>> ObtenerEventosAsync(Guid conversacionId, CancellationToken cancellationToken)
    {
        var eventosCrudos = await comunicacionesContext.EventosConversacion
            .Where(e => e.ConversacionId == conversacionId)
            .Select(e => new { e.Id, e.Tipo, e.ReferenciaId, e.FechaUtc })
            .ToListAsync(cancellationToken);

        if (eventosCrudos.Count == 0) return [];

        var visitaIds = eventosCrudos.Where(e => e.Tipo == TipoEventoConversacion.VisitaCreada).Select(e => e.ReferenciaId).ToList();
        var visitas = await visitasContext.Visitas
            .Where(v => visitaIds.Contains(v.Id))
            .Select(v => new VisitaResumenParaEvento(v.Id, v.CentroId, v.FechaInicio, v.FechaFin))
            .ToDictionaryAsync(v => v.Id, cancellationToken);

        var centroIds = visitas.Values.Select(v => v.CentroId).Distinct().ToList();
        var nombresCentroVisita = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre })
            .ToDictionaryAsync(c => c.Id, c => c.Nombre, cancellationToken);

        return eventosCrudos.Select(e => new EventoDetalleDto(
            e.Id, e.Tipo, e.ReferenciaId, e.FechaUtc, DescribirEvento(e.Tipo, e.ReferenciaId, visitas, nombresCentroVisita))).ToList();
    }

    private record VisitaResumenParaEvento(Guid Id, Guid CentroId, DateOnly FechaInicio, DateOnly FechaFin);

    private static string DescribirEvento(
        TipoEventoConversacion tipo, Guid referenciaId,
        Dictionary<Guid, VisitaResumenParaEvento> visitasPorId, Dictionary<Guid, string> nombresCentroVisita) => tipo switch
        {
            TipoEventoConversacion.VisitaCreada when visitasPorId.TryGetValue(referenciaId, out var visita) =>
                $"Se ha creado una visita en {nombresCentroVisita.GetValueOrDefault(visita.CentroId, "el centro")} para el {FormatearRangoFechas(visita.FechaInicio, visita.FechaFin)}.",
            TipoEventoConversacion.VisitaCreada => "Se ha creado una visita a partir de esta conversación.",
            _ => "Evento del sistema."
        };

    private static string FormatearRangoFechas(DateOnly inicio, DateOnly fin) =>
        inicio == fin ? inicio.ToString("dd/MM/yyyy") : $"{inicio:dd/MM/yyyy} - {fin:dd/MM/yyyy}";
}
