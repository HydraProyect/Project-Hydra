using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

using CaeManager.Application.Documentos.Queries.ObtenerEntidadesAsociadasDeDocumentos;

namespace CaeManager.Application.Documentos.Queries.ObtenerDocumentos;

/// <param name="FechaVencimientoHasta">
/// Documentos TrabajadorId? Ambito? Busqueda? con vencimiento entre hoy y
/// esta fecha (Preventivo, Parte XVI PROMPT 03) — a diferencia de
/// <paramref name="Estado"/>, que usa los umbrales configurables del
/// tenant (UmbralAmbarDias/UmbralRojoDias), esta es una ventana temporal
/// fija elegida por quien llama (7/30/90 días); un Vencido no entra nunca
/// aquí, "próximo a vencer" excluye "ya vencido" por diseño de esta vista.
/// Se combina con <paramref name="Estado"/> si ambos llegan, aunque en la
/// práctica se usan por separado.
/// </param>
/// <param name="Estados">
/// Varios estados a la vez (la franja de estado del listado deja marcar más de uno): pasa el Documento que esté
/// en cualquiera. Se suma a <paramref name="Estado"/> si llegan los dos.
/// </param>
/// <param name="ConRecuentosPorEstado">
/// Rellena <c>ResultadoPaginado.RecuentosPorEstado</c>: Documentos por estado con todos los demás filtros
/// aplicados y sin el de estado. Solo lo pide el listado, que es quien pinta la franja: cuesta una consulta más.
/// </param>
/// <param name="TipoDocumentoId">Solo los Documentos de ese Tipo de documento.</param>
/// <param name="ProveedorPlataformaCaeId">
/// Solo los Documentos con alguna acreditación en un canal de esa plataforma CAE, en cualquier estado: los mismos
/// que pintan esa plataforma en la columna «Plataformas» del listado.
/// </param>
/// <param name="AgruparPorAmbito">Ordena primero por ámbito y calcula su resumen sobre todos los resultados filtrados.</param>
public record ObtenerDocumentosQuery(
    Guid? TrabajadorId, AmbitoAplicacion? Ambito, string? Busqueda, EstadoDocumento? Estado = null,
    int Pagina = 1, int TamanoPagina = 20, Guid? PropietarioId = null,
    string? OrdenarPor = null, bool Descendente = false, DateOnly? FechaVencimientoHasta = null,
    IReadOnlyCollection<EstadoDocumento>? Estados = null, bool ConRecuentosPorEstado = false,
    Guid? DocumentoId = null, Guid? TipoDocumentoId = null, Guid? ProveedorPlataformaCaeId = null,
    bool AgruparPorAmbito = false, bool ConDestinosEnCentros = false)
    : IRequest<ResultadoPaginado<DocumentoListaDto>>
{
    /// <summary>
    /// Valor de <see cref="OrdenarPor"/> para el orden de inicio del listado: lo que más urge primero (la clave de
    /// <c>EstadoDocumentalFiltro.ClaveOrden</c>) y, dentro de cada estado, por vencimiento. No es una columna:
    /// lo pide el listado mientras el usuario no elige orden. Sin <see cref="OrdenarPor"/> el orden sigue siendo
    /// el de siempre (emisión más reciente primero), que es el que conocen la API y las fichas.
    /// </summary>
    public const string OrdenPorSeveridad = "Severidad";
}

/// <summary>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — una entrada por CanalGestionDocumental aplicable, no por ProveedorPlataformaCae (el mismo proveedor puede tener más de un acceso).</summary>
public record AcreditacionResumenDto(Guid Id, string NombrePlataforma, EstadoAcreditacion Estado);

public record DocumentoListaDto(
    Guid Id,
    AmbitoAplicacion Ambito,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    DateOnly FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl,
    IReadOnlyList<AcreditacionResumenDto> Acreditaciones,
    // Destino del icono 360 de la fila: la página del TIPO de documento (/documentos/tipos/{id}).
    // Opcional y al final: ningún otro productor cambia; sin él (Guid.Empty) la fila no pinta el icono.
    Guid TipoDocumentoId = default,
    [property: JsonIgnore] IReadOnlyList<DocumentoEnCentroListaDto>? DestinosEnCentros = null);

