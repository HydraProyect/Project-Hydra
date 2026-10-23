using CaeManager.Domain.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresas;

/// <summary>
/// Listado de Empresas propias del Tenant propietario. Los Clientes empresariales y las Empresas
/// subcontratistas se consultan en sus listados separados; este filtro también delimita total,
/// recuentos por estado, exportación y refresco de una fila por id.
///
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla:
/// una Empresa no tiene estado propio, se deriva del peor estado de vigencia
/// de sus Documentos (ver <see cref="ICalculoEstadoDocumentalService"/>) con los requisitos OBLIGATORIOS de ámbito
/// Empresa incumplidos de los Centros visibles donde opera aplicados encima
/// (<see cref="RequisitosObligatoriosIncumplidos"/> y <see cref="SeveridadEstadoDocumento.ConFaltantes"/>: Vencido &gt;
/// Pendiente &gt; Por vencer &gt; Sin confirmar &gt; Sin incidencias; decisión del 2026-10-23). Lo exigido sin la marca
/// «bloquea el acceso» no tiñe, y lo que les falta a sus Trabajadores tampoco: cada nivel pinta su contexto.
///
/// <para>
/// <paramref name="ConDesgloseDocumental"/> rellena, para las filas de la página, las incidencias de
/// <see cref="EmpresaListaDto"/>. Cuesta una consulta de documentos por página, así que hay que pedirlo: lo pide
/// solo quien lo pinta (el listado de Empresas, en su carga de página y al refrescar una fila por id). Sin
/// pedirlo, las filas llegan con las incidencias vacías.
/// </para>
///
/// <para>
/// <paramref name="ClienteEmpresarialId"/> deja solo las Empresas que prestan servicio a ese Cliente empresarial en una
/// Relación Empresarial vigente, y <paramref name="ConClientePrincipal"/> rellena
/// <see cref="EmpresaListaDto.ClientePrincipal"/> (la agrupación «Por Cliente» de la pantalla). Las dos preguntas
/// tocan la cartera COMERCIAL de la contratista —a qué Clientes sirve—, así que solo ven lo que el usuario
/// alcanza: Empresas de su alcance de GESTIÓN (REC-153) y Clientes de su alcance de lectura. Fuera de ahí la
/// Empresa no sale con el filtro ni lleva Cliente, aunque la relación exista. <c>OrdenarPor =
/// ClientePrincipal</c> ordena por ese Cliente (sin Cliente, al final), que es lo que hace contiguos los grupos.
/// </para>
/// </summary>
public record ObtenerEmpresasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null,
    bool ConRecuentosPorEstado = false, Guid? EmpresaId = null, bool ConDesgloseDocumental = false,
    Guid? ClienteEmpresarialId = null, bool ConClientePrincipal = false)
    : IRequest<ResultadoPaginado<EmpresaListaDto>>;

/// <param name="CumplimientoPorcentaje">
/// % de cumplimiento agregado de la Empresa (Centro 360, Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md
/// § 0.8/0.11) — mismo cálculo que <c>ObtenerCumplimientoEmpresaQuery</c>,
/// batcheado aquí para toda la página en vez de una consulta por fila.
/// <c>null</c> cuando la Empresa no tiene actividad en ningún Centro o
/// ninguno de esos Centros tiene requisitos aplicables.
/// </param>
/// <param name="DeteccionesPendientes">
/// Nº de altas/bajas de personal detectadas por IA sin resolver (Centro 360,
/// Project-Hydra-Negocio/tecnico/docs/ux-audit/03-empresas-subcontratas.md H2) — mismo dato que
/// <c>ObtenerDeteccionesPorEmpresaQuery</c>, batcheado aquí para toda la
/// página. Antes solo visible desde una notificación transitoria.
/// </param>
public record EmpresaListaDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc,
    EstadoDocumento? EstadoDocumental = null, int? CumplimientoPorcentaje = null, int DeteccionesPendientes = 0)
{
    /// <summary>
    /// Los documentos de ámbito Empresa que explican su <see cref="EstadoDocumental"/> y los demás que piden
    /// atención, del más grave al menos. Son los mismos documentos que deciden el estado de la fila: los de la
    /// propia Empresa, no los de sus Trabajadores ni los de sus Vehículos. Vacía si no hay ninguno o si la
    /// consulta no pidió el desglose.
    /// </summary>
    public IReadOnlyList<IncidenciaDocumentalDto> Incidencias { get; init; } = [];

    /// <summary>
    /// Nombres de los Tipos de documento de ámbito Empresa que algún Centro donde opera la Empresa marca como OBLIGATORIOS
    /// («bloquea el acceso») y de los que no tiene ningún documento que valga (<see cref="RequisitosObligatoriosIncumplidos"/>,
    /// decisión del 2026-10-23). Sin repetir y por nombre. Se rellenan siempre: con un requisito obligatorio incumplido
    /// (ausente o vencido en ese Centro) <see cref="EstadoDocumental"/> ya llega como <see cref="EstadoDocumento.Faltante"/>
    /// («Pendiente») salvo que haya un vencido. Lo exigido sin esa marca no está aquí ni tiñe. Solo Centros visibles para el usuario.
    /// </summary>
    public IReadOnlyList<string> DocumentosFaltantes { get; init; } = [];

    /// <summary>
    /// El Cliente empresarial bajo el que la agrupación «Por Cliente» coloca la fila: el primero por razón social
    /// de los Clientes a los que presta servicio la Empresa (el del filtro, si lo hay) y que el usuario alcanza.
    /// Una Empresa con varios Clientes sale una sola vez, no una por Cliente. Nulo si no tiene ninguno a su
    /// alcance o si la consulta no lo pidió (<see cref="ObtenerEmpresasQuery.ConClientePrincipal"/>).
    /// </summary>
    public string? ClientePrincipal { get; init; }
}

