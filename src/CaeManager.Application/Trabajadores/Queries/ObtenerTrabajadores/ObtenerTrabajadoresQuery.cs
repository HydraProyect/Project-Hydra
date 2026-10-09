using CaeManager.Domain.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;

/// <summary>
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla.
/// Un Trabajador no tiene estado propio en el modelo: se deriva del peor
/// estado de vigencia de sus Documentos con
/// <see cref="ICalculoEstadoDocumentalService"/> (o, en el camino de esta
/// clase que filtra/ordena por estado, con el mismo cálculo hecho en SQL). Un
/// Trabajador sin ningún Documento nunca se ve como <c>null</c> en el DTO:
/// cae en <see cref="EstadoDocumento.SinCaducidad"/>, igual que uno cuyos
/// Documentos están todos confirmados como que no caducan; si alguno está sin
/// vigencia confirmada y ninguno está en Próximo, Urgente o Vencido, cae en
/// <see cref="EstadoDocumento.SinConfirmar"/> — el <c>null</c> del tipo
/// es un artefacto de <see cref="TrabajadorListaDto"/> siendo compartido con
/// otros listados, no un valor que este handler produzca.
///
/// <para>
/// <paramref name="CentroId"/> deja solo a los Trabajadores con una Asignación activa en ese Centro. Solo
/// ESTRECHA: se compone con el alcance del usuario, y un Centro que el usuario no puede ver devuelve la lista
/// vacía (no revela quién trabaja allí, aunque el Trabajador sea visible por otro Centro).
/// </para>
///
/// <para>
/// <paramref name="ConDesgloseDocumental"/> rellena, para las filas de la página, las incidencias y la fracción
/// «vigentes / registrados» de <see cref="TrabajadorListaDto"/>. Lo apaga quien recorre el listado entero sin
/// pintarlo (la exportación) o no lo publica (la API v1): cuesta una consulta de documentos por página.
/// </para>
/// </summary>
public record ObtenerTrabajadoresQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20,
    Guid? CentroId = null, bool ConDesgloseDocumental = true,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null,
    bool ConRecuentosPorEstado = false, Guid? TrabajadorId = null)
    : IRequest<ResultadoPaginado<TrabajadorListaDto>>;

public record TrabajadorListaDto(
    Guid Id, string Nombre, string Apellidos, string? Dni, string EmpleadorNombre,
    EstadoDocumento? EstadoDocumental = null)
{
    /// <summary>
    /// Los documentos del Trabajador que explican su <see cref="EstadoDocumental"/> y los demás que piden
    /// atención, del más grave al menos. Vacía si no hay ninguno o si la consulta no pidió el desglose.
    /// </summary>
    public IReadOnlyList<IncidenciaDocumentalDto> Incidencias { get; init; } = [];

    /// <summary>Documentos operativos del Trabajador (ver <see cref="DesgloseDocumentalDto"/>).</summary>
    public int DocumentosRegistrados { get; init; }

    /// <summary>De los registrados, los que están al día (<see cref="CumplimientoDocumental.EsConforme(EstadoDocumento)"/>).</summary>
    public int DocumentosVigentes { get; init; }
}

