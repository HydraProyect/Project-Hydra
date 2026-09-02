using CaeManager.Application.Common;
using System.Text.RegularExpressions;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;

/// <summary>
/// Filtros de la bandeja compartida (ver ARQUITECTURA-INTEGRACIONES.md § 12.6,
/// pantalla "Bandeja"). <see cref="Anio"/>/<see cref="Mes"/> van juntos (mes
/// concreto de un año) porque la pantalla ofrece un único selector de mes, no
/// un rango de fechas libre. <see cref="SoloAsignadasAMi"/> usa
/// <see cref="ICurrentUserService"/> para resolver "a mí" — no recibe el
/// usuario como parámetro para que el Command no pueda usarse para consultar
/// la bandeja de otro usuario.
///
/// <see cref="SoloEsperandoCliente"/> es filtro de servidor, no de UI, aunque
/// "esperando cliente" sea un estado derivado y no persistido (§ 16.4): con
/// paginación en SQL, filtrar en memoria sobre la página ya cargada dejaría
/// páginas que parecen vacías cuando en realidad hay coincidencias más
/// adelante — el mismo motivo por el que <see cref="SoloAsignadasAMi"/> y
/// <see cref="SoloSinAsignar"/> ya eran filtros de servidor.
///
/// <see cref="Busqueda"/> compara Asunto Y el cuerpo de los mensajes del hilo
/// (H2, docs/COMUNICACIONES.md § 9 — "sin búsqueda de texto en
/// conversaciones"). Es <c>Contains</c> sin índice de texto completo, mismo
/// nivel que el resto de búsquedas de este repositorio — no se introduce
/// tsvector/GIN de Postgres para esto (YAGNI).
/// </summary>
public record ObtenerConversacionesQuery(
    EstadoConversacion? Estado = null,
    int? Anio = null,
    int? Mes = null,
    Guid? ClienteId = null,
    bool SoloAsignadasAMi = false,
    bool SoloSinAsignar = false,
    bool SoloEsperandoCliente = false,
    string? Busqueda = null,
    CanalConversacion? Canal = null,
    int Pagina = 1,
    int TamanoPagina = 20)
    : IRequest<ResultadoPaginado<ConversacionListaDto>>;

public record ConversacionListaDto(
    Guid Id,
    Guid? ClienteId,
    string? ClienteRazonSocial,
    string Asunto,
    EstadoConversacion Estado,
    Guid? EjecutivoAsignadoId,
    string RemitentePrincipal,
    string PreviewUltimoMensaje,
    DateTime FechaUltimoMensajeUtc,
    int TotalMensajes,
    CanalConversacion Canal,
    string? TelefonoContacto,
    DireccionMensaje? UltimoMensajeDireccion,
    int MensajesRuido = 0)
{
    /// <summary>
    /// "Esperando cliente" es un estado derivado, no persistido (decisión
    /// docs/COMUNICACIONES.md § 16.4): una conversación Abierta cuyo último
    /// mensaje fue nuestro, a la espera de que el contacto responda.
    /// </summary>
    public bool EsperandoCliente => Estado == EstadoConversacion.Abierta && UltimoMensajeDireccion == DireccionMensaje.Saliente;
}

