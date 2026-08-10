using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Clientes;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
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
    Guid Id, Guid? CentroId, string? CentroNombre, DateOnly? FechaInicioSugerida, DateOnly? FechaFinSugerida, string Resumen,
    int Confianza, int ConfianzaCentro, int ConfianzaFechas);

/// <summary>
/// Un ítem (DetalleSugerenciaGestionCorreo) todavía sin resolver — mismo criterio de "solo pendiente"
/// que SugerenciaVisitaDetalleDto. <paramref name="Id"/> es el ítem, no la sugerencia completa: un
/// mensaje puede tener varios (notificación en bloque, ronda de reducción de ruido en
/// Comunicaciones) — <paramref name="Resumen"/>/<paramref name="Confianza"/> son del mensaje
/// completo y se repiten en cada ítem que le pertenece.
/// </summary>
public record SugerenciaGestionDetalleDto(
    Guid Id, Guid? TrabajadorId, string? TrabajadorNombre, Guid? TipoDocumentoId, string? TipoDocumentoNombre, string Resumen,
    int Confianza, int ConfianzaTrabajador, int ConfianzaTipoDocumento);

/// <summary>
/// <paramref name="MotivoRuido"/>/<paramref name="RuidoConfirmadoManualmente"/> vienen de
/// <see cref="ClasificacionRuidoMensaje"/> (ronda de reducción de ruido en Comunicaciones).
/// <paramref name="TieneSoloRepeticionesPendientes"/> es distinto: true cuando el mensaje tiene
/// ítems de gestión pendientes (<see cref="SugerenciasGestion"/> no vacío) y TODOS están marcados
/// como repetición de un pendiente ya reclamado (<c>ClasificacionRuidoDetalleGestion</c>) — un
/// mensaje con un ítem repetido y uno genuinamente nuevo no cuenta como ruido, solo cuando no queda
/// nada nuevo que atender.
/// </summary>
public record MensajeDetalleDto(
    Guid Id, DireccionMensaje Direccion, CanalConversacion Canal, string Remitente, string CuerpoHtml, DateTime FechaUtc,
    IReadOnlyList<AdjuntoDetalleDto> Adjuntos, SugerenciaVisitaDetalleDto? SugerenciaVisita,
    IReadOnlyList<SugerenciaGestionDetalleDto> SugerenciasGestion, EstadoEntregaMensaje? EstadoEntrega = null, string? ErrorEntrega = null,
    MotivoRuidoMensaje MotivoRuido = MotivoRuidoMensaje.Ninguno, bool RuidoConfirmadoManualmente = false,
    bool TieneSoloRepeticionesPendientes = false);

public record ParticipanteDetalleDto(
    Guid Id, string Email, RolParticipante Rol, TipoParticipanteOrigen TipoOrigen, Guid? EntidadRelacionadaId);

/// <summary>
/// Entrada "Sistema/Evento" del Unified Timeline (docs/COMUNICACIONES.md
/// § 12.3/§ 16.7). <paramref name="Descripcion"/> se calcula aquí, en el
/// momento de leer — EventoConversacion no la guarda para no quedar
/// desfasada si la entidad referenciada cambia después.
/// </summary>
public record EventoDetalleDto(Guid Id, TipoEventoConversacion Tipo, Guid ReferenciaId, DateTime FechaUtc, string Descripcion);

/// <summary>
/// Propuesta del Conversation Matching Engine (§ 13.2) — solo se calcula
/// para conversaciones WhatsApp abiertas/pendientes con Cliente resuelto (ver
/// ObtenerConversacionPorIdQueryHandler). No se persiste nada: se recalcula
/// en cada lectura, y confirmarla es VincularConversacionCommand.
/// </summary>
public record SugerenciaVinculacionDetalleDto(Guid ConversacionDestinoId, string AsuntoDestino, DesgloseCoincidenciaDto Desglose);

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
    DateTime? FechaUltimoMensajeEntranteUtc = null,
    SugerenciaVinculacionDetalleDto? SugerenciaVinculacion = null,
    bool EsConversacionInformativaPreCae = false,
    string? ResumenRelevanciaCae = null);