public class ObtenerEmpresasQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    IAsignacionesQueryContext asignacionesContext, ITrabajadoresQueryContext trabajadoresContext,
    ICalculoEstadoCentroService calculoEstadoCentro, IRequisitosObligatoriosIncumplidosService requisitosObligatorios)
    : IRequestHandler<ObtenerEmpresasQuery, ResultadoPaginado<EmpresaListaDto>>
{
    public async Task<ResultadoPaginado<EmpresaListaDto>> Handle(ObtenerEmpresasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.Where(e => e.EsPropia);

        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(e => empresaIdsVisibles.Contains(e.Id));

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // Va después del alcance: solo estrecha.
        if (request.EmpresaId is not null)
            consulta = consulta.Where(e => e.Id == request.EmpresaId);

        // Cartera comercial (Clientes a los que sirve cada Empresa): solo se consulta si se pide el filtro,
        // la agrupación o ese orden, y siempre acotada al alcance (ver ClientesAlcanzadosAsync). Sin pedirlo es
        // una consulta vacía: no cuesta nada ni se traduce a SQL.
        var ordenaPorCliente = string.Equals(
            request.OrdenarPor, nameof(EmpresaListaDto.ClientePrincipal), StringComparison.Ordinal);
        var clientesAlcanzados = request.ClienteEmpresarialId is not null || request.ConClientePrincipal || ordenaPorCliente
            ? await ClientesAlcanzadosAsync(request.ClienteEmpresarialId, cancellationToken)
            : ClientesVigentesDeEmpresas.Todas(dbContext).Where(c => false);
        if (request.ClienteEmpresarialId is not null)
            consulta = consulta.Where(e => clientesAlcanzados.Any(c => c.EmpresaId == e.Id));

        // Razón social o CIF, como ObtenerSubcontratasQuery: el buscador de la lista promete los dos.
        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda;
            consulta = consulta.Where(e => TextoDeBusqueda.Contiene(e.RazonSocial, busqueda)
                || (e.Cif != null && TextoDeBusqueda.Contiene(e.Cif, busqueda)));
        }

        // Filtrar u ordenar por el estado documental obliga a conocer, de cada
        // Empresa visible, el peor vencimiento de sus Documentos — pero ese
        // agregado (MIN por propietario) se pide a SQL con una subconsulta
        // correlacionada en vez de materializar todas las Empresas visibles
        // (hallazgo Módulo 8, PR #389 § 4.1), igual que ObtenerTrabajadoresQuery
        // y que ObtenerDocumentosQuery traduce su filtro de Estado a fechas.
        var necesitaEstadoCompleto =
            !string.IsNullOrWhiteSpace(request.EstadoDocumental) ||
            string.Equals(request.OrdenarPor, nameof(EmpresaListaDto.EstadoDocumental), StringComparison.Ordinal) ||
            request.ConRecuentosPorEstado;

        if (necesitaEstadoCompleto)
        {
            var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
            var hoy = DiaDeNegocio.Hoy();
            var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);
            var limiteAmbar = hoy.AddDays(parametros.UmbralAmbarDias);

            // Los requisitos obligatorios incumplidos de todas las Empresas de la consulta, antes de paginar: entran en la
            // clave de estado igual que en ObtenerTrabajadoresQuery (decisión del 2026-10-23). Solo se materializan los Ids
            // con alguno, nunca el listado.
            var incumplidos = await requisitosObligatorios.DeEmpresasAsync(consulta.Select(e => e.Id), cancellationToken);
            var idsConIncumplidos = incumplidos.Keys.ToList();
            var claveVencido = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vencido);
            var claveFaltante = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Faltante);
            var claveUrgente = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Urgente);
            var claveProximo = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Proximo);
            var claveSinConfirmar = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.SinConfirmar);
            var claveVigente = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vigente);
            var claveSinCaducidad = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.SinCaducidad);

            var conFecha =
                from e in consulta
                select new
                {
                    e.Id,
                    e.RazonSocial,
                    e.Cif,
                    e.CreadoEnUtc,
                    // Solo con el orden por Cliente: sin él es un nulo constante y no entra en el SQL.
                    ClientePrincipal = ordenaPorCliente
                        ? clientesAlcanzados.Where(c => c.EmpresaId == e.Id).Min(c => c.RazonSocial)
                        : null,
                    PeorFecha = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Where(d => d.EmpresaId == e.Id)
                        .Min(d => (DateOnly?)d.FechaVencimiento),
                    // MIN ignora las fechas nulas, que son a la vez «no caduca» y «sin
                    // confirmar»: lo sin confirmar se cuenta aparte para no perderlo.
                    HaySinConfirmar = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Any(d => d.EmpresaId == e.Id && d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
                };

            // La clave de estado es la misma con la que se ordena (EstadoDocumentalFiltro.ClaveOrdenConFaltantes:
            // Vencido, Pendiente por requisito obligatorio, Urgente, Próximo, Sin confirmar, Vigente, Sin caducidad),
            // escrita en línea porque tiene que traducirse a SQL. Contar y filtrar por ella garantiza que la franja de
            // estado, el filtro y el orden parten las filas igual.
            var conEstado = conFecha.Select(e => new
            {
                e.Id,
                e.RazonSocial,
                e.Cif,
                e.CreadoEnUtc,
                e.ClientePrincipal,
                e.PeorFecha,
                e.HaySinConfirmar,
                ClaveEstado = e.PeorFecha != null && e.PeorFecha < hoy ? claveVencido
                    : idsConIncumplidos.Contains(e.Id) ? claveFaltante
                    : e.PeorFecha != null && e.PeorFecha <= limiteRojo ? claveUrgente
                    : e.PeorFecha != null && e.PeorFecha <= limiteAmbar ? claveProximo
                    : e.HaySinConfirmar ? claveSinConfirmar
                    : e.PeorFecha != null ? claveVigente
                    : claveSinCaducidad
            });

            // Los recuentos se toman ANTES de filtrar por estado y después de todos los demás filtros: cada
            // cifra de la franja dice cuántas filas quedarían al marcar solo ese estado.
            IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
            if (request.ConRecuentosPorEstado)
            {
                recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(await conEstado
                    .GroupBy(e => e.ClaveEstado)
                    .Select(grupo => new { Clave = grupo.Key, Filas = grupo.Count() })
                    .ToDictionaryAsync(grupo => grupo.Clave, grupo => grupo.Filas, cancellationToken), incluirFaltantes: true);
            }

            // Varios estados a la vez (la franja deja marcar más de uno). null: el filtro no descarta nada
            // (vacío o sin ningún valor conocido). Lista vacía: ninguna fila — «sin documentos» (este listado
            // nunca produce un estado null: sin Documentos cae en Sin caducidad) o un estado que una Empresa
            // nunca tiene (En tolerancia). Un filtro válido pero no aplicable devuelve nada, no todo.
            if (EstadoDocumentalFiltro.ClavesDeOrden(request.EstadoDocumental, incluirFaltantes: true) is { } clavesPedidas)
                conEstado = conEstado.Where(e => clavesPedidas.Contains(e.ClaveEstado));

            var totalConEstado = await conEstado.CountAsync(cancellationToken);

            // Este camino se toma también solo para contar por estado (la franja lo pide siempre), así que el
            // orden no puede darse por supuesto: por estado si es la columna pedida, y si no por la misma lista
            // blanca que el camino sin estado.
            var porEstado = request.Descendente
                ? conEstado.OrderByDescending(e => e.ClaveEstado)
                : conEstado.OrderBy(e => e.ClaveEstado);
            var ordenaPorEstado = string.Equals(
                request.OrdenarPor, nameof(EmpresaListaDto.EstadoDocumental), StringComparison.Ordinal);
            var ordenadaConEstado = ordenaPorEstado
                ? porEstado.ThenBy(e => e.RazonSocial)
                : (request.OrdenarPor, request.Descendente) switch
                {
                    // Agrupar por Cliente: los de un mismo Cliente seguidos, las sin Cliente al final.
                    (nameof(EmpresaListaDto.ClientePrincipal), _) => conEstado
                        .OrderBy(e => e.ClientePrincipal == null).ThenBy(e => e.ClientePrincipal).ThenBy(e => e.RazonSocial),
                    (nameof(EmpresaListaDto.RazonSocial), true) => conEstado.OrderByDescending(e => e.RazonSocial),
                    (nameof(EmpresaListaDto.Cif), false) => conEstado.OrderBy(e => e.Cif),
                    (nameof(EmpresaListaDto.Cif), true) => conEstado.OrderByDescending(e => e.Cif),
                    (nameof(EmpresaListaDto.CreadoEnUtc), false) => conEstado.OrderBy(e => e.CreadoEnUtc),
                    (nameof(EmpresaListaDto.CreadoEnUtc), true) => conEstado.OrderByDescending(e => e.CreadoEnUtc),
                    _ => conEstado.OrderBy(e => e.RazonSocial)
                };
            var ordenadaFinal = ordenadaConEstado.ThenBy(e => e.Id);

            var paginaConEstado = await ordenadaFinal
                .Skip((request.Pagina - 1) * request.TamanoPagina)
                .Take(request.TamanoPagina)
                .ToListAsync(cancellationToken);

            var idsPaginaConEstado = paginaConEstado.Select(e => e.Id).ToList();
            var cumplimientoConEstado = await CalcularCumplimientoPorEmpresaAsync(idsPaginaConEstado, cancellationToken);
            var deteccionesConEstado = await ContarDeteccionesPendientesPorEmpresaAsync(idsPaginaConEstado, cancellationToken);

            return new ResultadoPaginado<EmpresaListaDto>(
                await ConClientePrincipalAsync(
                    await ConDesgloseAsync(
                        paginaConEstado.Select(e => ConIncumplidos(
                            new EmpresaListaDto(
                                e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc,
                                CalculoEstadoDocumentalService.PeorEstado(e.PeorFecha, e.HaySinConfirmar, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                                cumplimientoConEstado.GetValueOrDefault(e.Id),
                                deteccionesConEstado.GetValueOrDefault(e.Id)),
                            incumplidos))
                            .ToList(),
                        request, cancellationToken),
                    request, clientesAlcanzados, cancellationToken),
                totalConEstado, request.Pagina, request.TamanoPagina)
            {
                RecuentosPorEstado = recuentosPorEstado
            };
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(EmpresaListaDto.ClientePrincipal), _) => consulta
                .OrderBy(e => clientesAlcanzados.Where(c => c.EmpresaId == e.Id).Min(c => c.RazonSocial) == null)
                .ThenBy(e => clientesAlcanzados.Where(c => c.EmpresaId == e.Id).Min(c => c.RazonSocial))
                .ThenBy(e => e.RazonSocial),
            (nameof(EmpresaListaDto.RazonSocial), true) => consulta.OrderByDescending(e => e.RazonSocial),
            (nameof(EmpresaListaDto.Cif), false) => consulta.OrderBy(e => e.Cif),
            (nameof(EmpresaListaDto.Cif), true) => consulta.OrderByDescending(e => e.Cif),
            (nameof(EmpresaListaDto.CreadoEnUtc), false) => consulta.OrderBy(e => e.CreadoEnUtc),
            (nameof(EmpresaListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(e => e.CreadoEnUtc),
            _ => consulta.OrderBy(e => e.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(e => e.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(e => new EmpresaListaDto(e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        // Solo para las de la página: los badges de la tabla, no un filtro.
        var idsPagina = elementos.Select(e => e.Id).ToList();
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Empresa, idsPagina, cancellationToken);
        var cumplimiento = await CalcularCumplimientoPorEmpresaAsync(idsPagina, cancellationToken);
        var detecciones = await ContarDeteccionesPendientesPorEmpresaAsync(idsPagina, cancellationToken);
        var incumplidosPagina = idsPagina.Count == 0
            ? RequisitosObligatoriosIncumplidos.Ninguno.PorEmpresa
            : await requisitosObligatorios.DeEmpresasAsync(dbContext.Empresas.Where(e => idsPagina.Contains(e.Id)).Select(e => e.Id), cancellationToken);

        return new ResultadoPaginado<EmpresaListaDto>(
            await ConClientePrincipalAsync(
                await ConDesgloseAsync(
                    elementos.Select(e => ConIncumplidos(
                        e with
                        {
                            EstadoDocumental = estados.GetValueOrDefault(e.Id),
                            CumplimientoPorcentaje = cumplimiento.GetValueOrDefault(e.Id),
                            DeteccionesPendientes = detecciones.GetValueOrDefault(e.Id)
                        },
                        incumplidosPagina)).ToList(),
                    request, cancellationToken),
                request, clientesAlcanzados, cancellationToken),
            total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// Los Clientes empresariales que el usuario puede ver en la cartera de las Empresas que gestiona: la
    /// relación vigente Empresa→Cliente (<see cref="ClientesVigentesDeEmpresas"/>) recortada por el alcance de
    /// GESTIÓN de las Empresas (REC-153: el usuario de portal obtiene lista vacía, porque la cartera comercial
    /// de una contratista no es contenido de portal) y por el de lectura de los Clientes. Con
    /// <paramref name="clienteEmpresarialId"/>, solo ese. Es la única fuente del filtro, de la agrupación y del orden por
    /// Cliente: ninguna ve una relación que esta consulta no deje pasar.
    /// </summary>
    private async Task<IQueryable<ClienteVigenteDeEmpresa>> ClientesAlcanzadosAsync(
        Guid? clienteEmpresarialId, CancellationToken cancellationToken)
    {
        var clientes = ClientesVigentesDeEmpresas.Todas(dbContext);

        var empresasGestionadas = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        if (empresasGestionadas is not null)
            clientes = clientes.Where(c => empresasGestionadas.Contains(c.EmpresaId));

        var clientesVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clientesVisibles is not null)
            clientes = clientes.Where(c => clientesVisibles.Contains(c.ClienteEmpresarialId));

        if (clienteEmpresarialId is { } id)
            clientes = clientes.Where(c => c.ClienteEmpresarialId == id);

        return clientes;
    }

    /// <summary>
    /// Rellena <see cref="EmpresaListaDto.ClientePrincipal"/> de las filas de la página con el MISMO criterio con
    /// el que <c>OrdenarPor = ClientePrincipal</c> las ordenó (el primero por razón social, calculado por la base
    /// de datos: con otra comparación un grupo podría partirse). Una consulta para toda la página.
    /// </summary>
    private async Task<IReadOnlyList<EmpresaListaDto>> ConClientePrincipalAsync(
        IReadOnlyList<EmpresaListaDto> pagina, ObtenerEmpresasQuery request,
        IQueryable<ClienteVigenteDeEmpresa> clientesAlcanzados, CancellationToken cancellationToken)
    {
        if (!request.ConClientePrincipal || pagina.Count == 0)
            return pagina;

        var ids = pagina.Select(e => e.Id).ToList();
        var principales = await clientesAlcanzados
            .Where(c => ids.Contains(c.EmpresaId))
            .GroupBy(c => c.EmpresaId)
            .Select(g => new { EmpresaId = g.Key, Principal = g.Min(c => c.RazonSocial) })
            .ToDictionaryAsync(x => x.EmpresaId, x => x.Principal, cancellationToken);

        return pagina.Select(e => e with { ClientePrincipal = principales.GetValueOrDefault(e.Id) }).ToList();
    }

    /// <summary>
    /// Añade a las filas YA paginadas su desglose documental, en una sola consulta de documentos para toda la
    /// página. Las filas son las que dejaron pasar el alcance y los filtros: el desglose no añade ninguna. No
    /// toca <see cref="EmpresaListaDto.EstadoDocumental"/>: sale de los mismos documentos (los de ámbito Empresa)
    /// y la misma calculadora, así que coinciden cuando la Empresa tiene alguno (lo ata
    /// <c>DesgloseDocumentalDeEmpresasBajoRlsTests</c>). Una Empresa sin documentos no trae desglose y conserva
    /// el estado que le dé cada camino.
    /// </summary>
    private async Task<IReadOnlyList<EmpresaListaDto>> ConDesgloseAsync(
        IReadOnlyList<EmpresaListaDto> pagina, ObtenerEmpresasQuery request, CancellationToken cancellationToken)
    {
        if (!request.ConDesgloseDocumental || pagina.Count == 0)
            return pagina;

        var desgloses = await calculoEstadoDocumental.CalcularDesgloseAsync(
            AmbitoAplicacion.Empresa, pagina.Select(e => e.Id).ToList(), cancellationToken);

        return pagina
            .Select(e => desgloses.TryGetValue(e.Id, out var desglose) ? e with { Incidencias = desglose.Incidencias } : e)
            .ToList();
    }

    /// <summary>
    /// Mismo cálculo que <c>ObtenerCumplimientoEmpresaQuery</c> pero para N Empresas de una sola vez: una consulta de
    /// actividad (los Centros donde trabajan sus Trabajadores) y una llamada a
    /// <see cref="ICalculoEstadoCentroService.ObtenerParesExigidosAsync"/> sobre la unión de Centros, en vez de repetir
    /// ambas por fila. Es el contexto <see cref="ContextoCumplimiento.Empresa"/>: solo cuentan los pares de los
    /// Trabajadores de la Empresa, no los de otras Empresas que comparten Centro con ella.
    /// </summary>
    private async Task<Dictionary<Guid, int?>> CalcularCumplimientoPorEmpresaAsync(
        IReadOnlyList<Guid> empresaIds, CancellationToken cancellationToken)
    {
        if (empresaIds.Count == 0) return new Dictionary<Guid, int?>();

        var centroIds = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where trabajador.EmpresaId != null && empresaIds.Contains(trabajador.EmpresaId.Value)
            select asignacion.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var pares = centroIds.Count == 0
            ? []
            : await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken);
        var porEmpresa = CumplimientoDocumental.PorContexto(ContextoCumplimiento.Empresa, pares);

        return empresaIds.Distinct().ToDictionary(id => id, id => porEmpresa.GetValueOrDefault(id)?.Porcentaje);
    }

    /// <summary>
    /// Aplica a una fila los requisitos obligatorios incumplidos de la Empresa (<see cref="RequisitosObligatoriosIncumplidos"/>):
    /// con alguno, el estado pasa a Pendiente salvo que ya haya un vencido (<see cref="SeveridadEstadoDocumento.ConFaltantes"/>,
    /// la misma combinación que el estado de Trabajador), y los Tipos que faltan dan el motivo de la fila.
    /// </summary>
    private static EmpresaListaDto ConIncumplidos(
        EmpresaListaDto fila, IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> incumplidos) =>
        incumplidos.TryGetValue(fila.Id, out var deLaEmpresa)
            ? fila with
            {
                EstadoDocumental = SeveridadEstadoDocumento.ConFaltantes(fila.EstadoDocumental, hayFaltantes: true),
                DocumentosFaltantes = RequisitosObligatoriosIncumplidos.NombresAusentes(deLaEmpresa)
            }
            : fila;

    /// <summary>
    /// Mismo dato que <c>ObtenerDeteccionesPorEmpresaQuery</c> (Project-Hydra-Negocio/tecnico/docs/ux-audit/03-empresas-subcontratas.md
    /// H2) pero contado en lote para toda la página, no una consulta por fila.
    /// </summary>
    private async Task<Dictionary<Guid, int>> ContarDeteccionesPendientesPorEmpresaAsync(
        IReadOnlyList<Guid> empresaIds, CancellationToken cancellationToken)
    {
        if (empresaIds.Count == 0) return new Dictionary<Guid, int>();

        return await trabajadoresContext.DeteccionesTrabajador
            .Where(d => !d.Resuelta && empresaIds.Contains(d.EmpresaId))
            .GroupBy(d => d.EmpresaId)
            .Select(g => new { EmpresaId = g.Key, Cantidad = g.Count() })
            .ToDictionaryAsync(x => x.EmpresaId, x => x.Cantidad, cancellationToken);
    }
}
