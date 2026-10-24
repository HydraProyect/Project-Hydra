using CaeManager.Domain.Trabajadores;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.EstadoMaterializado;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.EstadoMaterializado;
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
/// otros listados, no un valor que este handler produzca. Sobre ese peor
/// estado se aplican los requisitos OBLIGATORIOS incumplidos de sus Centros
/// (<see cref="RequisitosObligatoriosIncumplidos"/>: un Tipo de ámbito Trabajador que un Centro donde está asignado marca
/// «bloquea el acceso» y falta o ya no vale allí; lo exigido sin esa marca no tiñe, decisión del 2026-10-23)
/// con <see cref="SeveridadEstadoDocumento.ConRequisitosObligatorios"/>: Vencido obligatorio &gt;
/// Pendiente &gt; Vencido que solo avisa (<see cref="TrabajadorListaDto.VencidoSinBloqueo"/>) &gt; Por vencer &gt;
/// Sin confirmar &gt; Sin incidencias, el mismo orden de la clave con la que se filtra, cuenta y ordena antes de paginar.
/// Es el ÚNICO estado de Trabajador: el listado, Empresa 360 y la cabecera de
/// Trabajador 360 lo leen de aquí. Se lee de <c>EstadosEntidad</c> (<see cref="ILectorEstadoMaterializado"/>) cuando el Tenant
/// está completo y el usuario alcanza todos sus Centros; si no, se calcula en lectura, con el mismo resultado.
///
/// <para>
/// <paramref name="CentroId"/> deja solo a los Trabajadores con una Asignación activa en ese Centro. Solo
/// ESTRECHA: se compone con el alcance del usuario, y un Centro que el usuario no puede ver devuelve la lista
/// vacía (no revela quién trabaja allí, aunque el Trabajador sea visible por otro Centro).
/// </para>
///
/// <para>
/// <paramref name="DelegacionEmpresaId"/> y <paramref name="OficioEmpresaId"/> dejan a los Trabajadores de ESA Delegación
/// u Oficio del catálogo de su Empresa (cada Id es de una sola Empresa, así que filtrar por uno ya acota a ella). Solo
/// estrechan, se componen entre sí y con el alcance de Asignación de Cartera de arriba; un Trabajador de Subcontrata no
/// tiene ni Delegación ni Oficio y nunca casa. Ordenar por <c>DelegacionEmpresaNombre</c> u <c>OficioNombre</c> es lo que usa
/// «Agrupar»: las filas con ese catálogo, por nombre y Empresa, seguidas de las que no lo tienen («Sin delegación»,
/// «Sin oficio»), siempre al final.
/// </para>
///
/// <para>
/// <paramref name="ConDesgloseDocumental"/> rellena, para las filas de la página, las incidencias y la fracción
/// «vigentes / registrados» de <see cref="TrabajadorListaDto"/>. Cuesta una consulta de documentos por página, así
/// que hay que pedirlo: lo pide solo quien lo pinta (el listado de Trabajadores, en su carga de página y al
/// refrescar una fila por id). Sin pedirlo, las filas llegan con las incidencias vacías y los contadores a cero;
/// los faltantes y el estado combinado llegan siempre.
/// </para>
/// </summary>
public record ObtenerTrabajadoresQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20,
    Guid? CentroId = null, bool ConDesgloseDocumental = false,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null,
    bool ConRecuentosPorEstado = false, Guid? TrabajadorId = null,
    Guid? DelegacionEmpresaId = null, Guid? OficioEmpresaId = null)
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

    /// <summary>
    /// Delegación del Trabajador en el catálogo de su Empresa, o <c>null</c> (sin Delegación, o Trabajador de Subcontrata).
    /// Solo el Id y el nombre: el contacto de reclamación de la Delegación no viaja en un listado.
    /// </summary>
    public Guid? DelegacionEmpresaId { get; init; }

    public string? DelegacionEmpresaNombre { get; init; }

    /// <summary>Oficio del Trabajador en el catálogo de su Empresa, o <c>null</c>. Es lo que el listado pinta como etiqueta.</summary>
    public Guid? OficioEmpresaId { get; init; }

    public string? OficioNombre { get; init; }

    /// <summary>Documentos operativos del Trabajador (ver <see cref="DesgloseDocumentalDto"/>).</summary>
    public int DocumentosRegistrados { get; init; }

    /// <summary>De los registrados, los que están al día (<see cref="CumplimientoDocumental.EsConforme(EstadoDocumento)"/>).</summary>
    public int DocumentosVigentes { get; init; }

    /// <summary>
    /// Denominador de la cifra «vigentes / registrados» de las filas: los registrados más los Tipos exigidos que faltan
    /// (<see cref="DocumentosFaltantes"/>). Quien tiene «Falta: Documento de identidad» no puede leerse 4/4: lo que
    /// falta tampoco está vigente.
    /// </summary>
    public int DocumentosEsperados => DocumentosRegistrados + DocumentosFaltantes.Count;

    /// <summary>Cuenta ajena, autónomo o voluntario.</summary>
    public TipoContratacion TipoContratacion { get; init; }

    /// <summary>
    /// Voluntario sin Proyecto de tipo Evento vigente ni futuro (ver <c>ActividadVoluntario</c>): sigue en el
    /// listado, marcado, pero sus vencimientos no avisan ni cuentan en el Centro.
    /// </summary>
    public bool VoluntarioInactivo { get; init; }

    /// <summary>
    /// El <see cref="EstadoDocumental"/> es <see cref="EstadoDocumento.Vencido"/> solo por un documento que ningún Centro
    /// visible donde está asignado marca obligatorio («bloquea el acceso»): es un aviso y se pinta en el tono de aviso, no en
    /// rojo (<see cref="SeveridadEstadoDocumento.EsVencidoSinBloqueo"/>, decisión del 2026-10-23). Filtra y cuenta como
    /// Vencido, y se ordena detrás de lo que va en rojo.
    /// </summary>
    public bool VencidoSinBloqueo { get; init; }

    /// <summary>
    /// Nombres de los Tipos de documento que algún Centro con Asignación activa del Trabajador marca como OBLIGATORIOS
    /// («bloquea el acceso», <c>TipoDocumentoCentro.BloqueaAcceso</c>) y de los que no tiene ningún Documento operativo que
    /// valga (la regla única de <see cref="RequisitosObligatoriosIncumplidos"/>, decisión del 2026-10-23). Lo exigido sin
    /// esa marca no está aquí: no bloquea el Centro y no genera alarma. Sin repetir y por nombre. Se rellenan siempre, y
    /// con un requisito obligatorio incumplido (ausente o vencido en ese Centro) <see cref="EstadoDocumental"/> ya llega
    /// como <see cref="EstadoDocumento.Faltante"/> («Pendiente»), o como Vencido si lo incumplido es un obligatorio vencido
    /// (<see cref="SeveridadEstadoDocumento.ConRequisitosObligatorios"/>): ninguna pantalla lo combina por su cuenta.
    ///
    /// <para>
    /// El filtro, los recuentos y la ordenación incluyen los mismos requisitos antes de paginar.
    /// No entran los requisitos de un Proyecto de tipo Evento ni los documentos de la Empresa del Trabajador (un acta que
    /// falta pinta a la Empresa, no a sus Trabajadores). Un voluntario inactivo no los tiene (sus requisitos no avisan) y
    /// solo se miran los Centros que el usuario ve.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> DocumentosFaltantes { get; init; } = [];
}

