using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Facturacion;
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
    string MonedaIso,
    Guid Version);

public class ObtenerTarifasClienteQueryHandler(IEmpresasQueryContext empresasContext, IFacturacionQueryContext facturacionContext)
    : IRequestHandler<ObtenerTarifasClienteQuery, List<TarifaClienteDto>>
{
    public async Task<List<TarifaClienteDto>> Handle(ObtenerTarifasClienteQuery request, CancellationToken cancellationToken)
    {
        // Cargar antes el Cliente (filtrado por tenant) en vez de fiarse solo
        // del ClienteId recibido — mismo criterio que ObtenerResumenFacturacion
        // y la regla de CLAUDE.md: un Id ajeno debe resultar "no encontrado".
        // El filtro global de TarifaCliente ya aísla por tenant; esto lo
        // refuerza en el handler para no depender de una sola capa en datos
        // comercialmente sensibles.
        // F3b — ClienteId ahora repunta contra Empresas.
        var clienteExiste = await empresasContext.Empresas
            .AnyAsync(c => c.Id == request.ClienteId, cancellationToken);

        if (!clienteExiste)
            return [];

        var tarifas = await facturacionContext.TarifasCliente
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
                t.MonedaIso,
                t.Version))
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