public class ObtenerConversacionesQueryHandler(
    IEmpresasQueryContext empresasContext, IComunicacionesQueryContext comunicacionesContext,
    IIntegracionesQueryContext integracionesContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerConversacionesQuery, ResultadoPaginado<ConversacionListaDto>>
{
    private const int LongitudPreview = 140;

    // Mismo literal repetido que en AutorizacionEscrituraBehavior y por el
    // mismo motivo: Application no puede referenciar Infrastructure.Identity.Roles
    // sin invertir la dependencia entre capas.
    private const string RolCliente = "Cliente";

    public async Task<ResultadoPaginado<ConversacionListaDto>> Handle(
        ObtenerConversacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta = comunicacionesContext.Conversaciones.AsQueryable();

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
        {
            // La cola de triage (ClienteId null) queda visible pese a la
            // cartera: por definición todavía no tiene cliente resuelto, así
            // que no se puede acotar a una (ver § 12.4). Pero eso es cierto
            // solo para los roles de gestión CAE — al rol Cliente, un contacto
            // de una empresa cliente externa, le daba acceso de lectura al
            // correo sin triar de las demás (hallazgo N-2 de
            // INFORME-AUDITORIA-2.md).
            //
            // La comprobación se repite aquí y en [Authorize] de Bandeja.razor
            // a propósito: la página cierra la puerta de entrada, esto cierra
            // el dato para cualquier otra UI o API que llegue después.
            var rol = await currentUserService.ObtenerRolActualAsync();

            // Un hilo anclado a una Empresa contraparte (Conversacion.EmpresaId,
            // hoy solo la reclamación de ámbito Empresa) NO es triage: tiene
            // dueño, solo que en el otro plano. Se acota por la cartera de
            // Empresas, no por la de Clientes — sin esta rama caería en el
            // "ClienteId == null" de abajo y quedaría visible para toda la
            // gestión CAE, que es justo el alcance que la reclamación por
            // Empresa no puede regalar. Triage sigue siendo, y solo es, un
            // hilo sin ninguna de las dos anclas.
            var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken) ?? [];

            consulta = rol == RolCliente
                ? consulta.Where(c => c.ClienteId != null && clienteIdsVisibles.Contains(c.ClienteId!.Value))
                : consulta.Where(c =>
                    (c.ClienteId == null && c.EmpresaId == null) ||
                    (c.ClienteId != null && clienteIdsVisibles.Contains(c.ClienteId!.Value)) ||
                    (c.EmpresaId != null && empresaIdsVisibles.Contains(c.EmpresaId!.Value)));
        }

        // El buzón personal de OTRO gestor (ConexionIntegracion.GestorPropietarioId)
        // también tiene ClienteId null, igual que la cola de triage genuina —
        // sin esto, sus hilos se colaban en la bandeja de cualquier gestor con
        // acceso a Comunicaciones (mismo hueco corregido en ObtenerConversacionPorIdQuery
        // y en las cuatro consultas que resuelven "el buzón a usar para enviar").
        var usuarioActualId = await currentUserService.ObtenerUsuarioActualIdAsync();
        var conexionesAjenasPersonales = await integracionesContext.ConexionesIntegracion
            .Where(c => c.GestorPropietarioId != null && c.GestorPropietarioId != usuarioActualId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (conexionesAjenasPersonales.Count > 0)
        {
            consulta = consulta.Where(c =>
                c.ConexionIntegracionId == null || !conexionesAjenasPersonales.Contains(c.ConexionIntegracionId!.Value));
        }

        if (request.Estado is not null)
            consulta = consulta.Where(c => c.Estado == request.Estado);

        if (request.Canal is not null)
            consulta = consulta.Where(c => c.Canal == request.Canal);

        if (request.Anio is not null && request.Mes is not null)
            consulta = consulta.Where(c =>
                c.FechaUltimoMensajeUtc.Year == request.Anio && c.FechaUltimoMensajeUtc.Month == request.Mes);

        if (request.ClienteId is not null)
            consulta = consulta.Where(c => c.ClienteId == request.ClienteId);

        if (request.SoloSinAsignar)
            consulta = consulta.Where(c => c.EjecutivoAsignadoId == null);

        if (request.SoloAsignadasAMi)
        {
            var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
            consulta = consulta.Where(c => c.EjecutivoAsignadoId == usuarioId);
        }

        if (request.SoloEsperandoCliente)
        {
            // "Último mensaje saliente" resuelto aquí mismo, en SQL, con la
            // misma lógica que ConversacionListaDto.EsperandoCliente calcula
            // después en memoria para el resto de filas — pero este filtro
            // tiene que decidir ANTES de paginar, no después.
            consulta = consulta.Where(c => c.Estado == EstadoConversacion.Abierta &&
                comunicacionesContext.Mensajes
                    .Where(m => m.ConversacionId == c.Id)
                    .OrderByDescending(m => m.FechaUtc)
                    .Select(m => m.Direccion)
                    .FirstOrDefault() == DireccionMensaje.Saliente);
        }

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(c =>
                c.Asunto.ToUpper().Contains(busqueda) ||
                comunicacionesContext.Mensajes.Any(m => m.ConversacionId == c.Id && m.CuerpoHtml.ToUpper().Contains(busqueda)));
        }

        var total = await consulta.CountAsync(cancellationToken);
        if (total == 0)
            return new ResultadoPaginado<ConversacionListaDto>([], 0, request.Pagina, request.TamanoPagina);

        // Conversacion.ClienteId es un Empresa.Id desde F3b (AsignarClienteConversacionCommand
        // resuelve el Cliente vía IEmpresaRepository): "Cliente" es una Empresa contraparte.
        var conversaciones = await (
            from c in consulta
            join cliente in empresasContext.Empresas on c.ClienteId equals cliente.Id into clientesUnidos
            from cliente in clientesUnidos.DefaultIfEmpty()
            orderby c.FechaUltimoMensajeUtc descending
            select new
            {
                c.Id,
                c.ClienteId,
                ClienteRazonSocial = cliente != null ? cliente.RazonSocial : null,
                c.Asunto,
                c.Estado,
                c.EjecutivoAsignadoId,
                c.FechaUltimoMensajeUtc,
                c.Canal,
                c.TelefonoContacto
            })
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .ToListAsync(cancellationToken);

        var conversacionIds = conversaciones.Select(c => c.Id).ToList();

        var mensajes = await comunicacionesContext.Mensajes
            .Where(m => conversacionIds.Contains(m.ConversacionId))
            .Select(m => new { m.Id, m.ConversacionId, m.CuerpoHtml, m.FechaUtc, m.Direccion })
            .ToListAsync(cancellationToken);

        var remitentes = await comunicacionesContext.ParticipantesConversacion
            .Where(p => conversacionIds.Contains(p.ConversacionId) && p.Rol == RolParticipante.De)
            .Select(p => new { p.ConversacionId, p.Email })
            .ToListAsync(cancellationToken);

        var mensajesPorConversacion = mensajes.GroupBy(m => m.ConversacionId).ToDictionary(g => g.Key, g => g.ToList());
        var remitentesPorConversacion = remitentes.GroupBy(p => p.ConversacionId).ToDictionary(g => g.Key, g => g.First().Email);
        var conversacionPorMensaje = mensajes.ToDictionary(m => m.Id, m => m.ConversacionId);
        var mensajesRuidoPorConversacion = await ContarMensajesRuidoPorConversacionAsync(conversacionPorMensaje, cancellationToken);

        var elementos = conversaciones.Select(c =>
        {
            var mensajesDeConversacion = mensajesPorConversacion.GetValueOrDefault(c.Id, []);
            var ultimoMensaje = mensajesDeConversacion.OrderByDescending(m => m.FechaUtc).FirstOrDefault();

            return new ConversacionListaDto(
                c.Id, c.ClienteId, c.ClienteRazonSocial, c.Asunto, c.Estado, c.EjecutivoAsignadoId,
                // WhatsApp no tiene participantes de correo: el remitente es el teléfono del contacto.
                remitentesPorConversacion.GetValueOrDefault(c.Id) ?? c.TelefonoContacto ?? "Remitente desconocido",
                TruncarParaPreview(ultimoMensaje?.CuerpoHtml),
                c.FechaUltimoMensajeUtc, mensajesDeConversacion.Count, c.Canal, c.TelefonoContacto,
                ultimoMensaje?.Direccion, mensajesRuidoPorConversacion.GetValueOrDefault(c.Id));
        }).ToList();

        return new ResultadoPaginado<ConversacionListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// Ronda de reducción de ruido en Comunicaciones: cuenta, por conversación, los mensajes
    /// clasificados como ruido — <see cref="ClasificacionRuidoMensaje"/> con un motivo distinto de
    /// Ninguno, o un mensaje cuyos ítems de gestión pendientes son todos repetición de algo ya
    /// reclamado. Ambas clasificaciones se descartan si el Gestor ya confirmó manualmente que
    /// importan (ConfirmadaManualmente). Mismo criterio "cargar plano y agrupar en memoria" que el
    /// resto de este handler — evita una subconsulta correlacionada compleja para un contador
    /// secundario.
    /// </summary>
    private async Task<Dictionary<Guid, int>> ContarMensajesRuidoPorConversacionAsync(
        IReadOnlyDictionary<Guid, Guid> conversacionPorMensaje, CancellationToken cancellationToken)
    {
        var mensajeIds = conversacionPorMensaje.Keys.ToList();
        if (mensajeIds.Count == 0) return [];

        var mensajesConMotivo = await comunicacionesContext.ClasificacionesRuidoMensaje
            .Where(c => mensajeIds.Contains(c.MensajeId) && c.Motivo != MotivoRuidoMensaje.Ninguno && !c.ConfirmadaManualmente)
            .Select(c => c.MensajeId)
            .ToListAsync(cancellationToken);

        var sugerenciasGestion = await comunicacionesContext.SugerenciasGestionCorreo
            .Where(s => mensajeIds.Contains(s.MensajeId))
            .Select(s => new { s.Id, s.MensajeId })
            .ToListAsync(cancellationToken);

        var sugerenciaIds = sugerenciasGestion.Select(s => s.Id).ToList();
        var detallesPendientes = await comunicacionesContext.DetallesSugerenciaGestionCorreo
            .Where(d => sugerenciaIds.Contains(d.SugerenciaGestionCorreoId) && !d.Resuelta)
            .Select(d => new { d.Id, d.SugerenciaGestionCorreoId })
            .ToListAsync(cancellationToken);

        var detalleIdsPendientes = detallesPendientes.Select(d => d.Id).ToList();
        var detalleIdsRepeticion = (await comunicacionesContext.ClasificacionesRuidoDetalleGestion
            .Where(c => detalleIdsPendientes.Contains(c.DetalleSugerenciaGestionCorreoId) && !c.ConfirmadaManualmente)
            .Select(c => c.DetalleSugerenciaGestionCorreoId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        var mensajesSoloRepeticiones = sugerenciasGestion
            .Join(detallesPendientes, s => s.Id, d => d.SugerenciaGestionCorreoId, (s, d) => new { s.MensajeId, DetalleId = d.Id })
            .GroupBy(x => x.MensajeId)
            .Where(g => g.All(x => detalleIdsRepeticion.Contains(x.DetalleId)))
            .Select(g => g.Key);

        var mensajesRuidoIds = mensajesConMotivo.ToHashSet();
        mensajesRuidoIds.UnionWith(mensajesSoloRepeticiones);

        return mensajeIds
            .Where(mensajesRuidoIds.Contains)
            .GroupBy(mensajeId => conversacionPorMensaje[mensajeId])
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private static string TruncarParaPreview(string? cuerpoHtml)
    {
        if (string.IsNullOrWhiteSpace(cuerpoHtml)) return string.Empty;

        var textoPlano = Regex.Replace(cuerpoHtml, "<.*?>", " ");
        textoPlano = Regex.Replace(textoPlano, "\\s+", " ").Trim();

        return textoPlano.Length > LongitudPreview ? textoPlano[..LongitudPreview] + "…" : textoPlano;
    }
}
