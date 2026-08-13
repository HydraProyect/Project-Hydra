using CaeManager.Application.Common;
using CaeManager.Domain.Contactos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;

/// <summary>
/// Propietario de una agenda de contactos. Se pasa como par (tipo, id) en vez
/// de cuatro Guid? opcionales para que sea imposible pedir "la agenda de
/// nadie" o "la de dos dueños a la vez" desde el llamador.
/// </summary>
public enum TipoPropietarioAgenda
{
    Cliente,
    Empresa,
    Subcontrata,
    Centro
}

public record ObtenerAgendaContactosQuery(TipoPropietarioAgenda Tipo, Guid PropietarioId)
    : IRequest<IReadOnlyList<ContactoAgendaDto>>;

public record ContactoAgendaDto(
    Guid Id,
    string Nombre,
    string Email,
    string? Telefono,
    string? Cargo,
    string? Notas,
    bool EsPredeterminado,
    bool RecibeProgramacionVisitas,
    bool RecibeFacturacion,
    IReadOnlyList<Guid> TipoDocumentoIds,
    IReadOnlyList<string> TipoDocumentoNombres);

public class ObtenerAgendaContactosQueryHandler(
    IContactosAgendaQueryContext contactosContext,
    TiposDocumento.ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAgendaContactosQuery, IReadOnlyList<ContactoAgendaDto>>
{
    public async Task<IReadOnlyList<ContactoAgendaDto>> Handle(
        ObtenerAgendaContactosQuery request, CancellationToken cancellationToken)
    {
        // Alcance por el propietario: la agenda de un Cliente fuera de cartera
        // no se lee ni conociendo su Guid, igual que el resto de consultas
        // *PorId* (ver AlcanceDatosServiceExtensions).
        var visible = request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => await alcanceDatos.ClienteVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Empresa => await alcanceDatos.EmpresaVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Subcontrata => await alcanceDatos.SubcontrataVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Centro => await alcanceDatos.CentroVisibleAsync(request.PropietarioId, cancellationToken),
            _ => false
        };

        if (!visible) return [];

        var contactos = await FiltrarPorPropietario(contactosContext.ContactosAgenda, request)
            .OrderByDescending(c => c.EsPredeterminado)
            .ThenBy(c => c.Nombre)
            .Select(c => new
            {
                c.Id,
                c.Nombre,
                c.Email,
                c.Telefono,
                c.Cargo,
                c.Notas,
                c.EsPredeterminado,
                c.RecibeProgramacionVisitas,
                c.RecibeFacturacion
            })
            .ToListAsync(cancellationToken);

        if (contactos.Count == 0) return [];

        var contactoIds = contactos.Select(c => c.Id).ToList();

        var vinculos = await (
            from vinculo in contactosContext.ContactosAgendaTiposDocumento
            where contactoIds.Contains(vinculo.ContactoAgendaId)
            join tipo in tiposDocumentoContext.TiposDocumento on vinculo.TipoDocumentoId equals tipo.Id
            select new { vinculo.ContactoAgendaId, vinculo.TipoDocumentoId, tipo.Nombre })
            .ToListAsync(cancellationToken);

        var vinculosPorContacto = vinculos.GroupBy(v => v.ContactoAgendaId).ToDictionary(g => g.Key, g => g.ToList());

        return contactos.Select(c =>
        {
            var suyos = vinculosPorContacto.GetValueOrDefault(c.Id, []);
            return new ContactoAgendaDto(
                c.Id, c.Nombre, c.Email, c.Telefono, c.Cargo, c.Notas,
                c.EsPredeterminado, c.RecibeProgramacionVisitas, c.RecibeFacturacion,
                suyos.Select(v => v.TipoDocumentoId).ToList(),
                suyos.OrderBy(v => v.Nombre).Select(v => v.Nombre).ToList());
        }).ToList();
    }

    internal static IQueryable<ContactoAgenda> FiltrarPorPropietario(
        IQueryable<ContactoAgenda> consulta, ObtenerAgendaContactosQuery request) => request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => consulta.Where(c => c.ClienteId == request.PropietarioId),
            TipoPropietarioAgenda.Empresa => consulta.Where(c => c.EmpresaId == request.PropietarioId),
            TipoPropietarioAgenda.Subcontrata => consulta.Where(c => c.SubcontrataId == request.PropietarioId),
            TipoPropietarioAgenda.Centro => consulta.Where(c => c.CentroId == request.PropietarioId),
            _ => consulta.Where(_ => false)
        };
}
