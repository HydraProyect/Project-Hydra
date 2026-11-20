using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using System.Text.RegularExpressions;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Empresas;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.Visitas;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;

/// <summary>
/// Filtros de la bandeja compartida (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 12.6,
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
/// <see cref="Busqueda"/> compara el Asunto, las Etiquetas (texto libre de la conversación), el cuerpo de los
/// mensajes del hilo Y el nombre del Centro ligado a la conversación, siempre dentro de lo que la regla de
/// visibilidad ya deja ver (H2, Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 9 — "sin búsqueda de texto en
/// conversaciones"). Hoy ningún flujo escribe las Etiquetas, así que el Centro se resuelve por los vínculos que ya
/// existen en el esquema, sin migración: (1) un Centro participante del hilo; (2) una intención que propone un Centro
/// y que una persona confirmó (confirmar la intención no confirma por separado el Centro propuesto: una propuesta que
/// nadie confirmó no liga, una intención confirmada con un Centro mal leído sí); (3) una Gestión nacida de un mensaje
/// del hilo; (4) una sugerencia de visita aceptada tal cual; (5) una Visita creada desde el hilo. El nombre del Centro
/// se compara sin distinguir acentos (<c>TextoDeBusqueda.Contiene</c>, como /centros y /visitas) y solo entre los
/// Centros que el contexto de Centros deja leer (filtro de Tenant y de eliminados) y que están en la cartera de quien
/// busca. El filtro se suma con AND a la visibilidad: buscar por Centro no abre ninguna conversación nueva. Todo va en
/// una sola consulta (subconsultas <c>IN</c> no correlacionadas), sin cargar nada en memoria. Asunto, Etiquetas y
/// cuerpo siguen siendo <c>Contains</c> sin índice de texto completo y sensible a acentos (<c>ToUpper</c>; pasarlos a
/// <c>TextoDeBusqueda.Contiene</c> sería un incremento propio), mismo nivel que las búsquedas heredadas de este
/// repositorio — no se introduce tsvector/GIN de Postgres para esto (YAGNI). El vínculo estructurado
/// Conversación-Centro, cuando exista, es un incremento propio.
/// </summary>
[AreaDelEncargo("Comunicaciones")]
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
    int TamanoPagina = 20,
    IntencionCae? Intencion = null,
    bool SoloFueraDeSla = false)
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
    int MensajesRuido = 0,
    PrioridadConversacion Prioridad = PrioridadConversacion.Normal,
    DateTime? VencimientoSlaUtc = null,
    TimeSpan? SlaRestante = null,
    long NumeroTicket = 0,
    int TotalAdjuntos = 0,
    bool AccionPorAprobar = false,
    string? BuzonNombre = null,
    bool BuzonEsPersonal = false,
    string? EjecutivoAsignadoNombre = null)
{
    // Los cinco últimos campos son datos de la fila del lienzo «Pulida: bandeja de la cartera» (L-12). Van al final y con
    // valor por defecto: quien construye el DTO sin ellos sigue compilando.
    //  - TotalAdjuntos: archivos adjuntos de todos los mensajes del hilo, de cualquier estado de reparto (el clip dice que
    //    el hilo trae archivos, no cuántos faltan por repartir).
    //  - AccionPorAprobar: hay una propuesta de Vega sin resolver sobre el hilo (sugerencia de Visita pendiente o detalle de
    //    sugerencia de Gestión pendiente) que no sea ruido sin confirmar a mano; el detalle, en cambio, sigue mostrando esas
    //    propuestas como pendientes (agrupadas aparte como ruido). Siempre false para el Tenant solo Inbound (D6=a), que no
    //    recibe sugerencias de Outbound. No mira el estado de la conversación (decisión abierta, ver la PR).
    //  - TotalAdjuntos cuenta también las imágenes en línea (la firma): AdjuntoMensaje no distingue «en línea» y la ingesta
    //    de Graph no pide isInline. Hueco conocido, ver la PR.
    //  - BuzonNombre / BuzonEsPersonal: nombre de la conexión por la que entró el hilo y si es el buzón personal de un
    //    Gestor CAE (la lista solo trae el del usuario actual: los de otros quedan fuera por R-3a). Null si el hilo no está
    //    atado a ninguna conexión (creado a mano o sembrado).
    //  - EjecutivoAsignadoNombre: nombre visible de la persona asignada, resuelto por el directorio con el mismo acotado
    //    que el resto de lecturas (quien no es visible desde el Tenant activo no aparece: null).

    /// <summary>
    /// "Esperando respuesta externa" en la bandeja: el estado explícito <see cref="EstadoConversacion.EsperandoRespuestaExterna"/> que
    /// fija el Gestor CAE (decisión 2026-10-17, hueco 2 de Comunicaciones) o, como antes (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md
    /// § 16.4), una conversación Abierta cuyo último mensaje fue nuestro, a la espera de que el contacto responda. La
    /// segunda forma sigue derivada y no persistida; el estado explícito no depende de quién escribió el último mensaje.
    /// </summary>
    public bool EsperandoCliente =>
        Estado == EstadoConversacion.EsperandoRespuestaExterna
        || (Estado == EstadoConversacion.Abierta && UltimoMensajeDireccion == DireccionMensaje.Saliente);
}