public class ObtenerTrabajadoresQueryHandler(
    IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos, ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IAsignacionesQueryContext asignacionesContext)
    : IRequestHandler<ObtenerTrabajadoresQuery, ResultadoPaginado<TrabajadorListaDto>>
{
    public async Task<ResultadoPaginado<TrabajadorListaDto>> Handle(
        ObtenerTrabajadoresQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from trabajador in trabajadoresContext.Trabajadores
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new { trabajador, EmpleadorNombre = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial };

        // Este es el listado (tabla /trabajadores), no el selector de "elige
        // un trabajador ya existente" — se acota al alcance de Trabajadores:
        // Asignación activa en un Centro visible, más la plantilla de la
        // Empresa propia para un Gestor/Coordinador CAE con cartera (ver
        // IAlcanceDatosService).
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            consulta = consulta.Where(x => trabajadorIdsVisibles.Contains(x.trabajador.Id));

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // Va después del alcance: solo estrecha.
        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.trabajador.Id == request.TrabajadorId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.trabajador.Nombre.ToUpper().Contains(busqueda) ||
                x.trabajador.Apellidos.ToUpper().Contains(busqueda) ||
                (x.trabajador.Dni ?? "").ToUpper().Contains(busqueda) ||
                (x.trabajador.Alias != null && x.trabajador.Alias.ToUpper().Contains(busqueda)));
        }

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.trabajador.EmpresaId == request.EmpresaId);

        if (request.SubcontrataId is not null)
            consulta = consulta.Where(x => x.trabajador.SubcontrataId == request.SubcontrataId);

        if (request.CentroId is { } centroId)
        {
            // El filtro por Centro solo estrecha. El alcance de Trabajadores de arriba no basta: un Trabajador
            // visible por un Centro de la cartera puede tener además una Asignación en otro que el usuario no
            // ve, y filtrar por ese otro diría quién trabaja allí. Un Centro fuera del alcance no devuelve nada
            // (la consulta sigue su camino, con sus recuentos a cero, en vez de un atajo con otra forma).
            var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
            var centroVisible = centroIdsVisibles is null || centroIdsVisibles.Contains(centroId);

            consulta = consulta.Where(x => centroVisible && asignacionesContext.Asignaciones
                .Any(a => a.TrabajadorId == x.trabajador.Id && a.CentroId == centroId && a.FechaBaja == null));
        }

        // Lista blanca de columnas ordenables — ver ObtenerClientesQuery. Este
        // orden alimenta el camino normal (más abajo); el camino con estado
        // completo repite la misma lista blanca sobre su propia proyección
        // (conFecha), porque esa proyección añade PeorFecha y no puede
        // reutilizar este IQueryable ya materializado en columnas — pero es
        // el mismo criterio, mismo whitelist, misma intercalación de
        // PostgreSQL en los dos caminos.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(TrabajadorListaDto.Apellidos), true) => consulta.OrderByDescending(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(TrabajadorListaDto.Nombre), false) => consulta.OrderBy(x => x.trabajador.Nombre).ThenBy(x => x.trabajador.Apellidos),
            (nameof(TrabajadorListaDto.Nombre), true) => consulta.OrderByDescending(x => x.trabajador.Nombre).ThenBy(x => x.trabajador.Apellidos),
            (nameof(TrabajadorListaDto.Dni), false) => consulta.OrderBy(x => x.trabajador.Dni),
            (nameof(TrabajadorListaDto.Dni), true) => consulta.OrderByDescending(x => x.trabajador.Dni),
            (nameof(TrabajadorListaDto.EmpleadorNombre), false) => consulta.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.trabajador.Apellidos),
            (nameof(TrabajadorListaDto.EmpleadorNombre), true) => consulta.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.trabajador.Apellidos),
            _ => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.trabajador.Id);

        var proyeccion = ordenada.Select(x => new TrabajadorListaDto(
            x.trabajador.Id, x.trabajador.Nombre, x.trabajador.Apellidos, x.trabajador.Dni, x.EmpleadorNombre));

        // El estado documental se deriva de los Documentos, así que filtrar u
        // ordenar por él exige conocer, para cada Trabajador visible, el peor
        // vencimiento de sus Documentos ANTES de paginar — pero ese peor
        // vencimiento (MIN por propietario) sí se puede pedir a SQL con una
        // subconsulta correlacionada, igual que ObtenerDocumentosQuery traduce
        // su filtro de Estado a un rango de fechas (DocumentosPaginacionEnSqlTests).
        // Antes esto materializaba TODOS los Trabajadores visibles en memoria
        // para poder filtrar/ordenar (hallazgo Módulo 8, PR #389 § 4.1).
        var ordenaPorEstado = string.Equals(
            request.OrdenarPor, nameof(TrabajadorListaDto.EstadoDocumental), StringComparison.Ordinal);
        var necesitaEstadoCompleto = !string.IsNullOrWhiteSpace(request.EstadoDocumental) || ordenaPorEstado ||
            request.ConRecuentosPorEstado;

        if (necesitaEstadoCompleto)
        {
            var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
            var hoy = DiaDeNegocio.Hoy();
            // Equivalencia con CalculadoraEstadoDocumento: "días restantes <=
            // umbral" es "fecha <= hoy + umbral" (ver ObtenerDocumentosQuery).
            var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);
            var limiteAmbar = hoy.AddDays(parametros.UmbralAmbarDias);

            var conFecha =
                from x in consulta
                select new
                {
                    x.trabajador.Id,
                    x.trabajador.Nombre,
                    x.trabajador.Apellidos,
                    x.trabajador.Dni,
                    x.EmpleadorNombre,
                    PeorFecha = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Where(d => d.TrabajadorId == x.trabajador.Id)
                        .Min(d => (DateOnly?)d.FechaVencimiento),
                    // MIN ignora las fechas nulas, que son a la vez «no caduca» y «sin
                    // confirmar»: lo sin confirmar se cuenta aparte para no perderlo.
                    HaySinConfirmar = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Any(d => d.TrabajadorId == x.trabajador.Id && d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
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
                    .GroupBy(x =>
                        x.PeorFecha != null && x.PeorFecha < hoy ? 0
                    : x.PeorFecha != null && x.PeorFecha <= limiteRojo ? 1
                    : x.PeorFecha != null && x.PeorFecha <= limiteAmbar ? 2
                    : x.HaySinConfirmar ? 3
                    : x.PeorFecha != null ? 4
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
                conFecha = conFecha.Where(x => clavesPedidas.Contains(
                    x.PeorFecha != null && x.PeorFecha < hoy ? 0
                    : x.PeorFecha != null && x.PeorFecha <= limiteRojo ? 1
                    : x.PeorFecha != null && x.PeorFecha <= limiteAmbar ? 2
                    : x.HaySinConfirmar ? 3
                    : x.PeorFecha != null ? 4
                    : 5));
            }

            var totalConEstado = await conFecha.CountAsync(cancellationToken);

            // Ternario anidado en línea a propósito, no un método aparte: un
            // método de C# dentro de un OrderBy no se traduce a SQL — tiene
            // que ser una expresión que EF pueda leer, igual que
            // ObtenerDocumentosQuery.
            var ordenadaConEstado = ordenaPorEstado
                ? (request.Descendente
                    ? conFecha.OrderByDescending(x =>
                        x.PeorFecha != null && x.PeorFecha < hoy ? 0
                        : x.PeorFecha != null && x.PeorFecha <= limiteRojo ? 1
                        : x.PeorFecha != null && x.PeorFecha <= limiteAmbar ? 2
                        : x.HaySinConfirmar ? 3
                        : x.PeorFecha != null ? 4
                        : 5)
                    : conFecha.OrderBy(x =>
                        x.PeorFecha != null && x.PeorFecha < hoy ? 0
                        : x.PeorFecha != null && x.PeorFecha <= limiteRojo ? 1
                        : x.PeorFecha != null && x.PeorFecha <= limiteAmbar ? 2
                        : x.HaySinConfirmar ? 3
                        : x.PeorFecha != null ? 4
                        : 5))
                    .ThenBy(x => x.Apellidos).ThenBy(x => x.Nombre)
                : (request.OrdenarPor, request.Descendente) switch
                {
                    (nameof(TrabajadorListaDto.Apellidos), true) => conFecha.OrderByDescending(x => x.Apellidos).ThenBy(x => x.Nombre),
                    (nameof(TrabajadorListaDto.Nombre), false) => conFecha.OrderBy(x => x.Nombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.Nombre), true) => conFecha.OrderByDescending(x => x.Nombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.Dni), false) => conFecha.OrderBy(x => x.Dni),
                    (nameof(TrabajadorListaDto.Dni), true) => conFecha.OrderByDescending(x => x.Dni),
                    (nameof(TrabajadorListaDto.EmpleadorNombre), false) => conFecha.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.EmpleadorNombre), true) => conFecha.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.Apellidos),
                    _ => conFecha.OrderBy(x => x.Apellidos).ThenBy(x => x.Nombre)
                };
            // Mismo desempate estable que el camino normal.
            ordenadaConEstado = ordenadaConEstado.ThenBy(x => x.Id);

            var paginaConEstado = await ordenadaConEstado
                .Skip((request.Pagina - 1) * request.TamanoPagina)
                .Take(request.TamanoPagina)
                .ToListAsync(cancellationToken);

            return new ResultadoPaginado<TrabajadorListaDto>(
                await ConDesgloseAsync(
                    paginaConEstado.Select(x => new TrabajadorListaDto(
                        x.Id, x.Nombre, x.Apellidos, x.Dni, x.EmpleadorNombre,
                        CalculoEstadoDocumentalService.PeorEstado(x.PeorFecha, x.HaySinConfirmar, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
                        .ToList(),
                    request, cancellationToken),
                totalConEstado,
                request.Pagina,
                request.TamanoPagina)
            {
                RecuentosPorEstado = recuentosPorEstado
            };
        }

        var total = await consulta.CountAsync(cancellationToken);

        var elementos = await proyeccion
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .ToListAsync(cancellationToken);

        // Solo para los de la página: el badge de la tabla, no un filtro.
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Trabajador, elementos.Select(t => t.Id).ToList(), cancellationToken);

        return new ResultadoPaginado<TrabajadorListaDto>(
            await ConDesgloseAsync(
                elementos.Select(t => t with { EstadoDocumental = estados.GetValueOrDefault(t.Id) }).ToList(),
                request, cancellationToken),
            total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// Añade a las filas YA paginadas su desglose documental, en una sola consulta de documentos para toda la
    /// página. No toca <see cref="TrabajadorListaDto.EstadoDocumental"/>: sale de los mismos documentos y la
    /// misma calculadora, así que coinciden (lo ata <c>DesgloseDocumentalDeTrabajadoresBajoRlsTests</c>).
    /// </summary>
    private async Task<IReadOnlyList<TrabajadorListaDto>> ConDesgloseAsync(
        IReadOnlyList<TrabajadorListaDto> pagina, ObtenerTrabajadoresQuery request, CancellationToken cancellationToken)
    {
        if (!request.ConDesgloseDocumental || pagina.Count == 0)
            return pagina;

        var desgloses = await calculoEstadoDocumental.CalcularDesgloseAsync(
            AmbitoAplicacion.Trabajador, pagina.Select(t => t.Id).ToList(), cancellationToken);

        return pagina
            .Select(t => desgloses.TryGetValue(t.Id, out var desglose)
                ? t with
                {
                    Incidencias = desglose.Incidencias,
                    DocumentosRegistrados = desglose.DocumentosRegistrados,
                    DocumentosVigentes = desglose.DocumentosVigentes
                }
                : t)
            .ToList();
    }
}