/// <param name="ClienteNombre">Nombre del Cliente empresarial contraparte del Centro, no del Tenant propietario.</param>
public record DocumentoEnCentroListaDto(Guid CentroId, string CentroNombre, string ClienteNombre,
    string PlataformaNombre, EstadoDocumento Estado, DateOnly? EnToleranciaHasta = null);

/// <summary>
/// Une los Documentos de Trabajador/Cliente/Empresa (cada uno vive en la
/// misma tabla pero con un único FK de propietario relleno) en una sola
/// lista paginada — se resuelven por separado (cada ámbito hace su propio
/// join al propietario que le corresponde) y se combinan con Concat antes
/// de paginar, en vez de un LEFT JOIN triple. El semáforo (Estado) se
/// calcula en memoria con CalculadoraEstadoDocumento — la misma función que
/// usan el Dashboard y RenovarDocumentoCommand — para que nunca haya dos
/// sitios de la aplicación mostrando vigencias distintas (ver Project-Hydra-Negocio/tecnico/DATABASE.md).
/// </summary>
public class ObtenerDocumentosQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext)
    : IRequestHandler<ObtenerDocumentosQuery, ResultadoPaginado<DocumentoListaDto>>,
    IRequestHandler<ObtenerEntidadesAsociadasDeDocumentosQuery, ResultadoPaginado<EntidadAsociadaDocumentoDto>>
{
    public async Task<ResultadoPaginado<DocumentoListaDto>> Handle(ObtenerDocumentosQuery request, CancellationToken cancellationToken)
    {
        var consulta = await ConstruirUniversoAsync(cancellationToken);

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // El constructor compartido ya acota las cinco ramas al alcance: solo estrecha.
        if (request.DocumentoId is not null)
            consulta = consulta.Where(x => x.Id == request.DocumentoId);

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.TrabajadorId == request.TrabajadorId);

        if (request.Ambito is not null)
            consulta = consulta.Where(x => x.Ambito == request.Ambito);

        if (request.PropietarioId is not null)
            consulta = consulta.Where(x => x.PropietarioId == request.PropietarioId);

        // Los dos filtros recortan la unión ya limitada al alcance del usuario: nunca la amplían.
        if (request.TipoDocumentoId is { } tipoDocumentoId)
            consulta = consulta.Where(x => x.TipoDocumentoId == tipoDocumentoId);

        if (request.ProveedorPlataformaCaeId is { } proveedorPlataformaCaeId)
        {
            // La subconsulta se acota a los Centros visibles del usuario, igual que el destino por Centro de cada fila
            // (ObtenerDestinosEnCentrosAsync): sin ello el filtro serviría de oráculo de acreditaciones en Centros que no ve.
            var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
            var documentosEnPlataforma =
                from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
                join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
                where canal.ProveedorPlataformaCaeId == proveedorPlataformaCaeId
                    && (centrosVisibles == null || centrosVisibles.Contains(canal.CentroId))
                select acreditacion.DocumentoId;
            consulta = consulta.Where(x => documentosEnPlataforma.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda;
            consulta = consulta.Where(x =>
                TextoDeBusqueda.Contiene(x.PropietarioNombre, busqueda) ||
                TextoDeBusqueda.Contiene(x.TipoDocumentoNombre, busqueda));
        }

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // Equivalencias con CalculadoraEstadoDocumento, que compara "días
        // restantes" contra los umbrales: días <= umbral es lo mismo que
        // fechaVencimiento <= hoy + umbral. Las usan tanto el filtro por
        // Estado como el orden por Estado, así que se calculan una vez.
        var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);
        var limiteAmbar = hoy.AddDays(parametros.UmbralAmbarDias);

        // El Estado se sigue calculando con CalculadoraEstadoDocumento —
        // fuente única de verdad, ver el comentario de clase—, pero el
        // *filtro* por Estado se traduce a un rango de fechas equivalente
        // para que SQL pueda hacerlo. Antes no se traducía, y eso obligaba a
        // materializar todos los Documentos del tenant en cada carga de la
        // pantalla para paginar en memoria.
        if (request.FechaVencimientoHasta is { } hasta)
            consulta = consulta.Where(x => x.FechaVencimiento != null && x.FechaVencimiento >= hoy && x.FechaVencimiento <= hasta);

        // La clave de estado es el mismo CASE de umbrales con el que se ordena más abajo (el orden de
        // EstadoDocumentalFiltro.ClaveOrden: 0 Vencido, 1 Urgente, 2 Próximo, 3 Sin confirmar, 4 Vigente,
        // 5 Sin caducidad). «No caduca» y «sin confirmar» se distinguen por EstadoVigencia, nunca por una fecha
        // nula. Contar, filtrar y ordenar por la misma expresión garantiza que parten los Documentos igual.
        //
        // Los recuentos se toman ANTES de filtrar por estado y después de todos los demás filtros: cada cifra
        // de la franja dice cuántos Documentos quedarían al marcar solo ese estado.
        IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
        if (request.ConRecuentosPorEstado)
        {
            recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(await consulta
                .GroupBy(x =>
                    x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4)
                .Select(grupo => new { Clave = grupo.Key, Filas = grupo.Count() })
                .ToDictionaryAsync(grupo => grupo.Clave, grupo => grupo.Filas, cancellationToken));
        }

        // El Estado se sigue calculando con CalculadoraEstadoDocumento —
        // fuente única de verdad, ver el comentario de clase—, pero el
        // *filtro* por Estado se traduce a esa clave, equivalente a un rango
        // de fechas, para que SQL pueda hacerlo. Antes no se traducía, y eso
        // obligaba a materializar todos los Documentos del tenant en cada
        // carga de la pantalla para paginar en memoria. Si alguna vez cambia
        // la calculadora, la clave cambia con ella —
        // DocumentosPaginacionEnSqlTests compara ambas para que no se separen
        // en silencio.
        var estadosPedidos = (request.Estados ?? []).ToList();
        if (request.Estado is { } unEstado && !estadosPedidos.Contains(unEstado))
            estadosPedidos.Add(unEstado);

        if (estadosPedidos.Count > 0)
        {
            // «En tolerancia» es un estado de contexto de Centro (VigenciaEnCentro) y Faltante solo lo emiten las
            // Alertas: esta lista nunca los produce, así que su clave (6) no coincide con ninguna fila. Filtrar
            // solo por ellos no devuelve nada (y no «todo», que sacaría los vencidos con otro rótulo).
            var clavesPedidas = estadosPedidos.Select(estado => EstadoDocumentalFiltro.ClaveOrden(estado)).Distinct().ToList();
            consulta = consulta.Where(x => clavesPedidas.Contains(
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4));
        }

        var total = await consulta.CountAsync(cancellationToken);

        IReadOnlyDictionary<string, ResumenDeGrupo>? resumenPorGrupo = null;
        if (request.AgruparPorAmbito)
        {
            // Agregados del mismo universo filtrado antes de paginar; nunca un recuento de la página visible.
            var agregados = await consulta.GroupBy(x => new
                {
                    x.Ambito,
                    ClaveEstado = x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                        : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                        : x.FechaVencimiento < hoy ? 0
                        : x.FechaVencimiento <= limiteRojo ? 1
                        : x.FechaVencimiento <= limiteAmbar ? 2
                        : 4
                })
                .Select(g => new { g.Key.Ambito, g.Key.ClaveEstado, Total = g.Count() })
                .ToListAsync(cancellationToken);
            resumenPorGrupo = agregados.GroupBy(x => x.Ambito).ToDictionary(
                g => g.Key.ToString(),
                g => new ResumenDeGrupo(g.Sum(x => x.Total),
                    EstadoDocumentalFiltro.RecuentosPorEstado(g.ToDictionary(x => x.ClaveEstado, x => x.Total))
                        .Where(x => x.Value > 0).ToDictionary(x => x.Key, x => x.Value)));
        }

        IOrderedQueryable<FilaDocumental> OrdenarPor<TClave>(Expression<Func<FilaDocumental, TClave>> clave, bool descendente = false)
        {
            if (request.AgruparPorAmbito)
            {
                var agrupada = consulta.OrderBy(x => x.Ambito);
                return descendente ? agrupada.ThenByDescending(clave) : agrupada.ThenBy(clave);
            }
            return descendente ? consulta.OrderByDescending(clave) : consulta.OrderBy(clave);
        }

        // Lista blanca de columnas ordenables (nombres de propiedad de
        // DocumentoListaDto, que es lo que envía QuickGrid): un OrdenarPor
        // desconocido cae al orden por defecto, nunca se interpola en SQL.
        // "Estado" no está persistido, pero sí lo está FechaVencimiento, así
        // que se ordena por el mismo CASE de umbrales que usa el filtro de
        // arriba — exacto y resuelto en la base de datos. Ascendente deja
        // primero lo que más urge, que es lo que el gestor espera del primer
        // clic en esa cabecera; el orden es el de
        // EstadoDocumentalFiltro.ClaveOrden (lo sin confirmar entre lo malo
        // conocido y lo vigente).
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(DocumentoListaDto.PropietarioNombre), false) => OrdenarPor(x => x.PropietarioNombre),
            (nameof(DocumentoListaDto.PropietarioNombre), true) => OrdenarPor(x => x.PropietarioNombre, true),
            (nameof(DocumentoListaDto.TipoDocumentoNombre), false) => OrdenarPor(x => x.TipoDocumentoNombre),
            (nameof(DocumentoListaDto.TipoDocumentoNombre), true) => OrdenarPor(x => x.TipoDocumentoNombre, true),
            (nameof(DocumentoListaDto.Ambito), false) => OrdenarPor(x => x.Ambito),
            (nameof(DocumentoListaDto.Ambito), true) => OrdenarPor(x => x.Ambito, true),
            (nameof(DocumentoListaDto.FechaEmision), false) => OrdenarPor(x => x.FechaEmision),
            (nameof(DocumentoListaDto.FechaEmision), true) => OrdenarPor(x => x.FechaEmision, true),
            (nameof(DocumentoListaDto.FechaVencimiento), false) => OrdenarPor(x => x.FechaVencimiento),
            (nameof(DocumentoListaDto.FechaVencimiento), true) => OrdenarPor(x => x.FechaVencimiento, true),
            (nameof(DocumentoListaDto.Estado), false) => OrdenarPor(x =>
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4),
            (nameof(DocumentoListaDto.Estado), true) => OrdenarPor(x =>
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4, true),
            // Orden de inicio del listado: la misma clave de estado y, dentro de cada estado, lo que antes vence.
            (ObtenerDocumentosQuery.OrdenPorSeveridad, _) => OrdenarPor(x =>
                x.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca ? 5
                : x.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar ? 3
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 4).ThenBy(x => x.FechaVencimiento),
            _ => OrdenarPor(x => x.FechaEmision, true)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.Id);

        var pagina = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new FilaDocumental
            {
                Id = x.Id,
                Ambito = x.Ambito,
                PropietarioNombre = x.PropietarioNombre,
                TipoDocumentoNombre = x.TipoDocumentoNombre,
                TipoDocumentoId = x.TipoDocumentoId,
                FechaEmision = x.FechaEmision,
                EstadoVigencia = x.EstadoVigencia,
                FechaVencimiento = x.FechaVencimiento,
                ArchivoUrl = x.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        // Ahora solo sobre la página, no sobre todo el tenant.
        var documentoIds = pagina.Select(d => d.Id).ToList();
        var acreditacionesPorDocumento = await ObtenerAcreditacionesPorDocumentoAsync(documentoIds, cancellationToken);
        var destinos = request.ConDestinosEnCentros
            ? await ObtenerDestinosEnCentrosAsync(pagina, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, cancellationToken)
            : null;

        var elementos = pagina
            .Select(d => new DocumentoListaDto(
                d.Id, d.Ambito, d.PropietarioNombre, d.TipoDocumentoNombre, d.FechaEmision, d.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    d.EstadoVigencia, d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                d.ArchivoUrl,
                acreditacionesPorDocumento.GetValueOrDefault(d.Id, []),
                d.TipoDocumentoId,
                destinos?.GetValueOrDefault(d.Id, [])))
            .ToList();

        return new ResultadoPaginado<DocumentoListaDto>(elementos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado,
            ResumenPorGrupo = resumenPorGrupo
        };
    }

    public async Task<ResultadoPaginado<EntidadAsociadaDocumentoDto>> Handle(
        ObtenerEntidadesAsociadasDeDocumentosQuery request, CancellationToken cancellationToken)
    {
        var consulta = await ConstruirUniversoAsync(cancellationToken);
        if (request.Ambito is { } ambito)
            consulta = consulta.Where(x => x.Ambito == ambito);
        if (!string.IsNullOrWhiteSpace(request.Busqueda))
            consulta = consulta.Where(x => TextoDeBusqueda.Contiene(x.PropietarioNombre, request.Busqueda));

        var opciones = consulta.Where(x => x.PropietarioId != null)
            .Select(x => new { Id = x.PropietarioId!.Value, x.Ambito, Nombre = x.PropietarioNombre })
            .Distinct();
        var total = await opciones.CountAsync(cancellationToken);
        var pagina = Math.Max(1, request.Pagina);
        var tamano = Math.Clamp(request.TamanoPagina, 1, 50);
        var elementos = await opciones.OrderBy(x => x.Nombre).ThenBy(x => x.Ambito).ThenBy(x => x.Id)
            .Skip((pagina - 1) * tamano).Take(tamano)
            .Select(x => new EntidadAsociadaDocumentoDto(x.Id, x.Ambito, x.Nombre))
            .ToListAsync(cancellationToken);
        return new(elementos, total, pagina, tamano);
    }

    // Ambas lecturas comparten documentos operativos y restricciones de visibilidad por ámbito.
    private async Task<IQueryable<FilaDocumental>> ConstruirUniversoAsync(CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var deTrabajador =
            from documento in documentosContext.Documentos.Operativos()
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Trabajador,
                TrabajadorId = (Guid?)documento.TrabajadorId,
                PropietarioId = (Guid?)documento.TrabajadorId,
                PropietarioNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deCliente =
            from documento in documentosContext.Documentos.Operativos()
            where documento.ClienteId != null
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(documento.ClienteId!.Value)
            // Documento.ClienteId ya apunta a Empresas (F3): el propietario "Cliente"
            // de un Documento se resuelve contra Empresas, no contra Clientes (congelada).
            join cliente in empresasContext.Empresas on documento.ClienteId!.Value equals cliente.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Cliente,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ClienteId,
                PropietarioNombre = cliente.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deEmpresa =
            from documento in documentosContext.Documentos.Operativos()
            where documento.EmpresaId != null
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join empresa in empresasContext.Empresas on documento.EmpresaId!.Value equals empresa.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Empresa,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.EmpresaId,
                PropietarioNombre = empresa.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deVehiculo =
            from documento in documentosContext.Documentos.Operativos()
            where documento.VehiculoId != null
            where vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(documento.VehiculoId!.Value)
            join vehiculo in vehiculosContext.Vehiculos on documento.VehiculoId!.Value equals vehiculo.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Vehiculo,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.VehiculoId,
                PropietarioNombre = vehiculo.Nombre + " (" + vehiculo.NumeroPlaca + ")",
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        var deProyecto =
            from documento in documentosContext.Documentos.Operativos()
            where documento.ProyectoId != null
            join proyecto in proyectosContext.Proyectos on documento.ProyectoId!.Value equals proyecto.Id
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(proyecto.ClienteId)
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new FilaDocumental
            {
                Id = documento.Id,
                Ambito = AmbitoAplicacion.Proyecto,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ProyectoId,
                PropietarioNombre = proyecto.Nombre,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                TipoDocumentoId = tipoDocumento.Id,
                FechaEmision = documento.FechaEmision,
                EstadoVigencia = documento.EstadoVigencia,
                FechaVencimiento = documento.FechaVencimiento,
                ArchivoUrl = documento.ArchivoUrl
            };

        return deTrabajador.Concat(deCliente).Concat(deEmpresa).Concat(deVehiculo).Concat(deProyecto);

    }

    private sealed class FilaDocumental
    {
        public Guid Id { get; init; }
        public AmbitoAplicacion Ambito { get; init; }
        public Guid? TrabajadorId { get; init; }
        public Guid? PropietarioId { get; init; }
        public string PropietarioNombre { get; init; } = string.Empty;
        public string TipoDocumentoNombre { get; init; } = string.Empty;
        public Guid TipoDocumentoId { get; init; }
        public DateOnly FechaEmision { get; init; }
        public EstadoVigenciaDocumento EstadoVigencia { get; init; }
        public DateOnly? FechaVencimiento { get; init; }
        public string? ArchivoUrl { get; init; }
    }

    private async Task<Dictionary<Guid, List<DocumentoEnCentroListaDto>>> ObtenerDestinosEnCentrosAsync(
        IReadOnlyList<FilaDocumental> pagina, DateOnly hoy, int umbralAmbar, int umbralRojo, CancellationToken cancellationToken)
    {
        if (pagina.Count == 0) return [];
        var documentoIds = pagina.Select(x => x.Id).ToList();
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var crudas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where documentoIds.Contains(acreditacion.DocumentoId)
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            where centrosVisibles == null || centrosVisibles.Contains(centro.Id)
            join clienteEmpresarial in empresasContext.Empresas on centro.ClienteId equals clienteEmpresarial.Id
            select new
            {
                acreditacion.DocumentoId, CentroId = centro.Id, CentroNombre = centro.Nombre,
                ClienteEmpresarialId = clienteEmpresarial.Id, ClienteNombre = clienteEmpresarial.RazonSocial,
                canal.ProveedorPlataformaCaeId, canal.Tipo
            }).ToListAsync(cancellationToken);
        if (crudas.Count == 0) return [];

        var centroIds = crudas.Select(x => x.CentroId).Distinct().ToList();
        var clienteIds = crudas.Select(x => x.ClienteEmpresarialId).Distinct().ToList();
        var tipoIds = pagina.Select(x => x.TipoDocumentoId).Distinct().ToList();
        var proveedorIds = crudas.Where(x => x.ProveedorPlataformaCaeId != null)
            .Select(x => x.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var proveedores = await proveedoresContext.ProveedoresPlataformaCae.Where(x => proveedorIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Nombre, cancellationToken);
        var filasCentro = await tiposDocumentoContext.TiposDocumentoCentros
            .Where(x => centroIds.Contains(x.CentroId) && tipoIds.Contains(x.TipoDocumentoId))
            .ToListAsync(cancellationToken);
        var condicionesCentro = filasCentro.ToDictionary(x => (x.CentroId, x.TipoDocumentoId));
        var tolerancias = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, clienteIds, tipoIds, cancellationToken);
        var documentos = pagina.ToDictionary(x => x.Id);

        return crudas.GroupBy(x => x.DocumentoId).ToDictionary(g => g.Key, g => g.Select(x =>
        {
            var documento = documentos[x.DocumentoId];
            var propio = CalculadoraEstadoDocumento.Calcular(documento.EstadoVigencia, documento.FechaVencimiento,
                hoy, umbralAmbar, umbralRojo);
            var condiciones = VigenciaEnCentro.Condiciones(
                condicionesCentro.GetValueOrDefault((x.CentroId, documento.TipoDocumentoId)),
                tolerancias.TryGetValue((x.ClienteEmpresarialId, documento.TipoDocumentoId), out var dias) ? dias : null);
            var contextual = VigenciaEnCentro.Aplicar(propio, documento.EstadoVigencia, documento.FechaVencimiento,
                documento.FechaEmision, condiciones, hoy);
            var plataforma = x.ProveedorPlataformaCaeId is { } proveedorId
                ? proveedores.GetValueOrDefault(proveedorId, "Plataforma")
                : x.Tipo == TipoCanalGestion.Email ? "Sin portal" : "Plataforma";
            return new DocumentoEnCentroListaDto(x.CentroId, x.CentroNombre, x.ClienteNombre,
                plataforma, contextual.Estado, contextual.EnToleranciaHasta);
        }).Distinct().OrderBy(x => x.CentroNombre).ThenBy(x => x.PlataformaNombre).ThenBy(x => x.CentroId).ToList());
    }

    /// <summary>Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — badges por plataforma. Solo sobre la página ya paginada, nunca sobre todo el tenant.</summary>
    private async Task<Dictionary<Guid, List<AcreditacionResumenDto>>> ObtenerAcreditacionesPorDocumentoAsync(
        List<Guid> documentoIds, CancellationToken cancellationToken)
    {
        if (documentoIds.Count == 0) return [];

        var crudas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            where documentoIds.Contains(acreditacion.DocumentoId)
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            select new { acreditacion.DocumentoId, acreditacion.Id, acreditacion.Estado, canal.ProveedorPlataformaCaeId })
            .ToListAsync(cancellationToken);

        var proveedorIds = crudas.Where(c => c.ProveedorPlataformaCaeId is not null)
            .Select(c => c.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var nombresProveedor = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return crudas
            .GroupBy(c => c.DocumentoId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(c => new AcreditacionResumenDto(
                    c.Id, c.ProveedorPlataformaCaeId is { } id ? nombresProveedor.GetValueOrDefault(id, "Plataforma") : "Plataforma", c.Estado))
                    .ToList());
    }
}
