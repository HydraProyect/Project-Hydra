using System.Text.Json.Serialization;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientes;

/// <param name="OrdenarPor">
/// Nombre de una propiedad de <see cref="ClienteListaDto"/>. <c>EstadoDocumentalPeor</c> ordena por
/// el peor estado documental (ascendente: el peor primero; empates por razón social), exacto para
/// toda la cartera; solo combinado con <paramref name="EstadoDocumental"/> hereda el tope de 2000
/// candidatos de ese filtro. Sin valor, por razón social.
/// </param>
/// <param name="EstadosDocumentales">
/// Varios estados a la vez (la franja de estado del listado deja marcar más de uno): pasa el Cliente empresarial
/// que cumpla cualquiera. Se suma a <paramref name="EstadoDocumental"/> si llegan los dos, con su mismo criterio:
/// un estado pregunta si HAY alguna alerta en él, y <c>Vigente</c> es el centinela de «sin ninguna alerta».
/// </param>
/// <param name="ConRecuentosPorEstado">
/// Rellena <c>ResultadoPaginado.RecuentosPorEstado</c> con los demás filtros aplicados y sin el de estado. Como
/// el filtro pregunta «hay alguna», un Cliente empresarial con vencidos y urgentes cuenta en los dos: las cifras
/// no suman el total. Por eso la clave <see cref="ClavePorVencer"/> cuenta aparte a quien tiene urgentes o
/// próximos, sin sumarlo dos veces.
/// </param>
/// <param name="ConDesgloseDocumental">
/// Rellena, para las filas de la página, <see cref="ClienteListaDto.Incidencias"/> y
/// <see cref="ClienteListaDto.IncidenciasTotales"/>: las MISMAS alertas que dan el peor estado y su recuento,
/// una por una. Es opt-in porque solo lo pinta el listado (carga de página y refresco de una fila por
/// <paramref name="Id"/>): la API, la exportación y los demás llamadores no lo piden y reciben la lista vacía
/// (y la API, además, no serializa esos dos campos).
/// </param>
public record ObtenerClientesQuery(
    string? Busqueda, bool? SoloCriticos, Guid? EjecutivoUsuarioId = null, EstadoDocumento? EstadoDocumental = null,
    int Pagina = 1, int TamanoPagina = 20, string? OrdenarPor = null, bool Descendente = false,
    IReadOnlyCollection<EstadoDocumento>? EstadosDocumentales = null, bool ConRecuentosPorEstado = false,
    Guid? Id = null, bool ConDesgloseDocumental = false)
    : IRequest<ResultadoPaginado<ClienteListaDto>>
{
    /// <summary>
    /// Tope de incidencias que viajan por fila con <see cref="ConDesgloseDocumental"/>. Un Cliente empresarial
    /// agrega las alertas de todos los Trabajadores que se le atribuyen, sin límite natural; la ventana del
    /// listado enseña las más graves y dice cuántas quedan fuera (<see cref="ClienteListaDto.IncidenciasTotales"/>).
    /// </summary>
    public const int MaximoIncidenciasPorFila = 10;

    /// <summary>
    /// Clave de <c>RecuentosPorEstado</c> para «tiene alguna alerta Urgente o Próxima»: los dos estados que la
    /// interfaz rotula «Por vencer», unidos con el separador de la selección de estados.
    /// </summary>
    public const string ClavePorVencer = nameof(EstadoDocumento.Urgente) + "," + nameof(EstadoDocumento.Proximo);
}