public class ObtenerTrabajadoresQueryHandler(
    IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos, ICalculoEstadoDocumentalService calculoEstadoDocumental,
    IAsignacionesQueryContext asignacionesContext, IProyectosQueryContext proyectosContext,
    IRequisitosObligatoriosIncumplidosService requisitosObligatorios, ILectorEstadoMaterializado? lectorEstado = null)
    : IRequestHandler<ObtenerTrabajadoresQuery, ResultadoPaginado<TrabajadorListaDto>>
{
    // Sin lector (quien construye el manejador a mano, como los tests del cálculo en lectura) todo se calcula.
    private readonly ILectorEstadoMaterializado lector = lectorEstado ?? SinLectorDeEstadoMaterializado.Instancia;

    public Task<ResultadoPaginado<TrabajadorListaDto>> Handle(
        ObtenerTrabajadoresQuery request, CancellationToken cancellationToken) =>
        ManejarAsync(request, permitirEstadoMaterializado: true, cancellationToken);

    /// <param name="permitirEstadoMaterializado">
    /// El estado sale de <c>EstadosEntidad</c> (<see cref="ILectorEstadoMaterializado"/>) cuando el lector puede servirlo: Tenant
    /// completo, todos los Centros alcanzados y ninguna fila que la tabla no decida. En cualquier otro caso, o si es
    /// <c>false</c>, se calcula en lectura como siempre; es el camino de referencia y el que cubre a un Tenant sin su fila de
    /// versión (relleno inicial sin hacer).
    /// </param>
    private async Task<ResultadoPaginado<TrabajadorListaDto>> ManejarAsync(
        ObtenerTrabajadoresQuery request, bool permitirEstadoMaterializado, CancellationToken cancellationToken)
    {
        var consulta =
            from trabajador in trabajadoresContext.Trabajadores
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
                // Delegación y Oficio del catálogo de su Empresa (nombre para pintar y para «Agrupar»). La FK compuesta de
                // Trabajador garantiza que son de su misma Empresa y Tenant; un Trabajador sin ellos (o de Subcontrata) sale
                // con el nombre nulo, no se pierde de la lista.
            join delegacion in empresasContext.DelegacionesEmpresa on trabajador.DelegacionEmpresaId equals (Guid?)delegacion.Id into delegacionesCoincidentes
            from delegacion in delegacionesCoincidentes.DefaultIfEmpty()
            join oficio in empresasContext.OficiosEmpresa on trabajador.OficioEmpresaId equals (Guid?)oficio.Id into oficiosCoincidentes
            from oficio in oficiosCoincidentes.DefaultIfEmpty()
            select new
            {
                trabajador,
                EmpleadorNombre = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial,
                DelegacionEmpresaNombre = delegacion != null ? delegacion.Nombre : null,
                OficioNombre = oficio != null ? oficio.Nombre : null
            };

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
            var busqueda = request.Busqueda;
            consulta = consulta.Where(x =>
                TextoDeBusqueda.Contiene(x.trabajador.Nombre, busqueda) ||
                TextoDeBusqueda.Contiene(x.trabajador.Apellidos, busqueda) ||
                TextoDeBusqueda.Contiene(x.trabajador.Dni, busqueda) ||
                (x.trabajador.Alias != null && TextoDeBusqueda.Contiene(x.trabajador.Alias, busqueda)));
        }

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.trabajador.EmpresaId == request.EmpresaId);

        if (request.SubcontrataId is not null)
            consulta = consulta.Where(x => x.trabajador.SubcontrataId == request.SubcontrataId);

        // Delegación y Oficio solo estrechan (ver la nota de la Query): el Id viaja del cliente, pero el alcance de
        // arriba ya recortó las filas; un Id de otra Empresa o de otro Tenant no casa con ningún Trabajador visible.
        if (request.DelegacionEmpresaId is { } delegacionId)
            consulta = consulta.Where(x => x.trabajador.DelegacionEmpresaId == delegacionId);

        if (request.OficioEmpresaId is { } oficioId)
            consulta = consulta.Where(x => x.trabajador.OficioEmpresaId == oficioId);

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
            // «Agrupar» por Delegación u Oficio: con catálogo, por nombre y Empresa (un nombre puede repetirse entre
            // Empresas) y el Id para que cada grupo quede contiguo; sin catálogo, al final.
            (nameof(TrabajadorListaDto.DelegacionEmpresaNombre), _) => consulta.OrderBy(x => x.DelegacionEmpresaNombre == null).ThenBy(x => x.DelegacionEmpresaNombre)
                .ThenBy(x => x.EmpleadorNombre).ThenBy(x => x.trabajador.DelegacionEmpresaId).ThenBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            (nameof(TrabajadorListaDto.OficioNombre), _) => consulta.OrderBy(x => x.OficioNombre == null).ThenBy(x => x.OficioNombre)
                .ThenBy(x => x.EmpleadorNombre).ThenBy(x => x.trabajador.OficioEmpresaId).ThenBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre),
            _ => consulta.OrderBy(x => x.trabajador.Apellidos).ThenBy(x => x.trabajador.Nombre)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.trabajador.Id);

        var proyeccion = ordenada.Select(x => new TrabajadorListaDto(
            x.trabajador.Id, x.trabajador.Nombre, x.trabajador.Apellidos, x.trabajador.Dni, x.EmpleadorNombre)
        {
            DelegacionEmpresaId = x.trabajador.DelegacionEmpresaId,
            DelegacionEmpresaNombre = x.DelegacionEmpresaNombre,
            OficioEmpresaId = x.trabajador.OficioEmpresaId,
            OficioNombre = x.OficioNombre
        });

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

            // Solo se materializan las parejas activas (Id, Centro, contratación), nunca el listado completo.
            // La exigencia se resuelve en el servicio compartido, igual que el detalle de cada fila.
            var materializado = permitirEstadoMaterializado
                && await lector.DisponibleAsync(cancellationToken)
                && !await lector.HayFilasQueNoDecideAsync(
                    AmbitoEstadoEntidad.Trabajador, consulta.Select(x => x.trabajador.Id), cancellationToken);
            var faltantes = materializado
                ? RequisitosObligatoriosIncumplidos.Ninguno.PorTrabajador
                : await FaltantesPorTrabajadorAsync(consulta.Select(x => x.trabajador), cancellationToken);
            var idsConObligatorioVencido = faltantes.Where(p => RequisitosObligatoriosIncumplidos.HayVencido(p.Value)).Select(p => p.Key).ToList();
            var idsConObligatorioAusente = faltantes.Where(p => RequisitosObligatoriosIncumplidos.HayAusente(p.Value)).Select(p => p.Key).ToList();
            var claveVencidoSinBloqueo = EstadoDocumentalFiltro.ClaveVencidoSinBloqueo;
            var claveVencido = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vencido);
            var claveFaltante = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Faltante);
            var claveUrgente = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Urgente);
            var claveProximo = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Proximo);
            var claveSinConfirmar = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.SinConfirmar);
            var claveVigente = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.Vigente);
            var claveSinCaducidad = EstadoDocumentalFiltro.ClaveOrdenConFaltantes(EstadoDocumento.SinCaducidad);

            var conFecha =
                from x in consulta
                select new
                {
                    x.trabajador.Id,
                    x.trabajador.Nombre,
                    x.trabajador.Apellidos,
                    x.trabajador.Dni,
                    x.EmpleadorNombre,
                    DelegacionEmpresaId = x.trabajador.DelegacionEmpresaId,
                    x.DelegacionEmpresaNombre,
                    OficioEmpresaId = x.trabajador.OficioEmpresaId,
                    x.OficioNombre,
                    PeorFecha = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Where(d => d.TrabajadorId == x.trabajador.Id)
                        .Min(d => (DateOnly?)d.FechaVencimiento),
                    // MIN ignora las fechas nulas, que son a la vez «no caduca» y «sin
                    // confirmar»: lo sin confirmar se cuenta aparte para no perderlo.
                    HaySinConfirmar = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Any(d => d.TrabajadorId == x.trabajador.Id && d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
                };

            var conEstado = conFecha.Select(x => new
            {
                x.Id,
                x.Nombre,
                x.Apellidos,
                x.Dni,
                x.EmpleadorNombre,
                x.DelegacionEmpresaId,
                x.DelegacionEmpresaNombre,
                x.OficioEmpresaId,
                x.OficioNombre,
                x.PeorFecha,
                x.HaySinConfirmar,
                // En rojo solo lo que bloquea el acceso (SeveridadEstadoDocumento.ConRequisitosObligatorios): un obligatorio
                // vencido, luego uno que falta; un vencido que ningún Centro marca obligatorio es aviso y va detrás.
                ClaveEstado = idsConObligatorioVencido.Contains(x.Id) ? claveVencido
                    : idsConObligatorioAusente.Contains(x.Id) ? claveFaltante
                    : x.PeorFecha != null && x.PeorFecha < hoy ? claveVencidoSinBloqueo
                    : x.PeorFecha != null && x.PeorFecha <= limiteRojo ? claveUrgente
                    : x.PeorFecha != null && x.PeorFecha <= limiteAmbar ? claveProximo
                    : x.HaySinConfirmar ? claveSinConfirmar
                    : x.PeorFecha != null ? claveVigente
                    : claveSinCaducidad
            });

            // Estado materializado: la misma clave, de la fila de EstadosEntidad en vez de la fecha del peor documento y los
            // requisitos incumplidos calculados. Un Trabajador sin fila (alta que el recálculo aún no ha escrito) es uno sin
            // documentos ni requisitos incumplidos. Vencido en ámbar solo si todos sus documentos vencidos son de Tipos que
            // ningún Centro marca obligatorios (el lector ya descartó las filas que eso no decide).
            if (materializado)
            {
                var ordVencido = (short)EstadoDocumento.Vencido;
                var ordFaltante = (short)EstadoDocumento.Faltante;
                var ordUrgente = (short)EstadoDocumento.Urgente;
                var ordProximo = (short)EstadoDocumento.Proximo;
                var ordSinConfirmar = (short)EstadoDocumento.SinConfirmar;
                var ordVigente = (short)EstadoDocumento.Vigente;
                var unidos =
                    from x in consulta
                    join s in lector.Entidades(AmbitoEstadoEntidad.Trabajador) on x.trabajador.Id equals s.EntidadId into coincidentes
                    from s in coincidentes.DefaultIfEmpty()
                    select new
                    {
                        x.trabajador.Id,
                        x.trabajador.Nombre,
                        x.trabajador.Apellidos,
                        x.trabajador.Dni,
                        x.EmpleadorNombre,
                        DelegacionEmpresaId = x.trabajador.DelegacionEmpresaId,
                        x.DelegacionEmpresaNombre,
                        OficioEmpresaId = x.trabajador.OficioEmpresaId,
                        x.OficioNombre,
                        Estado = s == null ? EstadoEntidad.SinEstado : s.Estado,
                        Vencidos = s == null ? 0 : s.Vencidos,
                        VencidosSinBloqueo = s == null ? 0 : s.VencidosSinBloqueo
                    };
                conEstado = unidos.Select(x => new
                {
                    x.Id,
                    x.Nombre,
                    x.Apellidos,
                    x.Dni,
                    x.EmpleadorNombre,
                    x.DelegacionEmpresaId,
                    x.DelegacionEmpresaNombre,
                    x.OficioEmpresaId,
                    x.OficioNombre,
                    PeorFecha = (DateOnly?)null,
                    HaySinConfirmar = false,
                    ClaveEstado = x.Estado == ordVencido
                            ? (x.Vencidos > 0 && x.Vencidos == x.VencidosSinBloqueo ? claveVencidoSinBloqueo : claveVencido)
                        : x.Estado == ordFaltante ? claveFaltante
                        : x.Estado == ordUrgente ? claveUrgente
                        : x.Estado == ordProximo ? claveProximo
                        : x.Estado == ordSinConfirmar ? claveSinConfirmar
                        : x.Estado == ordVigente ? claveVigente
                        : claveSinCaducidad
                });
            }

            // Los recuentos se toman ANTES de filtrar por estado y después de todos los demás filtros: cada
            // cifra de la franja dice cuántas filas quedarían al marcar solo ese estado.
            IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
            if (request.ConRecuentosPorEstado)
            {
                recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(await conEstado
                    .GroupBy(x => x.ClaveEstado)
                    .Select(grupo => new { Clave = grupo.Key, Filas = grupo.Count() })
                    .ToDictionaryAsync(grupo => grupo.Clave, grupo => grupo.Filas, cancellationToken), incluirFaltantes: true);
            }

            if (EstadoDocumentalFiltro.ClavesDeOrden(request.EstadoDocumental, incluirFaltantes: true) is { } clavesPedidas)
                conEstado = conEstado.Where(x => clavesPedidas.Contains(x.ClaveEstado));

            var totalConEstado = await conEstado.CountAsync(cancellationToken);

            // Filtro, franja y orden usan una sola clave traducible a SQL antes de paginar.
            var ordenadaConEstado = ordenaPorEstado
                ? (request.Descendente
                    ? conEstado.OrderByDescending(x => x.ClaveEstado)
                    : conEstado.OrderBy(x => x.ClaveEstado))
                    .ThenBy(x => x.Apellidos).ThenBy(x => x.Nombre)
                : (request.OrdenarPor, request.Descendente) switch
                {
                    (nameof(TrabajadorListaDto.Apellidos), true) => conEstado.OrderByDescending(x => x.Apellidos).ThenBy(x => x.Nombre),
                    (nameof(TrabajadorListaDto.Nombre), false) => conEstado.OrderBy(x => x.Nombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.Nombre), true) => conEstado.OrderByDescending(x => x.Nombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.Dni), false) => conEstado.OrderBy(x => x.Dni),
                    (nameof(TrabajadorListaDto.Dni), true) => conEstado.OrderByDescending(x => x.Dni),
                    (nameof(TrabajadorListaDto.EmpleadorNombre), false) => conEstado.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.EmpleadorNombre), true) => conEstado.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.Apellidos),
                    (nameof(TrabajadorListaDto.DelegacionEmpresaNombre), _) => conEstado.OrderBy(x => x.DelegacionEmpresaNombre == null).ThenBy(x => x.DelegacionEmpresaNombre)
                        .ThenBy(x => x.EmpleadorNombre).ThenBy(x => x.DelegacionEmpresaId).ThenBy(x => x.Apellidos).ThenBy(x => x.Nombre),
                    (nameof(TrabajadorListaDto.OficioNombre), _) => conEstado.OrderBy(x => x.OficioNombre == null).ThenBy(x => x.OficioNombre)
                        .ThenBy(x => x.EmpleadorNombre).ThenBy(x => x.OficioEmpresaId).ThenBy(x => x.Apellidos).ThenBy(x => x.Nombre),
                    _ => conEstado.OrderBy(x => x.Apellidos).ThenBy(x => x.Nombre)
                };
            // Mismo desempate estable que el camino normal.
            ordenadaConEstado = ordenadaConEstado.ThenBy(x => x.Id);

            var paginaConEstado = await ordenadaConEstado
                .Skip((request.Pagina - 1) * request.TamanoPagina)
                .Take(request.TamanoPagina)
                .ToListAsync(cancellationToken);

            var leidoDeLaPagina = materializado
                ? await lector.LeerAsync(
                    AmbitoEstadoEntidad.Trabajador, paginaConEstado.Select(x => x.Id).ToList(), cancellationToken)
                : null;
            // El estado cambió entre decidir el camino y leer la página: se vuelve a empezar por el cálculo de siempre.
            if (materializado && leidoDeLaPagina is null)
                return await ManejarAsync(request, permitirEstadoMaterializado: false, cancellationToken);

            return new ResultadoPaginado<TrabajadorListaDto>(
                await ConDesgloseAsync(
                    paginaConEstado.Select(x => new TrabajadorListaDto(
                        x.Id, x.Nombre, x.Apellidos, x.Dni, x.EmpleadorNombre,
                        leidoDeLaPagina is not null
                            ? null
                            : CalculoEstadoDocumentalService.PeorEstado(x.PeorFecha, x.HaySinConfirmar, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias))
                    {
                        DelegacionEmpresaId = x.DelegacionEmpresaId,
                        DelegacionEmpresaNombre = x.DelegacionEmpresaNombre,
                        OficioEmpresaId = x.OficioEmpresaId,
                        OficioNombre = x.OficioNombre
                    })
                        .ToList(),
                    request, cancellationToken, faltantes, leidoDeLaPagina),
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

        // Solo para los de la página: el badge de la tabla, no un filtro. De la tabla si el lector puede servir la página;
        // si no, calculado en lectura.
        var idsDeLaPagina = elementos.Select(t => t.Id).ToList();
        var leido = permitirEstadoMaterializado
            ? await lector.LeerAsync(AmbitoEstadoEntidad.Trabajador, idsDeLaPagina, cancellationToken)
            : null;
        var estados = leido is null
            ? await calculoEstadoDocumental.CalcularPeorEstadoAsync(AmbitoAplicacion.Trabajador, idsDeLaPagina, cancellationToken)
            : null;

        return new ResultadoPaginado<TrabajadorListaDto>(
            await ConDesgloseAsync(
                elementos.Select(t => t with { EstadoDocumental = estados?.GetValueOrDefault(t.Id) }).ToList(),
                request, cancellationToken, leido: leido),
            total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>
    /// Añade a las filas YA paginadas sus faltantes (siempre) y su desglose documental (si se pide), en una sola
    /// consulta de documentos para toda la página. De <see cref="TrabajadorListaDto.EstadoDocumental"/> solo cambia
    /// lo que los requisitos obligatorios de Centro agravan (<see cref="SeveridadEstadoDocumento.ConRequisitosObligatorios"/>); lo demás sale de los
    /// mismos documentos y la misma calculadora que el desglose, así que coinciden (lo ata
    /// <c>DesgloseDocumentalDeTrabajadoresBajoRlsTests</c>).
    /// </summary>
    private async Task<IReadOnlyList<TrabajadorListaDto>> ConDesgloseAsync(
        IReadOnlyList<TrabajadorListaDto> pagina, ObtenerTrabajadoresQuery request, CancellationToken cancellationToken,
        IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>? faltantesCalculados = null,
        IReadOnlyDictionary<Guid, EstadoLeido>? leido = null)
    {
        pagina = await ConActividadAsync(pagina, cancellationToken);

        if (pagina.Count == 0)
            return pagina;

        var ids = pagina.Select(t => t.Id).ToList();
        if (leido is not null)
        {
            // Estado de la tabla (ILectorEstadoMaterializado): el mismo que deja el cálculo de abajo.
            pagina = pagina
                .Select(t =>
                {
                    var estado = leido.GetValueOrDefault(t.Id, EstadoLeido.SinNada);
                    return t with
                    {
                        EstadoDocumental = estado.Estado,
                        VencidoSinBloqueo = estado.VencidoSinBloqueo,
                        DocumentosFaltantes = estado.DocumentosFaltantes
                    };
                })
                .ToList();
        }
        else
        {
            // Los faltantes entran SIEMPRE en el estado de la fila, pida o no el desglose quien llama: es el mismo estado
            // que la clave de filtro, franja y orden del camino con estado completo (Vencido > Pendiente > Urgente > …).
            // Sin esto, Empresa 360 leía «Sin incidencias» a quien el listado daba como Pendiente.
            var faltantes = faltantesCalculados ?? await FaltantesPorTrabajadorAsync(
                trabajadoresContext.Trabajadores.Where(t => ids.Contains(t.Id)), cancellationToken);

            pagina = pagina
                .Select(t =>
                {
                    var incumplidos = faltantes.GetValueOrDefault(t.Id) ?? [];
                    var vencido = RequisitosObligatoriosIncumplidos.HayVencido(incumplidos);
                    var ausente = RequisitosObligatoriosIncumplidos.HayAusente(incumplidos);
                    return t with
                    {
                        EstadoDocumental = SeveridadEstadoDocumento.ConRequisitosObligatorios(t.EstadoDocumental, vencido, ausente),
                        VencidoSinBloqueo = SeveridadEstadoDocumento.EsVencidoSinBloqueo(t.EstadoDocumental, vencido, ausente),
                        DocumentosFaltantes = RequisitosObligatoriosIncumplidos.NombresAusentes(incumplidos)
                    };
                })
                .ToList();
        }

        if (!request.ConDesgloseDocumental)
            return pagina;

        var desgloses = await calculoEstadoDocumental.CalcularDesgloseAsync(
            AmbitoAplicacion.Trabajador, ids, cancellationToken);

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

    /// <summary>
    /// Los Trabajadores de la consulta que incumplen un requisito obligatorio de algún Centro donde están asignados
    /// (<see cref="RequisitosObligatoriosIncumplidos"/>: ausente o vencido en un Tipo que ese Centro marca «bloquea el acceso»).
    /// La clave dice que el estado va en rojo; el valor, los Tipos que faltan sin ningún documento (el motivo «Falta: …»),
    /// que puede estar vacío si lo incumplido es un vencido. Sirve antes de paginar para el estado completo y sobre los Ids
    /// de la página para el desglose. Lo exigido sin esa marca no entra (decisión del 2026-10-23).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> FaltantesPorTrabajadorAsync(
        IQueryable<Trabajador> trabajadores, CancellationToken cancellationToken)
    {
        // Un voluntario inactivo no genera avisos por requisitos. Los Centros visibles los aplica la evaluación.
        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(
            trabajadoresContext.Trabajadores, proyectosContext, DiaDeNegocio.Hoy());
        return await requisitosObligatorios.DeTrabajadoresAsync(
            trabajadores.Where(t => !inactivos.Contains(t.Id)).Select(t => t.Id), cancellationToken);
    }

    /// <summary>Tipo de contratación y actividad de voluntario de las filas YA paginadas, en una sola consulta.</summary>
    private async Task<IReadOnlyList<TrabajadorListaDto>> ConActividadAsync(
        IReadOnlyList<TrabajadorListaDto> pagina, CancellationToken cancellationToken)
    {
        if (pagina.Count == 0)
            return pagina;

        var ids = pagina.Select(t => t.Id).ToList();
        var inactivos = ActividadVoluntarios.VoluntariosInactivosIds(
            trabajadoresContext.Trabajadores, proyectosContext, DiaDeNegocio.Hoy());
        var actividad = await trabajadoresContext.Trabajadores
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.TipoContratacion, Inactivo = inactivos.Contains(t.Id) })
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        return pagina
            .Select(t => actividad.TryGetValue(t.Id, out var a)
                ? t with { TipoContratacion = a.TipoContratacion, VoluntarioInactivo = a.Inactivo }
                : t)
            .ToList();
    }
}
