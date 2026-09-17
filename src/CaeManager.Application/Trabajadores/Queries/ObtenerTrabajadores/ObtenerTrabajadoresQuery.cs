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
/// cae en <see cref="EstadoDocumento.SinCaducidad"/>, igual que uno con
/// Documentos pero sin ninguna fecha de vencimiento — el <c>null</c> del tipo
/// es un artefacto de <see cref="TrabajadorListaDto"/> siendo compartido con
/// otros listados, no un valor que este handler produzca.
/// </summary>
public record ObtenerTrabajadoresQuery(
    string? Busqueda, Guid? EmpresaId = null, Guid? SubcontrataId = null, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false, string? EstadoDocumental = null)
    : IRequest<ResultadoPaginado<TrabajadorListaDto>>;

public record TrabajadorListaDto(
    Guid Id, string Nombre, string Apellidos, string? Dni, string EmpleadorNombre,
    EstadoDocumento? EstadoDocumental = null);

public class ObtenerTrabajadoresQueryHandler(
    IEmpresasQueryContext empresasContext, ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext, IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos, ICalculoEstadoDocumentalService calculoEstadoDocumental)
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
        // un trabajador ya existente" — se acota a los que tienen una
        // Asignación activa en un Centro visible (ver IAlcanceDatosService).
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            consulta = consulta.Where(x => trabajadorIdsVisibles.Contains(x.trabajador.Id));

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
        var necesitaEstadoCompleto = !string.IsNullOrWhiteSpace(request.EstadoDocumental) || ordenaPorEstado;

        if (necesitaEstadoCompleto)
        {
            if (request.EstadoDocumental == EstadoDocumentalFiltro.SinDocumentos)
            {
                // Ningún Trabajador de este listado llega a EstadoDocumental
                // null (CalculoEstadoDocumentalService.GetValueOrDefault cae a
                // SinCaducidad, nunca a null) — "sin documentos" nunca
                // coincide, igual que EstadoDocumentalFiltro.Coincide.
                return new ResultadoPaginado<TrabajadorListaDto>([], 0, request.Pagina, request.TamanoPagina);
            }

            var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
            var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
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
                    PeorFecha = documentosContext.Documentos
                        .Where(d => d.TrabajadorId == x.trabajador.Id)
                        .Min(d => (DateOnly?)d.FechaVencimiento)
                };

            if (!string.IsNullOrWhiteSpace(request.EstadoDocumental))
            {
                // Si no parsea, no se filtra — igual que EstadoDocumentalFiltro.Coincide
                // (su `: true` final). Pero si parsea a un valor que este
                // listado nunca produce (p. ej. Faltante, que solo emiten las
                // Alertas), el resultado tiene que ser "ninguna fila" — no
                // "sin filtro". Codex (revisión previa a esta PR) encontró que
                // el primer intento colapsaba los dos casos en el mismo `_`,
                // así que un filtro válido pero no aplicable devolvía TODO en
                // vez de nada.
                if (Enum.TryParse<EstadoDocumento>(request.EstadoDocumental, out var estadoFiltro))
                {
                    conFecha = estadoFiltro switch
                    {
                        EstadoDocumento.SinCaducidad => conFecha.Where(x => x.PeorFecha == null),
                        EstadoDocumento.Vencido => conFecha.Where(x => x.PeorFecha != null && x.PeorFecha < hoy),
                        EstadoDocumento.Urgente => conFecha.Where(x => x.PeorFecha != null && x.PeorFecha >= hoy && x.PeorFecha <= limiteRojo),
                        EstadoDocumento.Proximo => conFecha.Where(x => x.PeorFecha != null && x.PeorFecha > limiteRojo && x.PeorFecha <= limiteAmbar),
                        EstadoDocumento.Vigente => conFecha.Where(x => x.PeorFecha != null && x.PeorFecha > limiteAmbar),
                        _ => conFecha.Where(x => false)
                    };
                }
            }

            var totalConEstado = await conFecha.CountAsync(cancellationToken);

            // Ternario anidado en línea a propósito, no un método aparte: un
            // método de C# dentro de un OrderBy no se traduce a SQL — tiene
            // que ser una expresión que EF pueda leer, igual que
            // ObtenerDocumentosQuery.
            var ordenadaConEstado = ordenaPorEstado
                ? (request.Descendente
                    ? conFecha.OrderByDescending(x =>
                        x.PeorFecha == null ? 4
                        : x.PeorFecha < hoy ? 0
                        : x.PeorFecha <= limiteRojo ? 1
                        : x.PeorFecha <= limiteAmbar ? 2
                        : 3)
                    : conFecha.OrderBy(x =>
                        x.PeorFecha == null ? 4
                        : x.PeorFecha < hoy ? 0
                        : x.PeorFecha <= limiteRojo ? 1
                        : x.PeorFecha <= limiteAmbar ? 2
                        : 3))
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
                paginaConEstado.Select(x => new TrabajadorListaDto(
                    x.Id, x.Nombre, x.Apellidos, x.Dni, x.EmpleadorNombre,
                    CalculadoraEstadoDocumento.Calcular(x.PeorFecha, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
                    .ToList(),
                totalConEstado,
                request.Pagina,
                request.TamanoPagina);
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
            elementos.Select(t => t with { EstadoDocumental = estados.GetValueOrDefault(t.Id) }).ToList(),
            total, request.Pagina, request.TamanoPagina);
    }
}
