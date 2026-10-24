using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Domain.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;

/// <param name="NivelServicio">Filtro exacto por nivel de servicio (Gestionada / Supervisada).</param>
/// <param name="EstadoDocumental">
/// Filtro de la franja de estado: nombres de <see cref="EstadoDocumento"/> separados por coma (el mismo valor
/// que viaja en la URL), sobre <see cref="SubcontrataListaDto.EstadoDocumental"/>, con Pendiente (Faltante) y el vencido
/// que solo avisa dentro de «Vencido», como el listado de Empresas (<see cref="EstadoDocumentalFiltro.ClavesDeOrden"/> con
/// requisitos obligatorios). Un valor que no es un estado se ignora; si ninguno lo es, no filtra.
/// </param>
/// <param name="ContratadaPorEmpresaId">
/// Filtro «Contratada por»: solo las Subcontratas con una Relación Empresarial vigente con esa Empresa propia
/// contratante. La Empresa no concede visibilidad: el alcance de arriba sigue decidiendo qué Subcontratas se ven.
/// </param>
/// <param name="ConRecuentosPorEstado">
/// Rellena <c>ResultadoPaginado.RecuentosPorEstado</c>: Subcontratas por estado documental con los demás
/// filtros aplicados y sin el de estado.
/// </param>
public record ObtenerSubcontratasQuery(
    string? Busqueda, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, Guid? SubcontrataId = null,
    NivelServicioSubcontrata? NivelServicio = null,
    string? EstadoDocumental = null, bool ConRecuentosPorEstado = false, Guid? ContratadaPorEmpresaId = null)
    : IRequest<ResultadoPaginado<SubcontrataListaDto>>;

/// <param name="CumplimientoPorcentaje">
/// % de cumplimiento documental de los trabajadores de la subcontrata
/// ("Subcontrata 360", mismo patrón que <c>CentroListaDto.CumplimientoPorcentaje</c>
/// de Centro 360) — <c>null</c> cuando ningún trabajador tiene un par
/// Trabajador×TipoDocumento exigido por alguno de sus centros activos.
/// </param>
/// <param name="Recuentos">
/// Las incidencias documentales de sus Trabajadores (el desplegable de la fila y la exportación). No deciden
/// <see cref="EstadoDocumental"/>: el de la Subcontrata es el de su documentación de Empresa (decisión D-1).
/// </param>
public record SubcontrataListaDto(
    Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, NivelServicioSubcontrata NivelServicio,
    int? CumplimientoPorcentaje, RecuentosSubcontrataDto Recuentos)
{
    /// <summary>Identidades visibles por el ámbito efectivo del listado de Trabajadores y por RLS.</summary>
    public IReadOnlyList<TrabajadorIdentidadSubcontrataDto> Trabajadores { get; init; } = [];

    /// <summary>Empresas propias contratantes visibles, con una Relación Empresarial vigente.</summary>
    public IReadOnlyList<EmpresaContratanteSubcontrataDto> Contratantes { get; init; } = [];

    /// <summary>
    /// Estado documental de la Subcontrata (decisión D-1, 2026-10-24): solo su documentación de Empresa, con el mismo criterio
    /// que una Empresa del listado de Empresas (<see cref="SeveridadEstadoDocumento.ConRequisitosObligatorios"/>: en rojo solo
    /// un documento de ámbito Empresa que un Centro donde operan sus Trabajadores marca obligatorio y que falta o ya no vale).
    /// Lo que les falta a sus Trabajadores no la pinta: va en <see cref="TrabajadoresBloqueados"/>. Sin documentos ni
    /// requisitos incumplidos es <see cref="EstadoDocumento.SinCaducidad"/>.
    /// </summary>
    public EstadoDocumento EstadoDocumental { get; init; } = EstadoDocumento.SinCaducidad;

    /// <summary>
    /// <see cref="EstadoDocumental"/> es Vencido solo por un documento de la Subcontrata que ningún Centro marca obligatorio:
    /// se pinta en el tono de aviso, no en rojo (<see cref="SeveridadEstadoDocumento.EsVencidoSinBloqueo"/>).
    /// </summary>
    public bool VencidoSinBloqueo { get; init; }

    /// <summary>Tipos de ámbito Empresa obligatorios en algún Centro que faltan sin ningún documento: el motivo «Falta: …».</summary>
    public IReadOnlyList<string> DocumentosFaltantes { get; init; } = [];

    /// <summary>Los documentos de ámbito Empresa de la Subcontrata que piden atención (vencidos, por vencer, sin confirmar).</summary>
    public IReadOnlyList<IncidenciaDocumentalDto> Incidencias { get; init; } = [];

    /// <summary>
    /// Trabajadores dados de alta con la Subcontrata (<see cref="Domain.Trabajadores.Trabajador.SubcontrataId"/>) y visibles
    /// para el usuario cuyo estado va en rojo: un requisito obligatorio de algún Centro donde están asignados falta o ya no
    /// vale. Recuento aparte («N Trabajadores bloqueados en algún Centro»): no cambia <see cref="EstadoDocumental"/>.
    /// </summary>
    public int TrabajadoresBloqueados { get; init; }
}

