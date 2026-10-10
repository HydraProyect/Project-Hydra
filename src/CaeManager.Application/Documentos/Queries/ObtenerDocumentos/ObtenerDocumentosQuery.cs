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
public record ObtenerDocumentosQuery(
    Guid? TrabajadorId, AmbitoAplicacion? Ambito, string? Busqueda, EstadoDocumento? Estado = null,
    int Pagina = 1, int TamanoPagina = 20, Guid? PropietarioId = null,
    string? OrdenarPor = null, bool Descendente = false, DateOnly? FechaVencimientoHasta = null,
    IReadOnlyCollection<EstadoDocumento>? Estados = null, bool ConRecuentosPorEstado = false,
    Guid? DocumentoId = null, Guid? TipoDocumentoId = null, Guid? ProveedorPlataformaCaeId = null)
    : IRequest<ResultadoPaginado<DocumentoListaDto>>
{
    /// <summary>
    /// Valor de <see cref="OrdenarPor"/> para el orden de inicio del listado: lo que más urge primero (la clave de
    /// <c>EstadoDocumentalFiltro.ClaveOrden</c>) y, dentro de cada estado, por vencimiento. No es una columna:
    /// lo pide el listado mientras el usuario no elige orden. Sin <see cref="OrdenarPor"/> el orden sigue siendo
    /// el de siempre (emisión más reciente primero), que es el que conocen la API y las fichas.
    /// </summary>
    public const string OrdenPorSeveridad = "Severidad";

    /// <summary>Lo que decide qué Documentos se listan: todos los parámetros menos el orden, la paginación y el interruptor de los recuentos.</summary>
    internal FiltrosDeDocumentos Filtros() => new(
        TrabajadorId, Ambito, Busqueda, Estado, Estados, PropietarioId, FechaVencimientoHasta, DocumentoId,
        TipoDocumentoId, ProveedorPlataformaCaeId);
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
    Guid TipoDocumentoId = default);

