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

public record ObtenerClientesQuery(
    string? Busqueda, bool? SoloCriticos, Guid? EjecutivoUsuarioId = null, EstadoDocumento? EstadoDocumental = null,
    int Pagina = 1, int TamanoPagina = 20, string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<ClienteListaDto>>;

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
/// ("Al día", mismo criterio que "nada pendiente" en Inicio/Mi trabajo: la
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

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(c => c.RazonSocial.ToUpper().Contains(busqueda));
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

        // Sin filtro de Estado documental: paginación normal en SQL. Con
        // filtro: el estado es un agregado calculado (no una columna), así
        // que hace falta materializar candidatos de sobra, calcular su
        // agregado y paginar en memoria — acotado a un límite razonable
        // (mismo principio que otros filtros calculados de este código base)
        // en vez de traer toda la cartera.
        const int limiteCandidatosConFiltroCalculado = 2000;

        List<Empresa> candidatos;
        int total;
        if (request.EstadoDocumental is null)
        {
            candidatos = await ordenada
                .Skip((request.Pagina - 1) * request.TamanoPagina)
                .Take(request.TamanoPagina)
                .ToListAsync(cancellationToken);
            total = totalSinEstado;
        }
        else
        {
            candidatos = await ordenada.Take(limiteCandidatosConFiltroCalculado).ToListAsync(cancellationToken);
            total = -1; // se recalcula abajo, tras aplicar el filtro de estado.
        }

        var estadoPorCliente = await ObtenerEstadoDocumentalPorClienteAsync(cancellationToken);

        var todosLosCandidatos = candidatos
            .Select(c => new ClienteListaDto(c.Id, c.RazonSocial, c.Cif ?? string.Empty, c.EsCritico == true, c.CreadoEnUtc,
                EjecutivoUsuarioId: c.EjecutivoUsuarioId))
            .Select(c => estadoPorCliente.TryGetValue(c.Id, out var estado)
                ? c with { EstadoDocumentalPeor = estado.Peor, EstadoDocumentalCantidad = estado.Cantidad }
                : c)
            .ToList();

        List<ClienteListaDto> elementos;
        if (request.EstadoDocumental is null)
        {
            elementos = todosLosCandidatos;
        }
        else
        {
            // "Con vencidos" pregunta si HAY algún Vencido, no si el PEOR
            // estado es exactamente Vencido — un Cliente con Faltantes Y
            // Vencidos a la vez (Faltante pesa más, ver PrioridadEstado) no
            // puede desaparecer del filtro "Con vencidos" solo porque además
            // tenga algo peor. EstadoDocumento.Vigente como centinela de "Al
            // día" (mockup "Lista Clientes TALVEG"): nunca es un valor real
            // de EstadosPresentes (ObtenerAlertasQuery no emite alertas
            // Vigente), así que sirve para pedir "sin ninguna alerta
            // abierta" sin un cuarto parámetro de tipo bool aparte.
            var filtrados = request.EstadoDocumental == EstadoDocumento.Vigente
                ? todosLosCandidatos.Where(c => c.EstadoDocumentalPeor is null).ToList()
                : todosLosCandidatos
                    .Where(c => estadoPorCliente.TryGetValue(c.Id, out var estado)
                        && estado.EstadosPresentes.Contains(request.EstadoDocumental.Value))
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

        return new ResultadoPaginado<ClienteListaDto>(enriquecidos, total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// "Estado documental" de Cliente (mockup "Lista Clientes TALVEG": "12
    /// vencidos" / "9 próximos" / "Al día") es el mismo agregado que ya
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

    private static int PrioridadEstado(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Faltante => 0,
        EstadoDocumento.Vencido => 1,
        EstadoDocumento.Urgente => 2,
        EstadoDocumento.Proximo => 3,
        _ => 4
    };

    private static EstadoDocumento EstadoDeLaPrioridad(int prioridad) => prioridad switch
    {
        0 => EstadoDocumento.Faltante,
        1 => EstadoDocumento.Vencido,
        2 => EstadoDocumento.Urgente,
        3 => EstadoDocumento.Proximo,
        _ => EstadoDocumento.Vigente
    };
}