/// <param name="SinContactoEnAgenda">
/// Perfil incompleto: no hay nadie a quien reclamarle documentación. La
/// reclamación de este cliente fallará hasta que la agenda tenga al menos un
/// contacto (decisión del usuario, 2026-08-13) — se expone en el listado para
/// que el hueco se pueda cerrar en bloque, no de uno en uno al fallar el envío.
/// </param>
/// <param name="Centros">Centros propios de este Cliente (mockup "Lista Clientes TALVEG") — mismo alcance que ObtenerCentrosDeClienteQuery, aquí en recuento por lote.</param>
/// <param name="EstadoDocumentalPeor">
/// El peor estado entre todas las alertas atribuidas a este Cliente empresarial: las de vigencia
/// (Vencido/Urgente/Próximo) de los Trabajadores cuyo Cliente principal es este (ver AlertaDto.ClienteId,
/// IResolverClientePrincipalService) y las de documento faltante (Faltante) de los Trabajadores asignados a
/// sus Centros — null si no tiene ninguna alerta abierta
/// ("Al corriente", mismo criterio que "nada pendiente" en Inicio/Mi trabajo: la
/// ausencia se lee como estado, no como "sin datos").
/// </param>
/// <param name="EstadoDocumentalCantidad">Cuántas alertas hay en <paramref name="EstadoDocumentalPeor"/> — "12 vencidos", no solo "vencidos".</param>
public record ClienteListaDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, DateTime CreadoEnUtc,
    bool SinContactoEnAgenda = false,
    Guid? EjecutivoUsuarioId = null,
    int Centros = 0,
    EstadoDocumento? EstadoDocumentalPeor = null,
    int EstadoDocumentalCantidad = 0)
{
    /// <summary>
    /// Las alertas que dan <see cref="EstadoDocumentalPeor"/>, una por una y de la más grave a la menos, hasta
    /// <see cref="ObtenerClientesQuery.MaximoIncidenciasPorFila"/>. Solo se rellena con
    /// <see cref="ObtenerClientesQuery.ConDesgloseDocumental"/>; vacía si no se pidió o si no hay ninguna.
    /// Son todas las alertas del Cliente empresarial, no solo las del peor estado que cuenta
    /// <see cref="EstadoDocumentalCantidad"/>.
    ///
    /// <para>
    /// No se serializa: este record es también el cuerpo de <c>GET /api/v1/clientes</c>, un contrato publicado
    /// que no gana campos porque los gane el listado, y las incidencias nombran Trabajadores. Lo fija
    /// <c>ClienteApiDtosTests</c>.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<IncidenciaClienteDto> Incidencias { get; init; } = [];

    /// <summary>
    /// Cuántas alertas tiene en total; las que no caben en <see cref="Incidencias"/> son la diferencia. Tampoco
    /// se serializa, por lo mismo.
    /// </summary>
    [JsonIgnore]
    public int IncidenciasTotales { get; init; }

    /// <summary>
    /// Las alertas del Cliente empresarial por estado, con las mismas palabras que el motivo de cada Centro
    /// («3 vencidos · 1 pendiente · 5 por vencer»). Revisión 1:1 del 2026-10-21: el motivo decía solo cuántas
    /// había en el peor estado («6 documentos») y no se podía comparar con Inicio ni con los Centros. Mismo
    /// universo que <see cref="EstadoDocumentalPeor"/> (documentos de Trabajador; un documento caducado cuenta una
    /// vez en el Cliente empresarial aunque cuente en cada uno de sus Centros). No se serializa, por lo mismo que
    /// <see cref="Incidencias"/>.
    /// </summary>
    [JsonIgnore]
    public RecuentoAlertasClienteDto Recuento { get; init; } = RecuentoAlertasClienteDto.Vacio;

    /// <summary>
    /// Algún Centro de Trabajo de este Cliente empresarial está en <c>EstadoCentro.Bloqueado</c>: la plataforma CAE
    /// rechazó un documento que ese Centro exige (D-7). No es un estado documental, por eso no cambia la pastilla
    /// ni el orden; se rotula aparte con las mismas palabras que Cliente 360 («Bloqueo de la plataforma CAE»).
    /// Solo se calcula para las filas de la página. No se serializa.
    /// </summary>
    [JsonIgnore]
    public bool BloqueoPlataformaCae { get; init; }
}

/// <summary>Alertas de un Cliente empresarial por estado; <see cref="PorVencer"/> junta Urgente y Próximo, como la pastilla.</summary>
public sealed record RecuentoAlertasClienteDto(int Vencidos, int Pendientes, int PorVencer)
{
    public static readonly RecuentoAlertasClienteDto Vacio = new(0, 0, 0);

