using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
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
public record ObtenerDocumentosQuery(
    Guid? TrabajadorId, AmbitoAplicacion? Ambito, string? Busqueda, EstadoDocumento? Estado = null,
    int Pagina = 1, int TamanoPagina = 20, Guid? PropietarioId = null,
    string? OrdenarPor = null, bool Descendente = false, DateOnly? FechaVencimientoHasta = null)
    : IRequest<ResultadoPaginado<DocumentoListaDto>>;

/// <summary>docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — una entrada por CanalGestionDocumental aplicable, no por ProveedorPlataformaCae (el mismo proveedor puede tener más de un acceso).</summary>
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
    IReadOnlyList<AcreditacionResumenDto> Acreditaciones);

/// <summary>
/// Une los Documentos de Trabajador/Cliente/Empresa (cada uno vive en la
/// misma tabla pero con un único FK de propietario relleno) en una sola
/// lista paginada — se resuelven por separado (cada ámbito hace su propio
/// join al propietario que le corresponde) y se combinan con Concat antes
/// de paginar, en vez de un LEFT JOIN triple. El semáforo (Estado) se
/// calcula en memoria con CalculadoraEstadoDocumento — la misma función que
/// usan el Dashboard y RenovarDocumentoCommand — para que nunca haya dos
/// sitios de la aplicación mostrando vigencias distintas (ver DATABASE.md).
/// </summary>
public class ObtenerDocumentosQueryHandler(IClientesQueryContext clientesContext, IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, IEmpresasQueryContext empresasContext, IProyectosQueryContext proyectosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos, ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext)
    : IRequestHandler<ObtenerDocumentosQuery, ResultadoPaginado<DocumentoListaDto>>
{
    public async Task<ResultadoPaginado<DocumentoListaDto>> Handle(ObtenerDocumentosQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var deTrabajador =
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Trabajador,
                TrabajadorId = (Guid?)documento.TrabajadorId,
                PropietarioId = (Guid?)documento.TrabajadorId,
                PropietarioNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deCliente =
            from documento in documentosContext.Documentos
            where documento.ClienteId != null
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(documento.ClienteId!.Value)
            join cliente in clientesContext.Clientes on documento.ClienteId!.Value equals cliente.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Cliente,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ClienteId,
                PropietarioNombre = cliente.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deEmpresa =
            from documento in documentosContext.Documentos
            where documento.EmpresaId != null
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join empresa in empresasContext.Empresas on documento.EmpresaId!.Value equals empresa.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Empresa,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.EmpresaId,
                PropietarioNombre = empresa.RazonSocial,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deVehiculo =
            from documento in documentosContext.Documentos
            where documento.VehiculoId != null
            where vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(documento.VehiculoId!.Value)
            join vehiculo in vehiculosContext.Vehiculos on documento.VehiculoId!.Value equals vehiculo.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Vehiculo,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.VehiculoId,
                PropietarioNombre = vehiculo.Nombre + " (" + vehiculo.NumeroPlaca + ")",
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var deProyecto =
            from documento in documentosContext.Documentos
            where documento.ProyectoId != null
            join proyecto in proyectosContext.Proyectos on documento.ProyectoId!.Value equals proyecto.Id
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(proyecto.ClienteId)
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                Ambito = AmbitoAplicacion.Proyecto,
                TrabajadorId = (Guid?)null,
                PropietarioId = (Guid?)documento.ProyectoId,
                PropietarioNombre = proyecto.Nombre,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaEmision,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            };

        var consulta = deTrabajador.Concat(deCliente).Concat(deEmpresa).Concat(deVehiculo).Concat(deProyecto);

        if (request.TrabajadorId is not null)
            consulta = consulta.Where(x => x.TrabajadorId == request.TrabajadorId);

        if (request.Ambito is not null)
            consulta = consulta.Where(x => x.Ambito == request.Ambito);

        if (request.PropietarioId is not null)
            consulta = consulta.Where(x => x.PropietarioId == request.PropietarioId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.PropietarioNombre.ToUpper().Contains(busqueda) ||
                x.TipoDocumentoNombre.ToUpper().Contains(busqueda));
        }

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

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
        if (request.Estado is not null)
        {
            // Si alguna vez cambia la calculadora, estas cuatro líneas cambian
            // con ella — DocumentosPaginacionEnSqlTests compara ambas para que
            // no se separen en silencio.
            consulta = request.Estado.Value switch
            {
                EstadoDocumento.NoAplica => consulta.Where(x => x.FechaVencimiento == null),
                EstadoDocumento.Vencido => consulta.Where(x => x.FechaVencimiento != null && x.FechaVencimiento < hoy),
                EstadoDocumento.Urgente => consulta.Where(x => x.FechaVencimiento >= hoy && x.FechaVencimiento <= limiteRojo),
                EstadoDocumento.Proximo => consulta.Where(x => x.FechaVencimiento > limiteRojo && x.FechaVencimiento <= limiteAmbar),
                EstadoDocumento.Vigente => consulta.Where(x => x.FechaVencimiento > limiteAmbar),
                _ => consulta
            };
        }

        if (request.FechaVencimientoHasta is { } hasta)
            consulta = consulta.Where(x => x.FechaVencimiento != null && x.FechaVencimiento >= hoy && x.FechaVencimiento <= hasta);

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables (nombres de propiedad de
        // DocumentoListaDto, que es lo que envía QuickGrid): un OrdenarPor
        // desconocido cae al orden por defecto, nunca se interpola en SQL.
        // "Estado" no está persistido, pero sí lo está FechaVencimiento, así
        // que se ordena por el mismo CASE de umbrales que usa el filtro de
        // arriba — exacto y resuelto en la base de datos. Ascendente deja
        // primero lo que más urge, que es lo que el gestor espera del primer
        // clic en esa cabecera.
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
            (nameof(DocumentoListaDto.Estado), false) => consulta.OrderBy(x =>
                x.FechaVencimiento == null ? 4
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 3),
            (nameof(DocumentoListaDto.Estado), true) => consulta.OrderByDescending(x =>
                x.FechaVencimiento == null ? 4
                : x.FechaVencimiento < hoy ? 0
                : x.FechaVencimiento <= limiteRojo ? 1
                : x.FechaVencimiento <= limiteAmbar ? 2
                : 3),
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
            .Select(x => new DocumentoListaDto(
                x.Id, x.Ambito, x.PropietarioNombre, x.TipoDocumentoNombre, x.FechaEmision, x.FechaVencimiento,
                EstadoDocumento.Vigente, x.ArchivoUrl, new List<AcreditacionResumenDto>()))
            .ToListAsync(cancellationToken);

        // Ahora solo sobre la página, no sobre todo el tenant.
        var documentoIds = pagina.Select(d => d.Id).ToList();
        var acreditacionesPorDocumento = await ObtenerAcreditacionesPorDocumentoAsync(documentoIds, cancellationToken);

        var elementos = pagina
            .Select(d => d with
            {
                Estado = CalculadoraEstadoDocumento.Calcular(
                    d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                Acreditaciones = acreditacionesPorDocumento.GetValueOrDefault(d.Id, [])
            })
            .ToList();

        return new ResultadoPaginado<DocumentoListaDto>(elementos, total, request.Pagina, request.TamanoPagina);
    }

    /// <summary>docs/ux-audit/PLAN-EJECUCION-UX.md § Parte 2 (c) — badges por plataforma. Solo sobre la página ya paginada, nunca sobre todo el tenant.</summary>
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
