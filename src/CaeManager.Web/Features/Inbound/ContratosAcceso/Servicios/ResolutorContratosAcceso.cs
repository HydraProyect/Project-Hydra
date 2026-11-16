using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Inbound.ContratosAcceso.Queries;
using MediatR;

namespace CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;

/// <summary>
/// Un contrato de acceso con los nombres que su DTO no trae (solo lleva identificadores). Un nombre nulo significa que la
/// Query de origen no lo devolvió al usuario (fuera de su alcance o no disponible): la pantalla lo dice, no lo inventa.
/// </summary>
public sealed record ContratoAccesoFila(
    ContratoAccesoDto Contrato, string? EmpresaNombre, string? EmpresaCif, string? CentroNombre, string? ClienteNombre);

/// <summary>
/// Pone nombre y CIF a la Empresa contratista, y nombre al Centro y al Cliente empresarial, con las Queries que ya existen
/// (<see cref="ObtenerCentrosParaSelectorQuery"/> y <see cref="ObtenerEmpresaPorIdQuery"/>) y con su autorización. No hay una
/// lectura por lotes de Empresas contratistas ni el DTO del contrato lleva los nombres: la Empresa se pide una a una (con
/// caché por circuito, solo de lo encontrado) y es una limitación declarada, no un diseño.
/// </summary>
public sealed class ResolutorContratosAcceso(IMediator mediator)
{
    private readonly Dictionary<Guid, EmpresaDetalleDto> _empresas = [];

    public async Task<IReadOnlyList<ContratoAccesoFila>> ResolverAsync(
        IReadOnlyList<ContratoAccesoDto> contratos, CancellationToken cancellationToken = default)
    {
        if (contratos.Count == 0)
            return [];

        var centros = new Dictionary<Guid, CentroSelectorDto>();
        foreach (var centro in await mediator.Send(new ObtenerCentrosParaSelectorQuery(), cancellationToken))
            centros.TryAdd(centro.Id, centro);

        // Una a una y sin paralelismo: las Queries comparten el contexto de datos del ámbito.
        foreach (var empresaId in contratos.Select(c => c.EmpresaContratistaId).Distinct())
        {
            if (_empresas.ContainsKey(empresaId))
                continue;
            var empresa = await mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId), cancellationToken);
            if (empresa is not null)
                _empresas[empresaId] = empresa;
        }

        return contratos.Select(contrato =>
        {
            _empresas.TryGetValue(contrato.EmpresaContratistaId, out var empresa);
            centros.TryGetValue(contrato.CentroId, out var centro);
            return new ContratoAccesoFila(contrato, empresa?.RazonSocial, empresa?.Cif, centro?.Nombre, centro?.ClienteRazonSocial);
        }).ToList();
    }
}