    public static RecuentoAlertasClienteDto De(IEnumerable<AlertaDto> alertas)
    {
        var lista = alertas as IReadOnlyCollection<AlertaDto> ?? alertas.ToList();
        return new(
            lista.Count(a => a.Estado == EstadoDocumento.Vencido),
            lista.Count(a => a.Estado == EstadoDocumento.Faltante),
            lista.Count(a => a.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo));
    }
}

/// <summary>
/// Una alerta documental atribuida a un Cliente empresarial, tal como la calcula <c>ObtenerAlertasQuery</c>: un
/// documento de un Trabajador que está Vencido, Urgente o Próximo, o un Tipo de documento que le falta a un
/// Trabajador en un Centro de ese Cliente empresarial (<see cref="EstadoDocumento.Faltante"/>, sin documento).
/// No es un documento propio del Cliente empresarial: esos no generan alerta.
///
/// <para>
/// Lleva lo justo para nombrarla en la ventana del listado y para corregirla sin salir de él: con
/// <see cref="DocumentoId"/> se renueva ese documento; sin él, se da de alta el que falta con
/// <see cref="TrabajadorId"/> y <see cref="TipoDocumentoId"/>. La atribución es la de la alerta: una de
/// vigencia va al Cliente empresarial «principal» del Trabajador (<c>IResolverClientePrincipalService</c>);
/// una de documento faltante, al del Centro donde falta.
/// </para>
/// </summary>
/// <param name="Clave">Identidad de la alerta entre las de su fila (<c>AlertaDto.IdDeFila</c>): sirve de <c>@key</c>.</param>
/// <param name="CentroNombre">Centro donde falta el documento; solo en <see cref="EstadoDocumento.Faltante"/>.</param>
public sealed record IncidenciaClienteDto(
    string Clave, EstadoDocumento Estado, Guid? DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre,
    Guid TrabajadorId, string TrabajadorNombre, DateOnly? FechaVencimiento, string? CentroNombre);

