using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.CatalogoBase;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Requisitos.Queries;

/// <summary>
/// «¿Es alguno de estos?» antes de crear un requisito propio (DECISION_LOG 2026-11-14): los tipos de documento del Tenant
/// TCE, por su nombre y sus alias (D-28), y los requisitos activos del catálogo base que se parecen a <see cref="Texto"/>.
/// Los mejores primero, como mucho <see cref="Maximo"/>. Solo sugiere (<see cref="SimilitudNombresRequisito"/>).
/// </summary>
/// <param name="AmbitoAplicacion">Si se indica, solo tipos de documento de ese ámbito.</param>
[AreaDelEncargo("TiposDocumento")]
public record BuscarRequisitosParecidosQuery(string Texto, AmbitoAplicacion? AmbitoAplicacion = null)
    : IRequest<IReadOnlyList<RequisitoParecidoDto>>
{
    public const int Maximo = 10;
    public const int LongitudMaximaTexto = 150;
}

public enum OrigenSugerencia
{
    TipoDocumentoDelTenant = 1,
    CatalogoBase = 2,
}

/// <param name="CoincideCon">El nombre o el alias que más se parece.</param>
public record RequisitoParecidoDto(OrigenSugerencia Origen, Guid Id, string Nombre, string CoincideCon, double Puntuacion);

public class BuscarRequisitosParecidosQueryValidator : AbstractValidator<BuscarRequisitosParecidosQuery>
{
    public BuscarRequisitosParecidosQueryValidator()
    {
        RuleFor(q => q.Texto).NotEmpty().MaximumLength(BuscarRequisitosParecidosQuery.LongitudMaximaTexto);
        RuleFor(q => q.AmbitoAplicacion).IsInEnum().When(q => q.AmbitoAplicacion is not null);
    }
}

public class BuscarRequisitosParecidosQueryHandler(
    ITiposDocumentoQueryContext tipos, ICatalogoBaseQueryContext catalogo, IAlcanceDatosService alcanceDatos, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<BuscarRequisitosParecidosQuery, IReadOnlyList<RequisitoParecidoDto>>
{
    public async Task<IReadOnlyList<RequisitoParecidoDto>> Handle(BuscarRequisitosParecidosQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return [];

        // El catálogo de tipos de documento es del Tenant entero, pero el usuario de portal no configura requisitos.
        if (!await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return [];

        // shortcut: compara en memoria todos los tipos no archivados del Tenant (cientos, no miles); cuando un Tenant
        // pase de unos miles de tipos y alias, mover el primer filtro a PostgreSQL (pg_trgm o unaccent + ILIKE).
        var consultaTipos = tipos.TiposDocumento.Where(t => !t.Archivado);
        if (request.AmbitoAplicacion is { } ambito)
            consultaTipos = consultaTipos.Where(t => t.AmbitoAplicacion == ambito);
        var candidatosTipo = await consultaTipos.Select(t => new { t.Id, t.Nombre }).ToListAsync(cancellationToken);

        var idsTipo = candidatosTipo.Select(t => t.Id).ToList();
        var alias = (await tipos.TiposDocumentoAlias
                .Where(a => idsTipo.Contains(a.TipoDocumentoId))
                .Select(a => new { a.TipoDocumentoId, a.Texto })
                .ToListAsync(cancellationToken))
            .ToLookup(a => a.TipoDocumentoId, a => a.Texto);

        var candidatosBase = await catalogo.CatalogoRequisitosBase
            .Where(r => r.Activo)
            .Select(r => new { r.Id, r.Nombre })
            .ToListAsync(cancellationToken);

        var sugerencias = candidatosTipo
            .Select(t => Puntuar(request.Texto, OrigenSugerencia.TipoDocumentoDelTenant, t.Id, t.Nombre, alias[t.Id]))
            .Concat(candidatosBase.Select(r => Puntuar(request.Texto, OrigenSugerencia.CatalogoBase, r.Id, r.Nombre, [])));

        return sugerencias
            .Where(s => s.Puntuacion >= SimilitudNombresRequisito.UmbralSugerencia)
            .OrderByDescending(s => s.Puntuacion).ThenBy(s => s.Nombre, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.Id)
            .Take(BuscarRequisitosParecidosQuery.Maximo)
            .ToList();
    }

    private static RequisitoParecidoDto Puntuar(string texto, OrigenSugerencia origen, Guid id, string nombre, IEnumerable<string> alias)
    {
        var mejor = alias.Prepend(nombre)
            .Select(c => (Texto: c, Puntuacion: SimilitudNombresRequisito.Puntuar(texto, c)))
            .OrderByDescending(c => c.Puntuacion)
            .First();
        return new RequisitoParecidoDto(origen, id, nombre, mejor.Texto, mejor.Puntuacion);
    }
}
