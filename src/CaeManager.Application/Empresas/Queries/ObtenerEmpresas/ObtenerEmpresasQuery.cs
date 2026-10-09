using CaeManager.Domain.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresas;

/// <summary>
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla:
/// una Empresa no tiene estado propio, se deriva del peor estado de vigencia
/// de sus Documentos (ver <see cref="ICalculoEstadoDocumentalService"/>).
/// </summary>
public record ObtenerEmpresasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null,
    bool ConRecuentosPorEstado = false, Guid? EmpresaId = null)
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
    EstadoDocumento? EstadoDocumental = null, int? CumplimientoPorcentaje = null, int DeteccionesPendientes = 0);

public class ObtenerEmpresasQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos,
    ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    IAsignacionesQueryContext asignacionesContext, ITrabajadoresQueryContext trabajadoresContext,
    ICalculoEstadoCentroService calculoEstadoCentro)
    : IRequestHandler<ObtenerEmpresasQuery, ResultadoPaginado<EmpresaListaDto>>
{
    public async Task<ResultadoPaginado<EmpresaListaDto>> Handle(ObtenerEmpresasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.AsQueryable();

        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(e => empresaIdsVisibles.Contains(e.Id));

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // Va después del alcance: solo estrecha.
        if (request.EmpresaId is not null)
            consulta = consulta.Where(e => e.Id == request.EmpresaId);

        // Razón social o CIF, como ObtenerSubcontratasQuery: el buscador de la lista promete los dos.
        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(e => e.RazonSocial.ToUpper().Contains(busqueda)
                || (e.Cif != null && e.Cif.ToUpper().Contains(busqueda)));
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

            var conFecha =
                from e in consulta
                select new
                {
                    e.Id,
                    e.RazonSocial,
                    e.Cif,
                    e.CreadoEnUtc,
                    PeorFecha = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Where(d => d.EmpresaId == e.Id)
                        .Min(d => (DateOnly?)d.FechaVencimiento),
                    // MIN ignora las fechas nulas, que son a la vez «no caduca» y «sin
                    // confirmar»: lo sin confirmar se cuenta aparte para no perderlo.
                    HaySinConfirmar = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Any(d => d.EmpresaId == e.Id && d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
                };

            // La clave de estado es la misma expresión con la que se ordena (EstadoDocumentalFiltro.ClaveOrden:
            // 0 Vencido, 1 Urgente, 2 Próximo, 3 Sin confirmar, 4 Vigente, 5 Sin caducidad), escrita en línea
            // porque tiene que traducirse a SQL. Contar y filtrar por ella garantiza que la franja de estado, el
            // filtro y el orden parten las filas igual.
            //
            // Los recuentos se toman ANTES de filtrar por estado y después de todos los demás filtros: cada
            // cifra de la franja dice cuántas filas quedarían al marcar solo ese estado.
            IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
            if (request.ConRecuentosPorEstado)
            {
                recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(await conFecha
                    .GroupBy(e =>
                        e.PeorFecha != null && e.PeorFecha < hoy ? 0
                    : e.PeorFecha != null && e.PeorFecha <= limiteRojo ? 1
                    : e.PeorFecha != null && e.PeorFecha <= limiteAmbar ? 2
                    : e.HaySinConfirmar ? 3
                    : e.PeorFecha != null ? 4
                    : 5)
                    .Select(grupo => new { Clave = grupo.Key, Filas = grupo.Count() })
                    .ToDictionaryAsync(grupo => grupo.Clave, grupo => grupo.Filas, cancellationToken));
            }

            // Varios estados a la vez (la franja deja marcar más de uno). null: el filtro no descarta nada
            // (vacío o sin ningún valor conocido). Lista vacía: ninguna fila — «sin documentos» (este listado
            // nunca produce un estado null: sin Documentos cae en Sin caducidad) o un estado que un propietario
            // nunca tiene (Faltante, En tolerancia). Un filtro válido pero no aplicable devuelve nada, no todo.
            if (EstadoDocumentalFiltro.ClavesDeOrden(request.EstadoDocumental) is { } clavesPedidas)
            {
                conFecha = conFecha.Where(e => clavesPedidas.Contains(
                    e.PeorFecha != null && e.PeorFecha < hoy ? 0
                    : e.PeorFecha != null && e.PeorFecha <= limiteRojo ? 1
                    : e.PeorFecha != null && e.PeorFecha <= limiteAmbar ? 2
                    : e.HaySinConfirmar ? 3
                    : e.PeorFecha != null ? 4
                    : 5));
            }

            var totalConEstado = await conFecha.CountAsync(cancellationToken);

            // Este camino se toma también solo para contar por estado (la franja lo pide siempre), así que el
            // orden no puede darse por supuesto: por estado si es la columna pedida, y si no por la misma lista
            // blanca que el camino sin estado.
            var porEstado = request.Descendente
                ? conFecha.OrderByDescending(e =>
                    e.PeorFecha != null && e.PeorFecha < hoy ? 0
                    : e.PeorFecha != null && e.PeorFecha <= limiteRojo ? 1
                    : e.PeorFecha != null && e.PeorFecha <= limiteAmbar ? 2
                    : e.HaySinConfirmar ? 3
                    : e.PeorFecha != null ? 4
                    : 5)
                : conFecha.OrderBy(e =>
                    e.PeorFecha != null && e.PeorFecha < hoy ? 0
                    : e.PeorFecha != null && e.PeorFecha <= limiteRojo ? 1
                    : e.PeorFecha != null && e.PeorFecha <= limiteAmbar ? 2
                    : e.HaySinConfirmar ? 3
                    : e.PeorFecha != null ? 4
                    : 5);
            var ordenaPorEstado = string.Equals(
                request.OrdenarPor, nameof(EmpresaListaDto.EstadoDocumental), StringComparison.Ordinal);
            var ordenadaConEstado = ordenaPorEstado
                ? porEstado.ThenBy(e => e.RazonSocial)
                : (request.OrdenarPor, request.Descendente) switch
                {
                    (nameof(EmpresaListaDto.RazonSocial), true) => conFecha.OrderByDescending(e => e.RazonSocial),
                    (nameof(EmpresaListaDto.Cif), false) => conFecha.OrderBy(e => e.Cif),
                    (nameof(EmpresaListaDto.Cif), true) => conFecha.OrderByDescending(e => e.Cif),
                    (nameof(EmpresaListaDto.CreadoEnUtc), false) => conFecha.OrderBy(e => e.CreadoEnUtc),
                    (nameof(EmpresaListaDto.CreadoEnUtc), true) => conFecha.OrderByDescending(e => e.CreadoEnUtc),
                    _ => conFecha.OrderBy(e => e.RazonSocial)
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
                paginaConEstado.Select(e => new EmpresaListaDto(
                    e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc,
                    CalculoEstadoDocumentalService.PeorEstado(e.PeorFecha, e.HaySinConfirmar, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                    cumplimientoConEstado.GetValueOrDefault(e.Id),
                    deteccionesConEstado.GetValueOrDefault(e.Id)))
                    .ToList(),
                totalConEstado, request.Pagina, request.TamanoPagina)
            {
                RecuentosPorEstado = recuentosPorEstado
            };
        }

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
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

        return new ResultadoPaginado<EmpresaListaDto>(
            elementos.Select(e => e with
            {
                EstadoDocumental = estados.GetValueOrDefault(e.Id),
                CumplimientoPorcentaje = cumplimiento.GetValueOrDefault(e.Id),
                DeteccionesPendientes = detecciones.GetValueOrDefault(e.Id)
            }).ToList(),
            total, request.Pagina, request.TamanoPagina);
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