/// <summary>
/// F4-P0 (2026-08-27): congelada desde F3b-Cliente (PR #279), esta consulta
/// seguía leyendo la tabla legacy <c>Clientes</c>, que ya no recibe
/// escrituras — cualquier Cliente dado de alta después del freeze era
/// invisible en su propio listado (nunca silencioso: el alta lo confirma, la
/// lista simplemente no lo muestra). "Cliente" es la Empresa contraparte
/// creada por <see cref="Empresa.CrearComoCliente"/>; <c>EsCritico != null</c>
/// es su discriminador — mismo patrón que <c>NivelServicio != null</c> distingue
/// Subcontrata en <see cref="Subcontratas.Queries.ObtenerSubcontratas.ObtenerSubcontratasQuery"/>.
/// Los demás lectores de este handler (Centros/ContactosAgenda/Alertas) ya
/// leían <c>Empresa.Id</c> desde el repunteo de FKs de F3b — sin cambio.
/// </summary>
public class ObtenerClientesQueryHandler(
    IEmpresasQueryContext dbContext, IContactosAgendaQueryContext contactosContext,
    ICentrosQueryContext centrosContext, IMediator mediator, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerClientesQuery, ResultadoPaginado<ClienteListaDto>>
{
    public async Task<ResultadoPaginado<ClienteListaDto>> Handle(ObtenerClientesQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.Where(e => e.EsCritico != null);

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(c => clienteIdsVisibles.Contains(c.Id));

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // Va después del alcance: solo estrecha.
        if (request.Id is { } id)
            consulta = consulta.Where(c => c.Id == id);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda;
            consulta = consulta.Where(c => TextoDeBusqueda.Contiene(c.RazonSocial, busqueda));
        }

        if (request.SoloCriticos == true)
            consulta = consulta.Where(c => c.EsCritico == true);

        if (request.EjecutivoUsuarioId is { } ejecutivoId)
            consulta = consulta.Where(c => c.EjecutivoUsuarioId == ejecutivoId);

        // Estado documental filtra sobre el agregado por Cliente, que solo se
        // puede calcular tras resolver las alertas (más abajo): ese agregado
        // se resuelve en memoria, pero el filtro que sale de él se aplica en
        // SQL como conjuntos de Id (FiltrarPorEstado), y se cuenta y pagina allí.
        var totalSinEstado = await consulta.CountAsync(cancellationToken);

        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(ClienteListaDto.RazonSocial), true) => consulta.OrderByDescending(c => c.RazonSocial),
            (nameof(ClienteListaDto.Cif), false) => consulta.OrderBy(c => c.Cif),
            (nameof(ClienteListaDto.Cif), true) => consulta.OrderByDescending(c => c.Cif),
            (nameof(ClienteListaDto.EsCritico), false) => consulta.OrderBy(c => c.EsCritico).ThenBy(c => c.RazonSocial),
            (nameof(ClienteListaDto.EsCritico), true) => consulta.OrderByDescending(c => c.EsCritico).ThenBy(c => c.RazonSocial),
            (nameof(ClienteListaDto.CreadoEnUtc), false) => consulta.OrderBy(c => c.CreadoEnUtc),
            (nameof(ClienteListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(c => c.CreadoEnUtc),
            _ => consulta.OrderBy(c => c.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(c => c.Id);

        // El estado documental es un agregado calculado (no una columna): sale de las alertas
        // de vigencia, que se resuelven una vez por petición.
        var (estadoPorCliente, alertasPorCliente) = await ObtenerEstadoDocumentalPorClienteAsync(cancellationToken);
        var ordenarPorEstado = request.OrdenarPor == nameof(ClienteListaDto.EstadoDocumentalPeor);

        var estadosPedidos = (request.EstadosDocumentales ?? []).ToHashSet();
        if (request.EstadoDocumental is { } unEstado)
            estadosPedidos.Add(unEstado);

        IReadOnlyDictionary<string, int>? recuentosPorEstado = request.ConRecuentosPorEstado
            ? await ContarPorEstadoAsync(consulta, estadoPorCliente, totalSinEstado, cancellationToken)
            : null;

        List<ClienteListaDto> elementos;
        int total;
        if (estadosPedidos.Count == 0 && !ordenarPorEstado)
        {
            // Sin filtro ni orden por Estado documental: paginación normal en SQL.
            elementos = ConEstado(await Proyectar(ordenada
                .Skip((request.Pagina - 1) * request.TamanoPagina)
                .Take(request.TamanoPagina))
                .ToListAsync(cancellationToken), estadoPorCliente);
            total = totalSinEstado;
        }
        else if (estadosPedidos.Count == 0)
        {
            // Solo orden por estado (el de por defecto de la lista, rediseño de listados fase 1):
            // exacto y sin tope de candidatos, porque todo Cliente empresarial sin alertas es
            // «Al corriente» y va detrás de los que tienen alguna (o delante, en descendente).
            // Así solo los que tienen alertas se ordenan en memoria; el resto se pagina en SQL.
            elementos = await PaginaOrdenadaPorEstadoAsync(consulta, estadoPorCliente, request, totalSinEstado, cancellationToken);
            total = totalSinEstado;
        }
        else
        {
            // Filtro por Estado documental, en SQL y sin tope de candidatos: el agregado por Cliente
            // empresarial ya está resuelto en memoria (estadoPorCliente, solo quien tiene alertas), así
            // que el filtro se traduce a dos conjuntos de Id que la base aplica sobre la consulta con
            // el alcance, la búsqueda y los demás filtros. Se cuenta y se pagina en SQL como el camino
            // normal; con orden por estado, el mismo reparto en dos tramos que sin filtro.
            var filtrada = FiltrarPorEstado(consulta, estadoPorCliente, estadosPedidos);
            total = await filtrada.CountAsync(cancellationToken);
            elementos = ordenarPorEstado
                ? await PaginaOrdenadaPorEstadoAsync(filtrada, estadoPorCliente, request, total, cancellationToken)
                : ConEstado(await Proyectar(FiltrarPorEstado(ordenada, estadoPorCliente, estadosPedidos)
                        .Skip((request.Pagina - 1) * request.TamanoPagina)
                        .Take(request.TamanoPagina))
                    .ToListAsync(cancellationToken), estadoPorCliente);
        }

        // Una sola consulta para la página entera, no una por fila.
        var idsPagina = elementos.Select(c => c.Id).ToList();
        var clientesConAgenda = await contactosContext.ContactosAgenda
            .Where(contacto => contacto.ClienteId != null && idsPagina.Contains(contacto.ClienteId.Value))
            .Select(contacto => contacto.ClienteId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        var conAgenda = clientesConAgenda.ToHashSet();

        var centrosPorCliente = await centrosContext.Centros
            .Where(c => idsPagina.Contains(c.ClienteId))
            .GroupBy(c => c.ClienteId)
            .Select(g => new { ClienteId = g.Key, Cantidad = g.Count() })
            .ToDictionaryAsync(x => x.ClienteId, x => x.Cantidad, cancellationToken);

        // Solo lo pinta la pantalla, que pide el desglose: la API y los recorridos masivos no pagan el cálculo de estado.
        var clientesConBloqueo = request.ConDesgloseDocumental
            ? await ClientesConBloqueoDePlataformaAsync(idsPagina, cancellationToken)
            : [];

        var enriquecidos = elementos
            .Select(c => c with
            {
                SinContactoEnAgenda = !conAgenda.Contains(c.Id),
                Centros = centrosPorCliente.GetValueOrDefault(c.Id),
                Recuento = alertasPorCliente.TryGetValue(c.Id, out var alertas)
                    ? RecuentoAlertasClienteDto.De(alertas)
                    : RecuentoAlertasClienteDto.Vacio,
                BloqueoPlataformaCae = clientesConBloqueo.Contains(c.Id)
            })
            .Select(c => request.ConDesgloseDocumental ? ConDesglose(c, alertasPorCliente) : c)
            .ToList();

        return new ResultadoPaginado<ClienteListaDto>(enriquecidos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado,
            TotalSinFiltroDeEstado = request.ConRecuentosPorEstado ? totalSinEstado : null
        };
    }

    /// <summary>
    /// Clientes empresariales de la página con algún Centro de Trabajo en <see cref="EstadoCentro.Bloqueado"/>, con el
    /// mismo cálculo que la pastilla de cada Centro en Cliente 360 (<see cref="ICalculoEstadoCentroService"/>): una sola
    /// llamada para todos los Centros de la página.
    /// </summary>
    private async Task<HashSet<Guid>> ClientesConBloqueoDePlataformaAsync(
        IReadOnlyList<Guid> idsPagina, CancellationToken cancellationToken)
    {
        if (idsPagina.Count == 0) return [];

        var centros = await centrosContext.Centros
            .Where(c => idsPagina.Contains(c.ClienteId))
            .Select(c => new { c.Id, c.ClienteId })
            .ToListAsync(cancellationToken);
        if (centros.Count == 0) return [];

        var estados = await calculoEstadoCentro.CalcularAsync(centros.Select(c => c.Id).ToList(), cancellationToken);
        return centros
            .Where(c => estados.TryGetValue(c.Id, out var estado) && estado.Estado == EstadoCentro.Bloqueado)
            .Select(c => c.ClienteId)
            .ToHashSet();
    }

    /// <summary>
    /// Clientes empresariales por estado entre los que pasan los demás filtros (<paramref name="consulta"/>), con
    /// el mismo criterio que el filtro: un estado cuenta a quien tiene ALGUNA alerta en él, y <c>Vigente</c> a
    /// quien no tiene ninguna. Solo se traen los Id de quienes tienen alertas (pocos); el resto sale por resta.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, int>> ContarPorEstadoAsync(
        IQueryable<Empresa> consulta,
        Dictionary<Guid, (EstadoDocumento Peor, int Cantidad, HashSet<EstadoDocumento> EstadosPresentes)> estadoPorCliente,
        int totalSinEstado, CancellationToken cancellationToken)
    {
        var idsConAlertas = estadoPorCliente.Keys.ToList();
        var visiblesConAlertas = idsConAlertas.Count == 0
            ? []
            : await consulta.Where(c => idsConAlertas.Contains(c.Id)).Select(c => c.Id).ToListAsync(cancellationToken);
        var presentes = visiblesConAlertas.Select(id => estadoPorCliente[id].EstadosPresentes).ToList();

        var recuentos = new Dictionary<string, int>
        {
            [nameof(EstadoDocumento.Vencido)] = presentes.Count(p => p.Contains(EstadoDocumento.Vencido)),
            [nameof(EstadoDocumento.Faltante)] = presentes.Count(p => p.Contains(EstadoDocumento.Faltante)),
            [nameof(EstadoDocumento.Urgente)] = presentes.Count(p => p.Contains(EstadoDocumento.Urgente)),
            [nameof(EstadoDocumento.Proximo)] = presentes.Count(p => p.Contains(EstadoDocumento.Proximo)),
            [ObtenerClientesQuery.ClavePorVencer] = presentes.Count(p => p.Contains(EstadoDocumento.Urgente) || p.Contains(EstadoDocumento.Proximo)),
            [nameof(EstadoDocumento.Vigente)] = totalSinEstado - visiblesConAlertas.Count
        };
        return recuentos;
    }

    /// <summary>
    /// Los Clientes empresariales de <paramref name="consulta"/> que pasan el filtro por Estado documental, como
    /// condición SQL. Un estado pregunta si HAY alguna alerta en él, no si el PEOR estado es exactamente ese — un
    /// Cliente con Vencidos Y Urgentes a la vez (Vencido pesa más, ver <see cref="PrioridadEstado"/>) no puede
    /// desaparecer de «Por vencer» solo porque además tenga algo peor. <see cref="EstadoDocumento.Vigente"/> es el
    /// centinela de «sin incidencias»: nunca es un valor real de <c>EstadosPresentes</c> (<c>ObtenerAlertasQuery</c>
    /// no emite alertas Vigente), así que pide «sin ninguna alerta abierta» sin un parámetro bool aparte. Con varios
    /// estados pasa quien cumpla cualquiera. Mismo criterio que <see cref="ContarPorEstadoAsync"/>: la cifra de la
    /// franja y el filtro no pueden contar cosas distintas.
    /// </summary>
    private static IQueryable<Empresa> FiltrarPorEstado(
        IQueryable<Empresa> consulta,
        Dictionary<Guid, (EstadoDocumento Peor, int Cantidad, HashSet<EstadoDocumento> EstadosPresentes)> estadoPorCliente,
        HashSet<EstadoDocumento> estadosPedidos)
    {
        var idsQueCoinciden = estadoPorCliente
            .Where(par => par.Value.EstadosPresentes.Overlaps(estadosPedidos))
            .Select(par => par.Key)
            .ToList();
        if (!estadosPedidos.Contains(EstadoDocumento.Vigente))
            return consulta.Where(c => idsQueCoinciden.Contains(c.Id));

        var idsConAlertas = estadoPorCliente.Keys.ToList();
        return consulta.Where(c => idsQueCoinciden.Contains(c.Id) || !idsConAlertas.Contains(c.Id));
    }

    /// <summary>
    /// Solo las columnas que pinta la fila, no la <see cref="Empresa"/> entera. Los argumentos van
    /// todos por posición: un árbol de expresión no admite los opcionales del record.
    /// </summary>
    private static IQueryable<ClienteListaDto> Proyectar(IQueryable<Empresa> empresas) =>
        empresas.Select(c => new ClienteListaDto(
            c.Id, c.RazonSocial, c.Cif ?? string.Empty, c.EsCritico == true, c.CreadoEnUtc,
            false, c.EjecutivoUsuarioId, 0, null, 0));

    private static List<ClienteListaDto> ConEstado(
        List<ClienteListaDto> filas,
        Dictionary<Guid, (EstadoDocumento Peor, int Cantidad, HashSet<EstadoDocumento> EstadosPresentes)> estadoPorCliente) =>
        filas
            .Select(c => estadoPorCliente.TryGetValue(c.Id, out var estado)
                ? c with { EstadoDocumentalPeor = estado.Peor, EstadoDocumentalCantidad = estado.Cantidad }
                : c)
            .ToList();

    /// <summary>
    /// Las incidencias de una fila de la página: sus alertas, de la más grave a la menos (el mismo orden que
    /// <see cref="PrioridadEstado"/> da a la pastilla), y dentro de un estado la que antes vence. El resto de
    /// claves solo fijan el orden entre empates. No decide qué cuenta: son las alertas que ya agrupó
    /// <see cref="ObtenerEstadoDocumentalPorClienteAsync"/>, y la fila ya pasó el alcance de quien consulta.
    /// </summary>
    private static ClienteListaDto ConDesglose(ClienteListaDto fila, Dictionary<Guid, List<AlertaDto>> alertasPorCliente)
    {
        if (!alertasPorCliente.TryGetValue(fila.Id, out var alertas))
            return fila;

        return fila with
        {
            IncidenciasTotales = alertas.Count,
            Incidencias = alertas
                .OrderBy(a => PrioridadEstado(a.Estado))
                .ThenBy(a => a.FechaVencimiento)
                .ThenBy(a => a.TrabajadorNombre, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.TipoDocumentoNombre, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.IdDeFila(PrefijoDeClave), StringComparer.Ordinal)
                .Take(ObtenerClientesQuery.MaximoIncidenciasPorFila)
                .Select(a => new IncidenciaClienteDto(
                    a.IdDeFila(PrefijoDeClave), a.Estado, a.DocumentoId, a.TipoDocumentoId, a.TipoDocumentoNombre,
                    a.TrabajadorId!.Value, a.TrabajadorNombre, a.FechaVencimiento, a.CentroNombre))
                .ToList()
        };
    }

    private const string PrefijoDeClave = "incidencia";

    /// <summary>
    /// «Peor estado primero» (rediseño de listados, fase 1): ascendente es Vencido → Faltante →
    /// Urgente → Próximo → sin incidencias (orden único fijado el 2026-10-03: Vencido antes que Faltante). OrderBy de LINQ es estable: dentro de un mismo estado se
    /// conserva el orden de entrada (razón social, Id).
    /// </summary>
    private static List<ClienteListaDto> OrdenarPorEstado(List<ClienteListaDto> filas, bool descendente) =>
        descendente
            ? filas.OrderByDescending(PrioridadDeLaFila).ToList()
            : filas.OrderBy(PrioridadDeLaFila).ToList();

    /// <summary>
    /// Página del orden por Estado documental sin filtro de estado, exacta para cualquier tamaño de
    /// cartera. La lista se parte en dos tramos contiguos: los Clientes empresariales con alguna
    /// alerta abierta (pocos: se traen proyectados y se ordenan en memoria por prioridad) y los
    /// que no tienen ninguna («Al corriente», todos con la misma prioridad: se paginan en SQL por
    /// razón social). En ascendente va primero el tramo con alertas; en descendente, el otro.
    /// </summary>
    private static async Task<List<ClienteListaDto>> PaginaOrdenadaPorEstadoAsync(
        IQueryable<Empresa> consulta,
        Dictionary<Guid, (EstadoDocumento Peor, int Cantidad, HashSet<EstadoDocumento> EstadosPresentes)> estadoPorCliente,
        ObtenerClientesQuery request, int total, CancellationToken cancellationToken)
    {
        var idsConAlertas = estadoPorCliente.Keys.ToList();
        var conAlertas = OrdenarPorEstado(ConEstado(await Proyectar(consulta
                .Where(c => idsConAlertas.Contains(c.Id))
                .OrderBy(c => c.RazonSocial).ThenBy(c => c.Id))
            .ToListAsync(cancellationToken), estadoPorCliente), request.Descendente);
        var sinAlertas = consulta
            .Where(c => !idsConAlertas.Contains(c.Id))
            .OrderBy(c => c.RazonSocial).ThenBy(c => c.Id);
        var cuantosSinAlertas = Math.Max(0, total - conAlertas.Count);

        var desde = (request.Pagina - 1) * request.TamanoPagina;
        var pagina = new List<ClienteListaDto>(request.TamanoPagina);
        if (!request.Descendente)
        {
            pagina.AddRange(conAlertas.Skip(desde).Take(request.TamanoPagina));
            var faltan = request.TamanoPagina - pagina.Count;
            if (faltan > 0 && cuantosSinAlertas > 0)
                pagina.AddRange(await Proyectar(sinAlertas.Skip(Math.Max(0, desde - conAlertas.Count)).Take(faltan))
                    .ToListAsync(cancellationToken));
        }
        else
        {
            if (desde < cuantosSinAlertas)
                pagina.AddRange(await Proyectar(sinAlertas.Skip(desde).Take(Math.Min(request.TamanoPagina, cuantosSinAlertas - desde)))
                    .ToListAsync(cancellationToken));
            var faltan = request.TamanoPagina - pagina.Count;
            if (faltan > 0)
                pagina.AddRange(conAlertas.Skip(Math.Max(0, desde - cuantosSinAlertas)).Take(faltan));
        }

        return pagina;
    }

    /// <summary>
    /// "Estado documental" de Cliente (mockup "Lista Clientes TALVEG": "12
    /// vencidos" / "9 próximos" / "Al corriente") es el mismo agregado que ya
    /// alimenta "Requiere atención" en Inicio/Mi trabajo — reutiliza
    /// ObtenerAlertasQuery en vez de recorrer Centro→Asignación→Documento
    /// desde cero, para no duplicar la lógica de umbrales/estados. Vencido
    /// pesa más que Urgente, que pesa más que Próximo — el mismo orden que
    /// ObtenerBandejaGestorQueryHandler ya usa para priorizar.
    ///
    /// Devuelve además las alertas de cada Cliente empresarial, del mismo agrupado: el desglose de la fila
    /// (<see cref="ConDesglose"/>) no puede contar otra cosa que la pastilla.
    /// </summary>
    private async Task<(
        Dictionary<Guid, (EstadoDocumento Peor, int Cantidad, HashSet<EstadoDocumento> EstadosPresentes)> Estados,
        Dictionary<Guid, List<AlertaDto>> Alertas)> ObtenerEstadoDocumentalPorClienteAsync(
        CancellationToken cancellationToken)
    {
        var alertas = await mediator.Send(new ObtenerAlertasQuery(), cancellationToken);

        var grupos = alertas
            .Where(a => a.ClienteId is not null)
            .GroupBy(a => a.ClienteId!.Value)
            .ToList();

        var estados = grupos.ToDictionary(g => g.Key, g =>
        {
            var peor = g.Min(a => PrioridadEstado(a.Estado));
            var cantidad = g.Count(a => PrioridadEstado(a.Estado) == peor);
            var presentes = g.Select(a => a.Estado).ToHashSet();
            return (EstadoDeLaPrioridad(peor), cantidad, presentes);
        });

        return (estados, grupos.ToDictionary(g => g.Key, g => g.ToList()));
    }

    /// <summary>Sin alertas abiertas («Al corriente») pesa lo mismo que Vigente: va el último.</summary>
    private static int PrioridadDeLaFila(ClienteListaDto fila) =>
        PrioridadEstado(fila.EstadoDocumentalPeor ?? EstadoDocumento.Vigente);

    private static int PrioridadEstado(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vencido => 0,
        EstadoDocumento.Faltante => 1,
        EstadoDocumento.Urgente => 2,
        EstadoDocumento.Proximo => 3,
        _ => 4
    };

    private static EstadoDocumento EstadoDeLaPrioridad(int prioridad) => prioridad switch
    {
        0 => EstadoDocumento.Vencido,
        1 => EstadoDocumento.Faltante,
        2 => EstadoDocumento.Urgente,
        3 => EstadoDocumento.Proximo,
        _ => EstadoDocumento.Vigente
    };
}