public class ObtenerConversacionPorIdQueryHandler(
    IClientesQueryContext clientesContext, ICentrosQueryContext centrosContext, IComunicacionesQueryContext comunicacionesContext,
    ITrabajadoresQueryContext trabajadoresContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IVisitasQueryContext visitasContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos, ISanitizadorHtmlService sanitizadorHtml,
    ICurrentUserService currentUserService, IMotorCoincidenciaConversacionesService motorCoincidencia)
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
                s.FechaInicioSugerida, s.FechaFinSugerida, s.Resumen, s.Confianza, s.ConfianzaCentro, s.ConfianzaFechas));

        var sugerenciasGestionPendientes = await comunicacionesContext.SugerenciasGestionCorreo
            .Where(s => mensajesCrudos.Select(m => m.Id).Contains(s.MensajeId))
            .Select(s => new { s.Id, s.MensajeId, s.Resumen, s.Confianza })
            .ToListAsync(cancellationToken);

        var sugerenciaGestionIds = sugerenciasGestionPendientes.Select(s => s.Id).ToList();
        var detallesPendientes = await comunicacionesContext.DetallesSugerenciaGestionCorreo
            .Where(d => sugerenciaGestionIds.Contains(d.SugerenciaGestionCorreoId) && !d.Resuelta)
            .ToListAsync(cancellationToken);

        var trabajadorIdsSugeridos = detallesPendientes.Where(d => d.TrabajadorId is not null).Select(d => d.TrabajadorId!.Value).ToList();
        var nombresTrabajador = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIdsSugeridos.Contains(t.Id))
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var tipoDocumentoIdsSugeridos = detallesPendientes.Where(d => d.TipoDocumentoId is not null).Select(d => d.TipoDocumentoId!.Value).ToList();
        var nombresTipoDocumento = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoDocumentoIdsSugeridos.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var sugerenciasGestionPorMensaje = sugerenciasGestionPendientes
            .Join(detallesPendientes, s => s.Id, d => d.SugerenciaGestionCorreoId, (s, d) => new { Sugerencia = s, Detalle = d })
            .GroupBy(x => x.Sugerencia.MensajeId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<SugerenciaGestionDetalleDto>)g
                    .Select(x => new SugerenciaGestionDetalleDto(
                        x.Detalle.Id,
                        x.Detalle.TrabajadorId, x.Detalle.TrabajadorId is not null ? nombresTrabajador.GetValueOrDefault(x.Detalle.TrabajadorId.Value) : null,
                        x.Detalle.TipoDocumentoId, x.Detalle.TipoDocumentoId is not null ? nombresTipoDocumento.GetValueOrDefault(x.Detalle.TipoDocumentoId.Value) : null,
                        x.Sugerencia.Resumen, x.Sugerencia.Confianza, x.Detalle.ConfianzaTrabajador, x.Detalle.ConfianzaTipoDocumento))
                    .ToList());

        var mensajeIds = mensajesCrudos.Select(m => m.Id).ToList();

        var clasificacionesMensaje = await comunicacionesContext.ClasificacionesRuidoMensaje
            .Where(c => mensajeIds.Contains(c.MensajeId))
            .Select(c => new { c.MensajeId, c.Motivo, c.ConfirmadaManualmente })
            .ToDictionaryAsync(c => c.MensajeId, cancellationToken);

        // Ronda de reducción de ruido en Comunicaciones: un mensaje tiene "solo repeticiones
        // pendientes" cuando ninguno de sus ítems de gestión pendientes es genuinamente nuevo — se
        // reutiliza el mismo join sugerenciasGestionPendientes/detallesPendientes que ya construye
        // sugerenciasGestionPorMensaje, no una consulta aparte.
        var detalleIdsPendientes = detallesPendientes.Select(d => d.Id).ToList();
        var detalleIdsRepeticion = (await comunicacionesContext.ClasificacionesRuidoDetalleGestion
            .Where(c => detalleIdsPendientes.Contains(c.DetalleSugerenciaGestionCorreoId) && !c.ConfirmadaManualmente)
            .Select(c => c.DetalleSugerenciaGestionCorreoId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var soloRepeticionesPorMensaje = sugerenciasGestionPendientes
            .Join(detallesPendientes, s => s.Id, d => d.SugerenciaGestionCorreoId, (s, d) => new { s.MensajeId, DetalleId = d.Id })
            .GroupBy(x => x.MensajeId)
            .ToDictionary(g => g.Key, g => g.All(x => detalleIdsRepeticion.Contains(x.DetalleId)));

        var mensajes = mensajesCrudos
            .Select(m =>
            {
                var clasificacion = clasificacionesMensaje.GetValueOrDefault(m.Id);
                return new MensajeDetalleDto(
                    m.Id, m.Direccion, m.Canal, m.Remitente, sanitizadorHtml.Sanear(m.CuerpoHtml), m.FechaUtc,
                    adjuntosPorMensaje.GetValueOrDefault(m.Id, []), sugerenciasPorMensaje.GetValueOrDefault(m.Id),
                    sugerenciasGestionPorMensaje.GetValueOrDefault(m.Id, []), m.EstadoEntrega, m.ErrorEntrega,
                    clasificacion?.Motivo ?? MotivoRuidoMensaje.Ninguno,
                    clasificacion?.ConfirmadaManualmente ?? false,
                    soloRepeticionesPorMensaje.GetValueOrDefault(m.Id));
            })
            .ToList();

        var clasificacionRelevancia = await comunicacionesContext.ClasificacionesRelevanciaCae
            .Where(c => c.ConversacionId == request.Id)
            .Select(c => new { c.EsAccionableCae, c.Resumen })
            .FirstOrDefaultAsync(cancellationToken);
        var esConversacionInformativaPreCae = clasificacionRelevancia is { EsAccionableCae: false };

        var participantes = await comunicacionesContext.ParticipantesConversacion
            .Where(p => p.ConversacionId == request.Id)
            .Select(p => new ParticipanteDetalleDto(p.Id, p.Email, p.Rol, p.TipoOrigen, p.EntidadRelacionadaId))
            .ToListAsync(cancellationToken);

        var eventos = await ObtenerEventosAsync(request.Id, cancellationToken);
        var sugerenciaVinculacion = await ObtenerSugerenciaVinculacionAsync(
            conversacion.Id, conversacion.ClienteId, conversacion.Canal, conversacion.Estado, conversacion.FechaUltimoMensajeUtc, cancellationToken);

        return new ConversacionDetalleDto(
            conversacion.Id, conversacion.ClienteId, conversacion.ClienteRazonSocial, conversacion.Asunto,
            conversacion.Estado, conversacion.EjecutivoAsignadoId, conversacion.Etiquetas, conversacion.FechaUltimoMensajeUtc,
            mensajes, participantes, eventos, conversacion.Canal, conversacion.TelefonoContacto, conversacion.FechaUltimoMensajeEntranteUtc,
            sugerenciaVinculacion, esConversacionInformativaPreCae, esConversacionInformativaPreCae ? clasificacionRelevancia!.Resumen : null);
    }

    /// <summary>
    /// Solo tiene sentido proponer vincular un hilo WhatsApp vivo con
    /// Cliente ya resuelto (§ 13.2 exige "mismo Cliente" como primer
    /// criterio) — un hilo de correo, uno cerrado/resuelto, o uno todavía en
    /// triage no calculan nada.
    /// </summary>
    private async Task<SugerenciaVinculacionDetalleDto?> ObtenerSugerenciaVinculacionAsync(
        Guid conversacionId, Guid? clienteId, CanalConversacion canal, EstadoConversacion estado,
        DateTime fechaUltimoMensajeUtc, CancellationToken cancellationToken)
    {
        if (canal != CanalConversacion.WhatsApp || clienteId is null) return null;
        if (estado != EstadoConversacion.Abierta && estado != EstadoConversacion.Pendiente) return null;

        var candidato = await motorCoincidencia.ProponerVinculacionAsync(conversacionId, clienteId.Value, fechaUltimoMensajeUtc, cancellationToken);
        return candidato is null ? null : new SugerenciaVinculacionDetalleDto(candidato.ConversacionId, candidato.Asunto, candidato.Desglose);
    }

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

        var documentoIds = eventosCrudos.Where(e => e.Tipo == TipoEventoConversacion.DocumentoActualizado).Select(e => e.ReferenciaId).ToList();
        var documentos = await documentosContext.Documentos
            .Where(d => documentoIds.Contains(d.Id))
            .Select(d => new DocumentoResumenParaEvento(d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId))
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var tipoDocumentoIdsEventos = documentos.Values.Select(d => d.TipoDocumentoId).Distinct().ToList();
        var nombresTipoDocumentoEventos = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoDocumentoIdsEventos.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var trabajadorIdsDocumentos = documentos.Values.Where(d => d.TrabajadorId is not null).Select(d => d.TrabajadorId!.Value).ToList();
        var nombresTrabajadorDocumentos = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIdsDocumentos.Contains(t.Id))
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos })
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var empresaIdsDocumentos = documentos.Values.Where(d => d.EmpresaId is not null).Select(d => d.EmpresaId!.Value).ToList();
        var nombresEmpresaDocumentos = await empresasContext.Empresas
            .Where(e => empresaIdsDocumentos.Contains(e.Id))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        return eventosCrudos.Select(e => new EventoDetalleDto(e.Id, e.Tipo, e.ReferenciaId, e.FechaUtc, DescribirEvento(
            e.Tipo, e.ReferenciaId, visitas, nombresCentroVisita, documentos, nombresTipoDocumentoEventos,
            nombresTrabajadorDocumentos, nombresEmpresaDocumentos))).ToList();
    }

    private record VisitaResumenParaEvento(Guid Id, Guid CentroId, DateOnly FechaInicio, DateOnly FechaFin);

    private record DocumentoResumenParaEvento(Guid Id, Guid TipoDocumentoId, Guid? TrabajadorId, Guid? EmpresaId);

    private static string DescribirEvento(
        TipoEventoConversacion tipo, Guid referenciaId,
        Dictionary<Guid, VisitaResumenParaEvento> visitasPorId, Dictionary<Guid, string> nombresCentroVisita,
        Dictionary<Guid, DocumentoResumenParaEvento> documentosPorId, Dictionary<Guid, string> nombresTipoDocumento,
        Dictionary<Guid, string> nombresTrabajador, Dictionary<Guid, string> nombresEmpresa) => tipo switch
        {
            TipoEventoConversacion.VisitaCreada when visitasPorId.TryGetValue(referenciaId, out var visita) =>
                $"Se ha creado una visita en {nombresCentroVisita.GetValueOrDefault(visita.CentroId, "el centro")} para el {FormatearRangoFechas(visita.FechaInicio, visita.FechaFin)}.",
            TipoEventoConversacion.VisitaCreada => "Se ha creado una visita a partir de esta conversación.",

            TipoEventoConversacion.DocumentoActualizado when documentosPorId.TryGetValue(referenciaId, out var documento) =>
                $"Se ha actualizado el documento {nombresTipoDocumento.GetValueOrDefault(documento.TipoDocumentoId, "sin tipo")} de "
                    + (documento.TrabajadorId is { } trabajadorId
                        ? nombresTrabajador.GetValueOrDefault(trabajadorId, "un trabajador")
                        : nombresEmpresa.GetValueOrDefault(documento.EmpresaId ?? Guid.Empty, "una empresa"))
                    + ".",
            TipoEventoConversacion.DocumentoActualizado => "Se ha actualizado un documento a partir de esta conversación.",

            TipoEventoConversacion.ConversacionVinculada => "Se vinculó otra conversación a este hilo (Conversation Matching Engine).",

            _ => "Evento del sistema."
        };

    private static string FormatearRangoFechas(DateOnly inicio, DateOnly fin) =>
        inicio == fin ? inicio.ToString("dd/MM/yyyy") : $"{inicio:dd/MM/yyyy} - {fin:dd/MM/yyyy}";
}
