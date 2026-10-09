using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using CaeManager.Application.Empresas;
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
public record ObtenerClientesQuery(
    string? Busqueda, bool? SoloCriticos, Guid? EjecutivoUsuarioId = null, EstadoDocumento? EstadoDocumental = null,
    int Pagina = 1, int TamanoPagina = 20, string? OrdenarPor = null, bool Descendente = false,
    IReadOnlyCollection<EstadoDocumento>? EstadosDocumentales = null, bool ConRecuentosPorEstado = false,
    Guid? Id = null)
    : IRequest<ResultadoPaginado<ClienteListaDto>>
{
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
/// El peor estado entre todas las alertas de vigencia (Vencido/Urgente/Próximo)
/// de los Trabajadores cuyo Cliente principal es este (ver AlertaDto.ClienteId,
/// IResolverClientePrincipalService) — null si no tiene ninguna alerta abierta
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
    int EstadoDocumentalCantidad = 0);

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
    ICentrosQueryContext centrosContext, IMediator mediator, IAlcanceDatosService alcanceDatos)
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
        // puede calcular tras resolver las alertas (más abajo) — se aplica
        // como segundo paso, no en SQL, igual que el resto de este método
        // hace la paginación en SQL pero el enriquecido en memoria por lote.
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
        var estadoPorCliente = await ObtenerEstadoDocumentalPorClienteAsync(cancellationToken);
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
            // Filtro por Estado documental: sigue acotado a un límite razonable de candidatos
            // (mismo principio que otros filtros calculados de este código base) en vez de traer
            // toda la cartera; el total es el de los que coinciden entre esos candidatos. Con
            // orden por estado, OrderBy de LINQ es estable y la razón social (rama por defecto
            // del switch de arriba) queda como desempate.
            const int limiteCandidatosConFiltroCalculado = 2000;
            var candidatos = ConEstado(await Proyectar(ordenada.Take(limiteCandidatosConFiltroCalculado))
                .ToListAsync(cancellationToken), estadoPorCliente);
            if (ordenarPorEstado)
                candidatos = OrdenarPorEstado(candidatos, request.Descendente);

            // Un estado pregunta si HAY alguna alerta en él, no si el PEOR
            // estado es exactamente ese — un Cliente con Vencidos Y
            // Urgentes a la vez (Vencido pesa más, ver PrioridadEstado) no
            // puede desaparecer de «Por vencer» solo porque además tenga
            // algo peor. EstadoDocumento.Vigente como centinela de «sin
            // incidencias»: nunca es un valor real de EstadosPresentes
            // (ObtenerAlertasQuery no emite alertas Vigente), así que sirve
            // para pedir «sin ninguna alerta abierta» sin un parámetro de
            // tipo bool aparte. Con varios estados pasa quien cumpla
            // cualquiera.
            var pideSinAlertas = estadosPedidos.Contains(EstadoDocumento.Vigente);
            var filtrados = candidatos
                .Where(c => estadoPorCliente.TryGetValue(c.Id, out var estado)
                    ? estado.EstadosPresentes.Overlaps(estadosPedidos)
                    : pideSinAlertas)
                .ToList();
            total = filtrados.Count;
            elementos = filtrados.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList();
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

        var enriquecidos = elementos
            .Select(c => c with
            {
                SinContactoEnAgenda = !conAgenda.Contains(c.Id),
                Centros = centrosPorCliente.GetValueOrDefault(c.Id)
            })
            .ToList();

        return new ResultadoPaginado<ClienteListaDto>(enriquecidos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado,
            TotalSinFiltroDeEstado = request.ConRecuentosPorEstado ? totalSinEstado : null
        };
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
    /// </summary>
    private async Task<Dictionary<Guid, (EstadoDocumento Peor, int Cantidad, HashSet<EstadoDocumento> EstadosPresentes)>> ObtenerEstadoDocumentalPorClienteAsync(
        CancellationToken cancellationToken)
    {
        var alertas = await mediator.Send(new ObtenerAlertasQuery(), cancellationToken);

        return alertas
            .Where(a => a.ClienteId is not null)
            .GroupBy(a => a.ClienteId!.Value)
            .ToDictionary(g => g.Key, g =>
            {
                var peor = g.Min(a => PrioridadEstado(a.Estado));
                var cantidad = g.Count(a => PrioridadEstado(a.Estado) == peor);
                var presentes = g.Select(a => a.Estado).ToHashSet();
                return (EstadoDeLaPrioridad(peor), cantidad, presentes);
            });
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