/// <summary>
/// La lista paginada de Documentos. Qué Documentos entran —la unión de los cinco ámbitos, el alcance del usuario,
/// los filtros y la clave de estado en SQL— lo decide <see cref="DocumentosFiltrados"/>, el mismo punto del que sale
/// el resumen por Tipo de documento de la lista agrupada; aquí solo se cuenta, se ordena, se pagina y se proyecta.
/// El semáforo (Estado) de cada fila se calcula en memoria con CalculadoraEstadoDocumento — la misma función que
/// usan el Dashboard y RenovarDocumentoCommand — para que nunca haya dos sitios de la aplicación mostrando
/// vigencias distintas (ver Project-Hydra-Negocio/tecnico/DATABASE.md).
/// </summary>
public class ObtenerDocumentosQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext)
    : IRequestHandler<ObtenerDocumentosQuery, ResultadoPaginado<DocumentoListaDto>>
{
    public async Task<ResultadoPaginado<DocumentoListaDto>> Handle(ObtenerDocumentosQuery request, CancellationToken cancellationToken)
    {
        var documentos = await DocumentosFiltrados.ComponerAsync(
            new FuentesDeDocumentos(
                configuracionContext, documentosContext, empresasContext, proyectosContext, tiposDocumentoContext,
                trabajadoresContext, vehiculosContext, alcanceDatos, centrosContext),
            request.Filtros(),
            cancellationToken);
        var claveDeEstado = documentos.ClaveDeEstado;

        // Los recuentos se toman ANTES de filtrar por estado y después de todos los demás filtros: cada cifra
        // de la franja dice cuántos Documentos quedarían al marcar solo ese estado. Se cuentan con la misma clave
        // con la que se filtra y se ordena, así que parten los Documentos igual.
        IReadOnlyDictionary<string, int>? recuentosPorEstado = null;
        if (request.ConRecuentosPorEstado)
        {
            recuentosPorEstado = EstadoDocumentalFiltro.RecuentosPorEstado(await documentos.SinFiltroDeEstado
                .GroupBy(claveDeEstado)
                .Select(grupo => new { Clave = grupo.Key, Filas = grupo.Count() })
                .ToDictionaryAsync(grupo => grupo.Clave, grupo => grupo.Filas, cancellationToken));
        }

        var consulta = documentos.Filtrados;
        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables (nombres de propiedad de
        // DocumentoListaDto, que es lo que envía QuickGrid): un OrdenarPor
        // desconocido cae al orden por defecto, nunca se interpola en SQL.
        // "Estado" no está persistido, pero sí lo está FechaVencimiento, así
        // que se ordena por la clave de estado de DocumentosFiltrados, la
        // misma expresión con la que se filtra y se cuenta — exacta y
        // resuelta en la base de datos. Ascendente deja
        // primero lo que más urge, que es lo que el gestor espera del primer
        // clic en esa cabecera; el orden es el de
        // EstadoDocumentalFiltro.ClaveOrden (lo sin confirmar entre lo malo
        // conocido y lo vigente).
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(DocumentoListaDto.PropietarioNombre), false) => consulta.OrderBy(x => x.PropietarioNombre),
            (nameof(DocumentoListaDto.PropietarioNombre), true) => consulta.OrderByDescending(x => x.PropietarioNombre),
            (nameof(DocumentoListaDto.TipoDocumentoNombre), false) => consulta.OrderBy(x => x.TipoDocumentoNombre),
            (nameof(DocumentoListaDto.TipoDocumentoNombre), true) => consulta.OrderByDescending(x => x.TipoDocumentoNombre),
            (nameof(DocumentoListaDto.Ambito), false) => consulta.OrderBy(x => x.Ambito),
            (nameof(DocumentoListaDto.Ambito), true) => consulta.OrderByDescending(x => x.Ambito),
            (nameof(DocumentoListaDto.FechaEmision), false) => consulta.OrderBy(x => x.FechaEmision),
            (nameof(DocumentoListaDto.FechaEmision), true) => consulta.OrderByDescending(x => x.FechaEmision),
            (nameof(DocumentoListaDto.FechaVencimiento), false) => consulta.OrderBy(x => x.FechaVencimiento),
            (nameof(DocumentoListaDto.FechaVencimiento), true) => consulta.OrderByDescending(x => x.FechaVencimiento),
            (nameof(DocumentoListaDto.Estado), false) => consulta.OrderBy(claveDeEstado),
            (nameof(DocumentoListaDto.Estado), true) => consulta.OrderByDescending(claveDeEstado),
            // Orden de inicio del listado: la misma clave de estado y, dentro de cada estado, lo que antes vence.
            (ObtenerDocumentosQuery.OrdenPorSeveridad, _) => consulta.OrderBy(claveDeEstado).ThenBy(x => x.FechaVencimiento),
            _ => consulta.OrderByDescending(x => x.FechaEmision)
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
            .Select(x => new
            {
                x.Id,
                x.Ambito,
                x.PropietarioNombre,
                x.TipoDocumentoNombre,
                x.TipoDocumentoId,
                x.FechaEmision,
                x.EstadoVigencia,
                x.FechaVencimiento,
                x.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        // Ahora solo sobre la página, no sobre todo el tenant.
        var documentoIds = pagina.Select(d => d.Id).ToList();
        var acreditacionesPorDocumento = await ObtenerAcreditacionesPorDocumentoAsync(documentoIds, cancellationToken);

        var elementos = pagina
            .Select(d => new DocumentoListaDto(
                d.Id, d.Ambito, d.PropietarioNombre, d.TipoDocumentoNombre, d.FechaEmision, d.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    d.EstadoVigencia, d.FechaVencimiento, documentos.Hoy, documentos.UmbralAmbarDias, documentos.UmbralRojoDias),
                d.ArchivoUrl,
                acreditacionesPorDocumento.GetValueOrDefault(d.Id, []),
                d.TipoDocumentoId))
            .ToList();

        return new ResultadoPaginado<DocumentoListaDto>(elementos, total, request.Pagina, request.TamanoPagina)
        {
            RecuentosPorEstado = recuentosPorEstado
        };
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