public record TrabajadorIdentidadSubcontrataDto(Guid Id, string Nombre, string Apellidos);
public record EmpresaContratanteSubcontrataDto(Guid Id, string RazonSocial);

/// <summary>
/// F3b-Subcontrata (revisión adversaria del 2026-08-26, evidencia real de
/// IntegrationTests): a diferencia de las otras dos consultas semánticas de
/// D2 (<c>ObtenerSubcontratasParaSelectorQuery</c> y la rama Subcontrata de
/// <c>BuscarGlobalQuery</c>, que siguen congeladas), esta se adelanta a leer
/// Empresas — congelarla dejaba el % de cumplimiento activamente incorrecto
/// (no solo desactualizado) para cualquier Subcontrata creada tras el
/// freeze, porque <see cref="Domain.Trabajadores.Trabajador.SubcontrataId"/>
/// ya solo puede apuntar a filas de Empresas. Ver
/// Project-Hydra-Negocio/tecnico/f3b-subcontrata-inventario-fresco-2026-08-26.md.
///
/// <para>
/// <b>Estado (decisión D-1, PR 8 del estado materializado).</b> Una Subcontrata es una fila de <c>Empresas</c>, y su estado es
/// el de su fila de ámbito Empresa en <c>EstadosEntidad</c> (<see cref="ILectorEstadoMaterializado"/>, Tenant completo y
/// usuario que alcanza todos sus Centros) o, si el lector no puede servirlo, el mismo cálculo en lectura que el listado de
/// Empresas. El recuento de Trabajadores bloqueados se recompone al leer, de las filas de sus Trabajadores o de su cálculo:
/// no se guarda en fila propia.
/// </para>
/// </summary>
public class ObtenerSubcontratasQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos, ICalculoEstadoSubcontrataService calculoEstado,
    ITrabajadoresQueryContext trabajadoresContext, ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IRequisitosObligatoriosIncumplidosService requisitosObligatorios,
    ILectorEstadoMaterializado? lectorEstado = null)
    : IRequestHandler<ObtenerSubcontratasQuery, ResultadoPaginado<SubcontrataListaDto>>
{
    /// <summary>Clave de <see cref="ObtenerSubcontratasQuery.OrdenarPor"/> de la columna «Contratada por».</summary>
    public const string ColumnaContratadaPor = "ContratadaPor";

    // Sin lector (quien construye el manejador a mano, como los tests del cálculo en lectura) todo se calcula.
    private readonly ILectorEstadoMaterializado lector = lectorEstado ?? SinLectorDeEstadoMaterializado.Instancia;

    public async Task<ResultadoPaginado<SubcontrataListaDto>> Handle(ObtenerSubcontratasQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Empresas.Where(e => e.NivelServicio != null);

        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        if (subcontrataIdsVisibles is not null)
            consulta = consulta.Where(s => subcontrataIdsVisibles.Contains(s.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda;
            consulta = consulta.Where(s => TextoDeBusqueda.Contiene(s.RazonSocial, busqueda)
                || (s.Cif != null && TextoDeBusqueda.Contiene(s.Cif, busqueda)));
        }

        // Drill-down por Id exacto — mismo criterio que ObtenerCentrosQuery.CentroId:
        // recargar una sola fila tras una acción en el acordeón sin colapsar el resto.
        if (request.SubcontrataId is not null)
            consulta = consulta.Where(s => s.Id == request.SubcontrataId);

        // La columna guarda el nombre del enum como texto (deuda de F3, ver Empresa.NivelServicio).
        // Es un filtro por VALOR: no decide el rol de la Empresa por la nulidad de la columna.
        var nivelTexto = request.NivelServicio?.ToString();
        if (nivelTexto is not null)
            consulta = consulta.Where(s => s.NivelServicio == nivelTexto);

        // Empresas propias contratantes con relación vigente: la columna «Contratada por» ordena por la primera por razón social.
        // Con el mismo alcance de Empresas que la columna que se pinta: ni el filtro ni el orden revelan una Empresa que no se ve.
        var empresasVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var contratantes = ContratantesVigentes(empresasVisibles);

        if (request.ContratadaPorEmpresaId is { } contratanteId)
            consulta = consulta.Where(s => contratantes.Any(c => c.ProveedoraId == s.Id && c.Id == contratanteId));

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery. El cumplimiento no está persistido:
        // ordena en memoria por el camino con estado (más abajo) y aquí queda el orden por razón social.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(SubcontrataListaDto.NivelServicio), false) => consulta.OrderBy(s => s.NivelServicio),
            (nameof(SubcontrataListaDto.NivelServicio), true) => consulta.OrderByDescending(s => s.NivelServicio),
            // Las sin contratante van siempre al final, en los dos sentidos.
            (ColumnaContratadaPor, false) => consulta
                .OrderBy(s => !contratantes.Any(c => c.ProveedoraId == s.Id))
                .ThenBy(s => contratantes.Where(c => c.ProveedoraId == s.Id).Min(c => c.RazonSocial)),
            (ColumnaContratadaPor, true) => consulta
                .OrderBy(s => !contratantes.Any(c => c.ProveedoraId == s.Id))
                .ThenByDescending(s => contratantes.Where(c => c.ProveedoraId == s.Id).Min(c => c.RazonSocial)),
            (nameof(SubcontrataListaDto.RazonSocial), true) => consulta.OrderByDescending(s => s.RazonSocial),
            (nameof(SubcontrataListaDto.Cif), false) => consulta.OrderBy(s => s.Cif),
            (nameof(SubcontrataListaDto.Cif), true) => consulta.OrderByDescending(s => s.Cif),
            (nameof(SubcontrataListaDto.CreadoEnUtc), false) => consulta.OrderBy(s => s.CreadoEnUtc),
            (nameof(SubcontrataListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(s => s.CreadoEnUtc),
            _ => consulta.OrderBy(s => s.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(s => s.Id);

        // Camino con estado (franja de estado): el filtro, los recuentos y el orden por cumplimiento necesitan el estado de
        // TODAS las filas que pasan los demás filtros, y se pagina después en memoria — mismo planteamiento que
        // ObtenerCentrosQuery. El alcance es el de arriba: no se lee ni se calcula nada de una Subcontrata que el usuario no ve.
        var clavesPedidas = EstadoDocumentalFiltro.ClavesDeOrden(request.EstadoDocumental, incluirFaltantes: true);
        var ordenaPorCumplimiento = request.OrdenarPor == nameof(SubcontrataListaDto.CumplimientoPorcentaje);
        if (clavesPedidas is not null || request.ConRecuentosPorEstado || ordenaPorCumplimiento)
        {
            var todas = await ordenada
                .Select(s => new FilaSubcontrata(s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, s.NivelServicio!))
                .ToListAsync(cancellationToken);
            var estado = await EstadoAsync(todas.Select(s => s.Id).ToList(), cancellationToken);

            IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
            if (request.ConRecuentosPorEstado)
            {
                recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(todas
                    .GroupBy(s => ClaveEstado(estado.Estados[s.Id]))
                    .ToDictionary(grupo => grupo.Key, grupo => grupo.Count()), incluirFaltantes: true);
            }

            IReadOnlyList<FilaSubcontrata> filtradas = clavesPedidas is not null
                ? todas.Where(s => clavesPedidas.Contains(ClaveEstado(estado.Estados[s.Id]))).ToList()
                : todas;

            // Sin cumplimiento (ningún requisito exigido) va siempre al final; el desempate es el orden de razón social ya aplicado.
            IReadOnlyDictionary<Guid, ResumenEstadoSubcontrata>? resumenes = null;
            if (ordenaPorCumplimiento)
            {
                resumenes = await calculoEstado.CalcularResumenAsync(filtradas.Select(s => s.Id).ToList(), cancellationToken);
                int? Porcentaje(FilaSubcontrata s) => resumenes.GetValueOrDefault(s.Id)?.Fraccion.Porcentaje;
                var conValor = filtradas.Where(s => Porcentaje(s) is not null);
                filtradas =
                [
                    .. (request.Descendente
                        ? conValor.OrderByDescending(Porcentaje)
                        : conValor.OrderBy(Porcentaje)),
                    .. filtradas.Where(s => Porcentaje(s) is null)
                ];
            }

            var paginaFiltrada = filtradas.Skip((request.Pagina - 1) * request.TamanoPagina).Take(request.TamanoPagina).ToList();
            return new ResultadoPaginado<SubcontrataListaDto>(
                await ConDetalleAsync(paginaFiltrada, estado, resumenes, cancellationToken),
                filtradas.Count, request.Pagina, request.TamanoPagina)
            {
                RecuentosPorEstado = recuentosPorEstado
            };
        }

        var total = await consulta.CountAsync(cancellationToken);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(s => new FilaSubcontrata(s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, s.NivelServicio!))
            .ToListAsync(cancellationToken);

        return new ResultadoPaginado<SubcontrataListaDto>(
            await ConDetalleAsync(pagina, await EstadoAsync(pagina.Select(s => s.Id).ToList(), cancellationToken), null, cancellationToken),
            total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// La clave con la que la franja cuenta y filtra (<see cref="EstadoDocumentalFiltro.ClaveOrdenConFaltantes"/>), la misma
    /// que el listado de Empresas: el vencido que solo avisa tiene la suya.
    /// </summary>
    private static int ClaveEstado(EstadoLeido estado) =>
        estado.VencidoSinBloqueo ? EstadoDocumentalFiltro.ClaveVencidoSinBloqueo : EstadoDocumentalFiltro.ClaveOrdenConFaltantes(estado.Estado);

    /// <summary>
    /// El estado de esas Subcontratas y sus Trabajadores visibles, para las filas que se pintan y las que se cuentan.
    /// <para>
    /// <b>Estado de la Subcontrata</b> (decisión D-1): su fila de ámbito Empresa, leída de <c>EstadosEntidad</c> si el lector
    /// puede servirla (el relleno y el recálculo la escriben con los Centros donde trabajan los Trabajadores de la Subcontrata,
    /// <c>EmpresaId ?? SubcontrataId</c>, igual que <see cref="IRequisitosObligatoriosIncumplidosService.DeEmpresasAsync"/>); si
    /// no, el cálculo en lectura de una Empresa. Las dos dan lo mismo, y lo ata el test de integración del listado.
    /// </para>
    /// <para>
    /// <b>Trabajadores bloqueados</b>: los dados de alta con la Subcontrata (<c>SubcontrataId</c>) que el usuario ve, en rojo
    /// según su listado (un requisito obligatorio de Trabajador falta o ya no vale en un Centro donde está asignado). Si el
    /// estado de la Subcontrata se leyó de las tablas, se intenta leer también el de ellos; si no, se calcula con una sola
    /// evaluación de Centros para los dos lados. Un voluntario inactivo no cuenta, como en el listado de Trabajadores.
    /// </para>
    /// </summary>
    private async Task<EstadoDeLasFilas> EstadoAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
            return new EstadoDeLasFilas(new Dictionary<Guid, EstadoLeido>(), [], new Dictionary<Guid, int>());

        // Los Trabajadores de la Subcontrata que el usuario ve: los mismos con los que se pinta la fila (Mi equipo).
        var consultaTrabajadores = trabajadoresContext.Trabajadores
            .Where(t => t.SubcontrataId != null && ids.Contains(t.SubcontrataId.Value));
        var idsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (idsVisibles is not null)
            consultaTrabajadores = consultaTrabajadores.Where(t => idsVisibles.Contains(t.Id));
        var trabajadores = await consultaTrabajadores.OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre).ThenBy(t => t.Id)
            .Select(t => new TrabajadorDeSubcontrata(t.SubcontrataId!.Value, new TrabajadorIdentidadSubcontrataDto(t.Id, t.Nombre, t.Apellidos)))
            .ToListAsync(cancellationToken);
        var trabajadorIds = trabajadores.Select(t => t.Identidad.Id).ToList();

        HashSet<Guid> bloqueados;
        var leido = await lector.LeerAsync(AmbitoEstadoEntidad.Empresa, ids, cancellationToken);
        IReadOnlyDictionary<Guid, EstadoLeido> estados;
        if (leido is not null)
        {
            estados = leido;
            var leidoDeTrabajadores = trabajadorIds.Count == 0
                ? new IdsPorEstadoLeido([], [], [], [], [], [], [])
                : await lector.LeerEstadoCompletoAsync(
                    AmbitoEstadoEntidad.Trabajador,
                    trabajadoresContext.Trabajadores.Where(t => trabajadorIds.Contains(t.Id)).Select(t => t.Id),
                    cancellationToken);
            bloqueados = leidoDeTrabajadores is not null
                ? [.. leidoDeTrabajadores.Vencido, .. leidoDeTrabajadores.Faltante]
                : EnRojo(await requisitosObligatorios.DeTrabajadoresAsync(calculoEstado.SinVoluntariosInactivos(trabajadorIds), cancellationToken));
        }
        else
        {
            var peores = await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Empresa, ids, cancellationToken);
            var incumplidos = await requisitosObligatorios.DeEmpresasYTrabajadoresAsync(
                dbContext.Empresas.Where(e => ids.Contains(e.Id)).Select(e => e.Id), calculoEstado.SinVoluntariosInactivos(trabajadorIds), cancellationToken);
            estados = ids.Distinct().ToDictionary(id => id, id => Combinar(peores.GetValueOrDefault(id), incumplidos.PorEmpresa.GetValueOrDefault(id) ?? []));
            bloqueados = EnRojo(incumplidos.PorTrabajador);
        }

        var bloqueadosPorSubcontrata = trabajadores
            .Where(t => bloqueados.Contains(t.Identidad.Id))
            .GroupBy(t => t.SubcontrataId)
            .ToDictionary(g => g.Key, g => g.Count());
        return new EstadoDeLasFilas(estados, trabajadores, bloqueadosPorSubcontrata);
    }

    private static HashSet<Guid> EnRojo(IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>> porTrabajador) =>
        porTrabajador
            .Where(p => RequisitosObligatoriosIncumplidos.HayVencido(p.Value) || RequisitosObligatoriosIncumplidos.HayAusente(p.Value))
            .Select(p => p.Key)
            .ToHashSet();

    /// <summary>
    /// La combinación del listado de Empresas (<c>ObtenerEmpresasQuery.ConIncumplidos</c>): un obligatorio vencido es Vencido,
    /// uno que falta es Pendiente, y sin ninguno manda el peor de sus documentos de Empresa; sin documentos, Sin caducidad.
    /// </summary>
    private static EstadoLeido Combinar(EstadoDocumento peorVigencia, IReadOnlyList<RequisitoObligatorioIncumplido> incumplidos)
    {
        var vencido = RequisitosObligatoriosIncumplidos.HayVencido(incumplidos);
        var ausente = RequisitosObligatoriosIncumplidos.HayAusente(incumplidos);
        return new EstadoLeido(
            SeveridadEstadoDocumento.ConRequisitosObligatorios(peorVigencia, vencido, ausente) ?? EstadoDocumento.SinCaducidad,
            SeveridadEstadoDocumento.EsVencidoSinBloqueo(peorVigencia, vencido, ausente),
            RequisitosObligatoriosIncumplidos.NombresAusentes(incumplidos));
    }

    /// <summary>Empresas propias contratantes (dentro del alcance de visibilidad) con una relación vigente con cada Subcontrata.</summary>
    private IQueryable<ContratanteVigente> ContratantesVigentes(IReadOnlyList<Guid>? empresasVisibles)
    {
        var empresas = dbContext.Empresas.Where(e => e.EsPropia);
        if (empresasVisibles is not null)
            empresas = empresas.Where(e => empresasVisibles.Contains(e.Id));
        return
            from r in dbContext.RelacionesEmpresariales
            where r.VigenciaHasta == null
            join e in empresas on r.ClienteId equals e.Id
            select new ContratanteVigente { ProveedoraId = r.ProveedoraId, Id = e.Id, RazonSocial = e.RazonSocial };
    }

    private sealed class ContratanteVigente
    {
        public Guid ProveedoraId { get; init; }
        public Guid Id { get; init; }
        public string RazonSocial { get; init; } = string.Empty;
    }

    /// <summary>
    /// Solo para las filas que se devuelven, ya paginadas: el cumplimiento y las incidencias de sus Trabajadores, los documentos
    /// de Empresa que piden atención y las Empresas contratantes no se cargan para las Subcontratas que el listado no enseña.
    /// El estado y los Trabajadores salen de <see cref="EstadoAsync"/>.
    /// </summary>
    private async Task<List<SubcontrataListaDto>> ConDetalleAsync(
        IReadOnlyList<FilaSubcontrata> filas, EstadoDeLasFilas estado,
        IReadOnlyDictionary<Guid, ResumenEstadoSubcontrata>? resumenes, CancellationToken cancellationToken)
    {
        if (filas.Count == 0)
            return [];

        var ids = filas.Select(s => s.Id).ToList();
        resumenes ??= await calculoEstado.CalcularResumenAsync(ids, cancellationToken);
        var desgloses = await calculoEstadoDocumental.CalcularDesgloseAsync(AmbitoAplicacion.Empresa, ids, cancellationToken);
        var empresasVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var contratantes = await ContratantesVigentes(empresasVisibles)
            .Where(c => ids.Contains(c.ProveedoraId))
            .Distinct().OrderBy(e => e.RazonSocial).ThenBy(e => e.Id).ToListAsync(cancellationToken);
        var trabajadoresPorSubcontrata = estado.Trabajadores.ToLookup(t => t.SubcontrataId, t => t.Identidad);

        return filas.Select(s =>
        {
            var resumen = resumenes.GetValueOrDefault(s.Id);
            var deLaSubcontrata = estado.Estados[s.Id];
            return new SubcontrataListaDto(
                s.Id, s.RazonSocial, s.Cif, s.CreadoEnUtc, Enum.Parse<NivelServicioSubcontrata>(s.NivelServicio),
                resumen?.Fraccion.Porcentaje,
                resumen is null ? RecuentosSubcontrataDto.Vacio : Desglosar(resumen))
            {
                EstadoDocumental = deLaSubcontrata.Estado,
                VencidoSinBloqueo = deLaSubcontrata.VencidoSinBloqueo,
                DocumentosFaltantes = deLaSubcontrata.DocumentosFaltantes,
                Incidencias = desgloses.TryGetValue(s.Id, out var desglose) ? desglose.Incidencias : [],
                TrabajadoresBloqueados = estado.BloqueadosPorSubcontrata.GetValueOrDefault(s.Id),
                Trabajadores = trabajadoresPorSubcontrata[s.Id].ToList(),
                Contratantes = contratantes.Where(e => e.ProveedoraId == s.Id)
                    .Select(e => new EmpresaContratanteSubcontrataDto(e.Id, e.RazonSocial)).ToList()
            };
        }).ToList();
    }

    private record FilaSubcontrata(Guid Id, string RazonSocial, string? Cif, DateTime CreadoEnUtc, string NivelServicio);

    private record TrabajadorDeSubcontrata(Guid SubcontrataId, TrabajadorIdentidadSubcontrataDto Identidad);

    private record EstadoDeLasFilas(
        IReadOnlyDictionary<Guid, EstadoLeido> Estados, IReadOnlyList<TrabajadorDeSubcontrata> Trabajadores,
        IReadOnlyDictionary<Guid, int> BloqueadosPorSubcontrata);

    /// <summary>
    /// "Faltante" cuenta como vencido, y Urgente va con "próximas" — mismo
    /// criterio que <c>ObtenerCentrosQuery.Desglosar</c>: un requisito sin
    /// documento no está al día, y Urgente es más severo que Próximo pero el
    /// documento aún no venció. Estos dos buckets se leen como texto literal
    /// ("N vencido(s)") en la UI, no solo como un tono de color — meter
    /// Urgente en "vencidas" afirmaría una fecha vencida que no lo está
    /// (hallazgo de Codex, oleada 3). La severidad de color se resuelve en el
    /// badge de cada incidencia, no en el bucket del recuento. Antes de que
    /// <c>ObtenerCentrosQuery</c> lo corrigiera (D-7, piloto Outbound), este
    /// comentario afirmaba paridad mientras Urgente se descartaba en silencio
    /// aquí igual que allí.
    /// </summary>
    private static RecuentosSubcontrataDto Desglosar(ResumenEstadoSubcontrata resumen)
    {
        var vencidas = resumen.Incidencias.Where(i => i.Estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante).ToList();
        var proximas = resumen.Incidencias.Where(i => i.Estado is EstadoDocumento.Urgente or EstadoDocumento.Proximo).ToList();
        return new RecuentosSubcontrataDto(vencidas, proximas) { SinConfirmar = resumen.SinConfirmar };
    }
}