public class ObtenerConversacionesQueryHandler(
    IEmpresasQueryContext empresasContext, IComunicacionesQueryContext comunicacionesContext,
    IIntegracionesQueryContext integracionesContext,
    ICentrosQueryContext centrosContext, IGestionesQueryContext gestionesContext, IVisitasQueryContext visitasContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, ITenantActual tenantActual,
    PlazosConversacionService? plazosService = null,
    IDirectorioUsuariosService? directorioUsuarios = null)
    : IRequestHandler<ObtenerConversacionesQuery, ResultadoPaginado<ConversacionListaDto>>
{
    private const int LongitudPreview = 140;

    public async Task<ResultadoPaginado<ConversacionListaDto>> Handle(
        ObtenerConversacionesQuery request, CancellationToken cancellationToken)
    {
        var consulta = comunicacionesContext.Conversaciones.AsQueryable();
        var ahoraUtc = DateTime.UtcNow;

        // Regla de visibilidad por anclas, la misma expresión que evalúa el
        // detalle (ObtenerConversacionPorIdQuery) y todo consumidor de
        // ConversacionVisibleAsync: ver VisibilidadConversacion (D-6 b, R-3a).
        //  - Ancla Cliente: en la cartera de Clientes.
        //  - Ancla Empresa contraparte (Conversacion.EmpresaId, hoy solo la
        //    reclamación de ámbito Empresa): NO es triaje, tiene dueño en el
        //    otro plano; se acota por el alcance de gestión de Empresas, que
        //    para el rol Cliente es vacío.
        //  - Cola de triaje (sin ninguna de las dos anclas): no tiene cartera
        //    que la acote, así que solo la ve quien cumple
        //    IAlcanceDatosService.TriajeVisibleAsync: acceso total en el
        //    Tenant o, fuera de él, un rol de gestión de Comunicaciones con un
        //    ámbito efectivo del Tenant entero. Un rol nulo (por ejemplo, una
        //    selección cuya Asignación de Operación deja de autorizar con el
        //    circuito abierto), el rol Cliente (hallazgo N-2 de
        //    Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md) o un
        //    Gestor CAE bajo una Asignación de Operación acotada a un Cliente
        //    empresarial (ADR-011: ámbito efectivo = cartera ∩ operación) no
        //    ven triaje. Falla cerrado: antes de R-3a el triaje se abría a
        //    todo rol distinto de Cliente, el nulo incluido.
        //  - Sin ancla, pero entrando por el buzón personal propio
        //    (IAlcanceDatosService.BuzonesPersonalesPropiosAsync): solo con
        //    una Asignación de Cartera vigente, el Gestor CAE propietario lo ve
        //    aunque no alcance el Tenant entero (DECISION_LOG 2026-11-14,
        //    decisión 2: ese correo «solo vive en la cuenta del Gestor CAE
        //    propietario»). Es la misma condición que el detalle, dentro de la
        //    misma expresión.
        //  - Y la regla del buzón del detalle: fuera los hilos del buzón
        //    personal de OTRO Gestor CAE (ConexionIntegracion.GestorPropietarioId),
        //    que también tienen ClienteId null, y los de una conexión eliminada.
        //
        // Las dos mitades viven en ConversacionesVisibles.
        // La comprobación se repite aquí y en [Authorize] de Bandeja.razor
        // a propósito: la página cierra la puerta de entrada, esto cierra
        // el dato para cualquier otra UI o API que llegue después.
        consulta = await ConversacionesVisibles.FiltrarAsync(
            consulta, alcanceDatos, currentUserService, integracionesContext, tenantActual, cancellationToken);

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
            consulta = consulta.Where(c => c.Estado == EstadoConversacion.EsperandoRespuestaExterna ||
                (c.Estado == EstadoConversacion.Abierta &&
                comunicacionesContext.Mensajes
                    .Where(m => m.ConversacionId == c.Id && !(m.Direccion == DireccionMensaje.Saliente && m.EsAutomatico))
                    .OrderByDescending(m => m.FechaUtc)
                    .Select(m => m.Direccion)
                    .FirstOrDefault() == DireccionMensaje.Saliente));
        }

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();

            // Centros cuyo nombre contiene la búsqueda (sin distinguir acentos, como /centros y /visitas), como
            // subconsulta y nunca en memoria. Pasan por el filtro global de Tenant y de eliminados de Centro, y por la
            // cartera de Centros de quien busca (la misma que ObtenerCentrosQuery): un Centro de otro Tenant, eliminado
            // o fuera de la cartera no liga con nada ni revela su nombre. Todo se suma con AND al conjunto que ya acotó
            // la regla de visibilidad: buscar por Centro solo deja ENCONTRAR lo que ya se veía. Al Tenant solo Inbound
            // (AmbitoComunicacionesSoloInbound, D6=a) la cartera de Centros le llega vacía: ningún Centro liga, y con él
            // ningún hilo por participante, intención, Gestión, sugerencia ni Visita.
            var centroIdsVisibles = await alcanceDatos.CentrosVisiblesAsync(cancellationToken);
            var terminoCentro = request.Busqueda;
            var centrosCoincidentes = centrosContext.Centros.Where(centro => TextoDeBusqueda.Contiene(centro.Nombre, terminoCentro));
            if (centroIdsVisibles is not null)
                centrosCoincidentes = centrosCoincidentes.Where(centro => centroIdsVisibles.Contains(centro.Id));
            var centroIds = centrosCoincidentes.Select(centro => centro.Id);

            // Una subconsulta por vínculo con las Conversaciones que liga a esos Centros. No dependen de la fila de la
            // lista, así que Postgres las resuelve una vez (hashed subplan) en vez de una por conversación.
            // Vínculo 1: un Centro es participante del hilo (el Centro de la visita en copia).
            var porParticipante = comunicacionesContext.ParticipantesConversacion
                .Where(p => p.TipoOrigen == TipoParticipanteOrigen.Centro
                    && p.EntidadRelacionadaId != null && centroIds.Contains(p.EntidadRelacionadaId.Value))
                .Select(p => p.ConversacionId);
            // Vínculo 2: la intención del hilo propone un Centro y una persona confirmó la propuesta sin cambiarla
            // (ConfirmadaPorPersona y la intención actual sigue siendo la propuesta, es decir, !CambiadaPorPersona;
            // esa propiedad no está mapeada, así que la comparación se escribe aquí). La propuesta que nadie confirmó y
            // la que la persona confirmó cambiándola no ligan: si corrigió la intención, el Centro propuesto con ella
            // tampoco queda respaldado. Confirmar no confirma por separado el Centro: una propuesta aceptada tal cual
            // con un Centro mal leído sí liga.
            var porIntencion = comunicacionesContext.IntencionesConversacion
                .Where(i => i.ConfirmadaPorPersona && i.IntencionActual == i.IntencionPropuesta
                    && i.TipoEntidadPropuesta == TipoEntidadIntencion.Centro
                    && i.EntidadPropuestaId != null && centroIds.Contains(i.EntidadPropuestaId.Value))
                .Select(i => i.ConversacionId);
            // Vínculo 3: una Gestión nacida de un mensaje del hilo, en el Centro de esa Gestión.
            var porGestion = gestionesContext.Gestiones
                .Where(g => g.MensajeOrigenId != null && centroIds.Contains(g.CentroId))
                .Join(comunicacionesContext.Mensajes, g => g.MensajeOrigenId!.Value, m => m.Id, (g, m) => m.ConversacionId);
            // Vínculo 4: una sugerencia de visita aceptada tal cual (Confirmada: el Centro propuesto es el de la Visita
            // creada). La pendiente, la descartada y la aceptada con edición no ligan: o el Centro es solo una
            // propuesta de la IA, o el Gestor pudo cambiarlo y esta fila no guarda cuál.
            var porSugerencia = comunicacionesContext.SugerenciasVisitaCorreo
                .Where(s => s.Resuelta && s.Resolucion == ResolucionSugerencia.Confirmada
                    && s.CentroId != null && centroIds.Contains(s.CentroId.Value))
                .Join(comunicacionesContext.Mensajes, s => s.MensajeId, m => m.Id, (s, m) => m.ConversacionId);
            // Vínculo 5: la Visita creada desde el hilo (evento VisitaCreada), en el Centro real de la Visita, también
            // si el Gestor corrigió el Centro propuesto al crearla. Una Visita eliminada deja de ligar por este vínculo (el 4 sigue ligando).
            var porVisita = comunicacionesContext.EventosConversacion
                .Where(e => e.Tipo == TipoEventoConversacion.VisitaCreada)
                .Join(visitasContext.Visitas.Where(v => centroIds.Contains(v.CentroId)), e => e.ReferenciaId, v => v.Id,
                    (e, v) => e.ConversacionId);

            consulta = consulta.Where(c =>
                c.Asunto.ToUpper().Contains(busqueda) ||
                (c.Etiquetas != null && c.Etiquetas.ToUpper().Contains(busqueda)) ||
                comunicacionesContext.Mensajes.Any(m => m.ConversacionId == c.Id && m.CuerpoHtml.ToUpper().Contains(busqueda)) ||
                porParticipante.Contains(c.Id) || porIntencion.Contains(c.Id) || porGestion.Contains(c.Id) ||
                porSugerencia.Contains(c.Id) || porVisita.Contains(c.Id));
        }

        if (request.Intencion is { } intencion)
            consulta = consulta.Where(c => comunicacionesContext.IntencionesConversacion.Any(i => i.ConversacionId == c.Id && i.IntencionActual == intencion));

        Dictionary<Guid, (DateTime Vence, TimeSpan Restante)>? plazosFiltrados = null;
        if (request.SoloFueraDeSla)
        {
            // Antes de paginar: el calendario del Operador CAE no es una duración constante traducible a SQL.
            // shortcut: CalcularPlazosAsync carga en memoria las conversaciones vivas del conjunto visible, la dirección y la fecha de todos sus mensajes y un calendario
            // por conversación, y la pantalla de la bandeja lo repite en cada refresco de plazos (un minuto) con «Fuera de plazo» como
            // vista vigente; un filtro de plazos vencidos en SQL (vencimiento persistido o calendario precalculado) cuando el volumen
            // de conversaciones vivas lo note.
            plazosFiltrados = await CalcularPlazosAsync(consulta, ahoraUtc, cancellationToken);
            var vencidas = plazosFiltrados.Where(p => p.Value.Vence <= ahoraUtc).Select(p => p.Key).ToArray();
            consulta = consulta.Where(c => vencidas.Contains(c.Id));
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
                // Buzón por el que entró el hilo (L-12): unión izquierda por la clave primaria de la conexión, así que ni
                // quita ni duplica filas, y sin una sentencia más. Pasa por el filtro global de Tenant y de eliminadas: una
                // conexión eliminada o de otro Tenant queda sin nombre (R-3a ya sacó de la lista los hilos de las eliminadas y de
                // los buzones personales de otros Gestores CAE).
            join conexion in integracionesContext.ConexionesIntegracion on c.ConexionIntegracionId equals conexion.Id into conexionesUnidas
            from conexion in conexionesUnidas.DefaultIfEmpty()
            orderby c.FechaUltimoMensajeUtc descending
            select new
            {
                c.Id,
                c.ClienteId,
                ClienteRazonSocial = cliente != null ? cliente.RazonSocial : null,
                c.Asunto,
                c.Estado,
                c.Prioridad,
                c.NumeroTicket,
                c.EjecutivoAsignadoId,
                BuzonNombre = conexion != null ? conexion.Nombre : null,
                BuzonEsPersonal = conexion != null && conexion.GestorPropietarioId != null,
                c.FechaUltimoMensajeUtc,
                c.Canal,
                c.TelefonoContacto
            })
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .ToListAsync(cancellationToken);

        var conversacionIds = conversaciones.Select(c => c.Id).ToList();
        var plazos = plazosFiltrados ?? await CalcularPlazosAsync(consulta.Where(c => conversacionIds.Contains(c.Id)), ahoraUtc, cancellationToken);

        var mensajes = await comunicacionesContext.Mensajes
            .Where(m => conversacionIds.Contains(m.ConversacionId))
            .Select(m => new { m.Id, m.ConversacionId, m.CuerpoHtml, m.FechaUtc, m.Direccion, m.Remitente, m.EsAutomatico })
            .ToListAsync(cancellationToken);

        var remitentes = await comunicacionesContext.ParticipantesConversacion
            .Where(p => conversacionIds.Contains(p.ConversacionId) && p.Rol == RolParticipante.De)
            .Select(p => new { p.ConversacionId, p.Email })
            .ToListAsync(cancellationToken);

        var mensajesPorConversacion = mensajes.GroupBy(m => m.ConversacionId).ToDictionary(g => g.Key, g => g.ToList());
        var remitentesPorConversacion = remitentes.GroupBy(p => p.ConversacionId).ToDictionary(g => g.Key, g => g.First().Email);
        var conversacionPorMensaje = mensajes.ToDictionary(m => m.Id, m => m.ConversacionId);
        var mensajesRuidoPorConversacion = await ContarMensajesRuidoPorConversacionAsync(conversacionPorMensaje, cancellationToken);

        // Datos de la fila del lienzo (L-12). Todo se lee por los Ids de la página ya paginada, nunca por fila (sin N+1), y
        // por los mismos contextos de lectura (mismo filtro de Tenant y misma RLS): no amplían el conjunto de filas, solo
        // rellenan columnas de las que ya salieron de la visibilidad R-3a.
        var adjuntosPorConversacion = await ContarAdjuntosPorConversacionAsync(conversacionPorMensaje, cancellationToken);
        var conversacionesConAccionPorAprobar = await ConversacionesConAccionPorAprobarAsync(conversacionPorMensaje, cancellationToken);
        var nombresAsignados = await ObtenerNombresAsignadosAsync(
            conversaciones.Where(c => c.EjecutivoAsignadoId != null).Select(c => c.EjecutivoAsignadoId!.Value).Distinct().ToList(),
            cancellationToken);

        var elementos = conversaciones.Select(c =>
        {
            var mensajesDeConversacion = mensajesPorConversacion.GetValueOrDefault(c.Id, []);
            // El acuse automático no dice en qué punto está la conversación: ni la vista previa ni «esperando» lo cuentan.
            var ultimoMensaje = mensajesDeConversacion
                .Where(m => !(m.Direccion == DireccionMensaje.Saliente && m.EsAutomatico))
                .OrderByDescending(m => m.FechaUtc).FirstOrDefault();

            return new ConversacionListaDto(
                c.Id, c.ClienteId, c.ClienteRazonSocial, c.Asunto, c.Estado, c.EjecutivoAsignadoId,
                // WhatsApp no tiene participantes de correo: el remitente es el teléfono del contacto. Un hilo sin
                // participante «De» enseña la dirección de su primer mensaje antes que un rótulo sin dato.
                remitentesPorConversacion.GetValueOrDefault(c.Id) ?? c.TelefonoContacto
                    ?? mensajesDeConversacion.OrderBy(m => m.FechaUtc).Select(m => m.Remitente).FirstOrDefault()
                    ?? "Remitente desconocido",
                TruncarParaPreview(ultimoMensaje?.CuerpoHtml),
                c.FechaUltimoMensajeUtc, mensajesDeConversacion.Count, c.Canal, c.TelefonoContacto,
                ultimoMensaje?.Direccion, mensajesRuidoPorConversacion.GetValueOrDefault(c.Id), c.Prioridad,
                plazos.TryGetValue(c.Id, out var plazo) ? plazo.Vence : null,
                plazos.ContainsKey(c.Id) ? plazo.Restante : null, c.NumeroTicket,
                adjuntosPorConversacion.GetValueOrDefault(c.Id),
                conversacionesConAccionPorAprobar.Contains(c.Id),
                c.BuzonNombre, c.BuzonEsPersonal,
                c.EjecutivoAsignadoId is { } ejecutivoId && nombresAsignados.TryGetValue(ejecutivoId, out var nombreAsignado) ? nombreAsignado : null);
        }).ToList();

        return new ResultadoPaginado<ConversacionListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }

    private async Task<Dictionary<Guid, (DateTime Vence, TimeSpan Restante)>> CalcularPlazosAsync(
        IQueryable<Conversacion> consulta, DateTime ahoraUtc, CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, (DateTime, TimeSpan)>();
        if (plazosService is null) return resultado;
        var candidatas = await consulta.Where(c => TransicionesEstadoConversacion.EstadosVivos.Contains(c.Estado))
            .AsNoTracking().ToListAsync(cancellationToken);
        var ids = candidatas.Select(c => c.Id).ToArray();
        var fechas = await comunicacionesContext.Mensajes.Where(m => ids.Contains(m.ConversacionId))
            .Select(m => new { m.ConversacionId, m.Direccion, m.FechaUtc, m.EsAutomatico }).ToListAsync(cancellationToken);
        var porConversacion = fechas.ToLookup(m => m.ConversacionId);
        foreach (var conversacion in candidatas)
        {
            var mensajes = porConversacion[conversacion.Id];
            var ultimoSaliente = mensajes.Where(m => m.Direccion == DireccionMensaje.Saliente && !m.EsAutomatico).Select(m => (DateTime?)m.FechaUtc).Max();
            var primero = mensajes.Where(m => m.Direccion == DireccionMensaje.Entrante && !m.EsAutomatico
                                              && (ultimoSaliente == null || m.FechaUtc > ultimoSaliente))
                .Select(m => (DateTime?)m.FechaUtc).Min();
            if (primero is null) continue;
            var calendario = await plazosService.ObtenerCalendarioAsync(conversacion, cancellationToken);
            if (calendario is null) continue;
            var vence = PlazosConversacionService.Vencimiento(conversacion.Prioridad,
                new DateTimeOffset(DateTime.SpecifyKind(primero.Value, DateTimeKind.Utc)), calendario);
            var ahora = new DateTimeOffset(DateTime.SpecifyKind(ahoraUtc, DateTimeKind.Utc));
            var restante = vence >= ahora ? calendario.CalcularTiempoLaborable(ahora, vence)
                : -calendario.CalcularTiempoLaborable(vence, ahora);
            resultado[conversacion.Id] = (vence.UtcDateTime, restante);
        }
        return resultado;
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

        // Con el marco de Comunicaciones del Tenant solo Inbound (D6=a), el detalle no proyecta sugerencias de Gestión;
        // la bandeja tampoco las cuenta como ruido, o el número revelaría que existen y no coincidiría con el detalle.
        var sugerenciasGestion = AmbitoComunicacionesSoloInbound.Activo ? [] : await comunicacionesContext.SugerenciasGestionCorreo
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

    /// <summary>Archivos adjuntos por conversación (todos los estados de reparto), en una sola consulta agrupada por mensaje.</summary>
    private async Task<Dictionary<Guid, int>> ContarAdjuntosPorConversacionAsync(
        IReadOnlyDictionary<Guid, Guid> conversacionPorMensaje, CancellationToken cancellationToken)
    {
        var mensajeIds = conversacionPorMensaje.Keys.ToList();
        if (mensajeIds.Count == 0) return [];

        var porMensaje = await comunicacionesContext.AdjuntosMensaje
            .Where(a => mensajeIds.Contains(a.MensajeId))
            .GroupBy(a => a.MensajeId)
            .Select(g => new { MensajeId = g.Key, Total = g.Count() })
            .ToListAsync(cancellationToken);

        return porMensaje
            .GroupBy(a => conversacionPorMensaje[a.MensajeId])
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Total));
    }

    /// <summary>
    /// Conversaciones con una propuesta de Vega sin resolver: una sugerencia de Visita pendiente o un detalle de sugerencia de
    /// Gestión pendiente (<c>!Resuelta</c>, como el detalle), salvo lo que el sistema ya clasificó como ruido y nadie
    /// confirmó a mano: un mensaje con motivo de ruido (<c>ClasificacionRuidoMensaje</c>) no aporta ninguna de las dos
    /// propuestas, y un detalle de Gestión que repite algo ya reclamado (<c>ClasificacionRuidoDetalleGestion</c>) tampoco.
    /// Es la regla de <see cref="ContarMensajesRuidoPorConversacionAsync"/> para que una fila no salga a la vez con ruido y con
    /// acción por aprobar POR LO MISMO. Distinto del detalle, que sigue proyectando esas propuestas como pendientes y las
    /// agrupa aparte como ruido. Salvedad conocida: un mensaje cuyos detalles de Gestión son todos repetición cuenta como
    /// ruido en la fila aunque lleve además una sugerencia de Visita pendiente sin clasificar, que sí cuenta aquí. No depende del estado de la
    /// conversación (decisión abierta, ver la PR). Con el marco de Comunicaciones del Tenant solo Inbound (D6=a) no se
    /// cuentan, como tampoco se proyectan en el detalle ni cuentan como ruido: el indicador revelaría que existen. Es el único
    /// sitio donde se decide qué es «acción por aprobar» para la fila. Una sola sentencia (UNION), por el presupuesto de SQL.
    /// </summary>
    private async Task<HashSet<Guid>> ConversacionesConAccionPorAprobarAsync(
        IReadOnlyDictionary<Guid, Guid> conversacionPorMensaje, CancellationToken cancellationToken)
    {
        var mensajeIds = conversacionPorMensaje.Keys.ToList();
        if (mensajeIds.Count == 0 || AmbitoComunicacionesSoloInbound.Activo) return [];

        var mensajesConVisita = comunicacionesContext.SugerenciasVisitaCorreo
            .Where(s => mensajeIds.Contains(s.MensajeId) && !s.Resuelta
                && !comunicacionesContext.ClasificacionesRuidoMensaje.Any(c =>
                    c.MensajeId == s.MensajeId && c.Motivo != MotivoRuidoMensaje.Ninguno && !c.ConfirmadaManualmente))
            .Select(s => s.MensajeId);

        var mensajesConGestion =
            from detalle in comunicacionesContext.DetallesSugerenciaGestionCorreo
            where !detalle.Resuelta
                && !comunicacionesContext.ClasificacionesRuidoDetalleGestion.Any(c =>
                    c.DetalleSugerenciaGestionCorreoId == detalle.Id && !c.ConfirmadaManualmente)
            join sugerencia in comunicacionesContext.SugerenciasGestionCorreo on detalle.SugerenciaGestionCorreoId equals sugerencia.Id
            where mensajeIds.Contains(sugerencia.MensajeId)
                && !comunicacionesContext.ClasificacionesRuidoMensaje.Any(c =>
                    c.MensajeId == sugerencia.MensajeId && c.Motivo != MotivoRuidoMensaje.Ninguno && !c.ConfirmadaManualmente)
            select sugerencia.MensajeId;

        var mensajesConAccion = await mensajesConVisita.Union(mensajesConGestion).ToListAsync(cancellationToken);

        return mensajesConAccion.Select(id => conversacionPorMensaje[id]).ToHashSet();
    }

    /// <summary>
    /// Nombre visible de las personas asignadas a las conversaciones de la página. El directorio solo resuelve a quien es
    /// alcanzable desde el Tenant activo: el resto queda sin nombre, no con el de otro Tenant. Sin directorio, sin nombres.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresAsignadosAsync(
        IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken)
    {
        if (directorioUsuarios is null || usuarioIds.Count == 0) return new Dictionary<Guid, string>();
        return await directorioUsuarios.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);
    }

    private static string TruncarParaPreview(string? cuerpoHtml)
    {
        if (string.IsNullOrWhiteSpace(cuerpoHtml)) return string.Empty;

        var textoPlano = Regex.Replace(cuerpoHtml, "<.*?>", " ");
        textoPlano = Regex.Replace(textoPlano, "\\s+", " ").Trim();

        return textoPlano.Length > LongitudPreview ? textoPlano[..LongitudPreview] + "…" : textoPlano;
    }
}
