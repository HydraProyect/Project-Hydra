using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;

/// <summary>
/// <paramref name="EstadoDocumental"/> es el filtro de estado de la pantalla:
/// un Vehículo no tiene estado propio, se deriva del peor estado de vigencia
/// de sus Documentos (ver <see cref="ICalculoEstadoDocumentalService"/>).
/// </summary>
public record ObtenerVehiculosQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null,
    bool ConRecuentosPorEstado = false, Guid? VehiculoId = null)
    : IRequest<ResultadoPaginado<VehiculoListaDto>>;

public record VehiculoListaDto(
    Guid Id, string Nombre, string Modelo, string NumeroPlaca, string EmpleadorNombre,
    EstadoDocumento? EstadoDocumental = null);

public class ObtenerVehiculosQueryHandler(
    IEmpresasQueryContext empresasContext,
    IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos,
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    ICalculoEstadoDocumentalService calculoEstadoDocumental)
    : IRequestHandler<ObtenerVehiculosQuery, ResultadoPaginado<VehiculoListaDto>>
{
    public async Task<ResultadoPaginado<VehiculoListaDto>> Handle(
        ObtenerVehiculosQuery request, CancellationToken cancellationToken)
    {
        var consulta =
            from vehiculo in vehiculosContext.Vehiculos
            join empresa in empresasContext.Empresas on vehiculo.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in empresasContext.Empresas on vehiculo.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            select new { vehiculo, EmpleadorNombre = empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial };

        // Este es el listado (tabla /vehiculos), no el selector — se acota a
        // los de una Empresa/Subcontrata visible (ver IAlcanceDatosService).
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);
        if (vehiculoIdsVisibles is not null)
            consulta = consulta.Where(x => vehiculoIdsVisibles.Contains(x.vehiculo.Id));

        // Una sola fila, para sustituirla en sitio en el listado tras editarla en la vista rápida.
        // Va después del alcance: solo estrecha.
        if (request.VehiculoId is not null)
            consulta = consulta.Where(x => x.vehiculo.Id == request.VehiculoId);

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(x =>
                x.vehiculo.Nombre.ToUpper().Contains(busqueda) ||
                x.vehiculo.Modelo.ToUpper().Contains(busqueda) ||
                x.vehiculo.NumeroPlaca.ToUpper().Contains(busqueda));
        }

        if (request.EmpresaId is not null)
            consulta = consulta.Where(x => x.vehiculo.EmpresaId == request.EmpresaId);

        if (request.SubcontrataId is not null)
            consulta = consulta.Where(x => x.vehiculo.SubcontrataId == request.SubcontrataId);

        // Filtrar u ordenar por el estado documental obliga a conocer, de cada
        // Vehículo visible, el peor vencimiento de sus Documentos — pero ese
        // agregado (MIN por propietario) se pide a SQL con una subconsulta
        // correlacionada en vez de materializar todos los Vehículos visibles
        // (hallazgo Módulo 8, PR #389 § 4.1), igual que ObtenerTrabajadoresQuery.
        var necesitaEstadoCompleto =
            !string.IsNullOrWhiteSpace(request.EstadoDocumental) ||
            string.Equals(request.OrdenarPor, nameof(VehiculoListaDto.EstadoDocumental), StringComparison.Ordinal) ||
            request.ConRecuentosPorEstado;

        if (necesitaEstadoCompleto)
        {
            var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
            var hoy = DiaDeNegocio.Hoy();
            var limiteRojo = hoy.AddDays(parametros.UmbralRojoDias);
            var limiteAmbar = hoy.AddDays(parametros.UmbralAmbarDias);

            var conFecha =
                from x in consulta
                select new
                {
                    x.vehiculo.Id,
                    x.vehiculo.Nombre,
                    x.vehiculo.Modelo,
                    x.vehiculo.NumeroPlaca,
                    x.EmpleadorNombre,
                    PeorFecha = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Where(d => d.VehiculoId == x.vehiculo.Id)
                        .Min(d => (DateOnly?)d.FechaVencimiento),
                    // MIN ignora las fechas nulas, que son a la vez «no caduca» y «sin
                    // confirmar»: lo sin confirmar se cuenta aparte para no perderlo.
                    HaySinConfirmar = documentosContext.Documentos.Where(DocumentoOperativo.Expresion)
                        .Any(d => d.VehiculoId == x.vehiculo.Id && d.EstadoVigencia == EstadoVigenciaDocumento.SinConfirmar)
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

            // Este camino se toma también solo para contar por estado (la franja lo pide siempre), así que el
            // orden no puede darse por supuesto: por estado si es la columna pedida, y si no por la misma lista
            // blanca que el camino sin estado.
            var porEstado = request.Descendente
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
                    : 5);
            var ordenaPorEstado = string.Equals(
                request.OrdenarPor, nameof(VehiculoListaDto.EstadoDocumental), StringComparison.Ordinal);
            var ordenadaConEstado = ordenaPorEstado
                ? porEstado.ThenBy(x => x.Nombre)
                : (request.OrdenarPor, request.Descendente) switch
                {
                    (nameof(VehiculoListaDto.Nombre), true) => conFecha.OrderByDescending(x => x.Nombre),
                    (nameof(VehiculoListaDto.Modelo), false) => conFecha.OrderBy(x => x.Modelo).ThenBy(x => x.Nombre),
                    (nameof(VehiculoListaDto.Modelo), true) => conFecha.OrderByDescending(x => x.Modelo).ThenBy(x => x.Nombre),
                    (nameof(VehiculoListaDto.NumeroPlaca), false) => conFecha.OrderBy(x => x.NumeroPlaca),
                    (nameof(VehiculoListaDto.NumeroPlaca), true) => conFecha.OrderByDescending(x => x.NumeroPlaca),
                    (nameof(VehiculoListaDto.EmpleadorNombre), false) => conFecha.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.Nombre),
                    (nameof(VehiculoListaDto.EmpleadorNombre), true) => conFecha.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.Nombre),
                    _ => conFecha.OrderBy(x => x.Nombre)
                };
            var ordenadaFinal = ordenadaConEstado.ThenBy(x => x.Id);

            var paginaConEstado = await ordenadaFinal
                .Skip((request.Pagina - 1) * request.TamanoPagina)
                .Take(request.TamanoPagina)
                .ToListAsync(cancellationToken);

            return new ResultadoPaginado<VehiculoListaDto>(
                paginaConEstado.Select(x => new VehiculoListaDto(
                    x.Id, x.Nombre, x.Modelo, x.NumeroPlaca, x.EmpleadorNombre,
                    CalculoEstadoDocumentalService.PeorEstado(x.PeorFecha, x.HaySinConfirmar, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
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
            (nameof(VehiculoListaDto.Nombre), true) => consulta.OrderByDescending(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.Modelo), false) => consulta.OrderBy(x => x.vehiculo.Modelo).ThenBy(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.Modelo), true) => consulta.OrderByDescending(x => x.vehiculo.Modelo).ThenBy(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.NumeroPlaca), false) => consulta.OrderBy(x => x.vehiculo.NumeroPlaca),
            (nameof(VehiculoListaDto.NumeroPlaca), true) => consulta.OrderByDescending(x => x.vehiculo.NumeroPlaca),
            (nameof(VehiculoListaDto.EmpleadorNombre), false) => consulta.OrderBy(x => x.EmpleadorNombre).ThenBy(x => x.vehiculo.Nombre),
            (nameof(VehiculoListaDto.EmpleadorNombre), true) => consulta.OrderByDescending(x => x.EmpleadorNombre).ThenBy(x => x.vehiculo.Nombre),
            _ => consulta.OrderBy(x => x.vehiculo.Nombre)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(x => x.vehiculo.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(x => new VehiculoListaDto(x.vehiculo.Id, x.vehiculo.Nombre, x.vehiculo.Modelo, x.vehiculo.NumeroPlaca, x.EmpleadorNombre))
            .ToListAsync(cancellationToken);

        // Solo para los de la página: el badge de la tabla, no un filtro.
        var estados = await calculoEstadoDocumental.CalcularPeorEstadoAsync(
            AmbitoAplicacion.Vehiculo, elementos.Select(v => v.Id).ToList(), cancellationToken);

        return new ResultadoPaginado<VehiculoListaDto>(
            elementos.Select(v => v with { EstadoDocumental = estados.GetValueOrDefault(v.Id) }).ToList(),
            total, request.Pagina, request.TamanoPagina);
    }
}
