using CaeManager.Application.Common;
using CaeManager.Domain.Facturacion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;

public record ObtenerTarifasClienteQuery(Guid ClienteId) : IRequest<List<TarifaClienteDto>>;

public record TarifaClienteDto(
    Guid Id,
    Guid ClienteId,
    ConceptoFacturable Concepto,
    string ConceptoNombre,
    decimal PrecioUnitario,
    string MonedaIso);

public class ObtenerTarifasClienteQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerTarifasClienteQuery, List<TarifaClienteDto>>
{
    public async Task<List<TarifaClienteDto>> Handle(ObtenerTarifasClienteQuery request, CancellationToken cancellationToken)
    {
        var tarifas = await dbContext.TarifasCliente
            .Where(t => t.ClienteId == request.ClienteId)
            .OrderBy(t => t.Concepto)
            .ToListAsync(cancellationToken);

        return tarifas
            .Select(t => new TarifaClienteDto(
                t.Id,
                t.ClienteId,
                t.Concepto,
                NombreConcepto(t.Concepto),
                t.PrecioUnitario,
                t.MonedaIso))
            .ToList();
    }

    internal static string NombreConcepto(ConceptoFacturable concepto) => concepto switch
    {
        ConceptoFacturable.TrabajadorActivo => "Trabajador activo",
        ConceptoFacturable.AltaCentro => "Alta de centro",
        ConceptoFacturable.VisitaTrabajadorExtranjero => "Visita de trabajador extranjero",
        ConceptoFacturable.DocumentoGestionado => "Documento gestionado",
        ConceptoFacturable.TecnicoAsignadoProyecto => "Técnico asignado a proyecto",
        ConceptoFacturable.GestionProyectoRealizada => "Gestión de proyecto realizada",
        ConceptoFacturable.DiaProyectoAbierto => "Día de proyecto abierto",
        _ => concepto.ToString()
    };
}
