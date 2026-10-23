using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.ContratosAcceso;

namespace CaeManager.Application.Tests.ContratosAcceso;

public class ContratoAccesoRepositorioFalso : IContratoAccesoRepository
{
    public List<ContratoAcceso> Contratos { get; } = [];

    public Task<ContratoAcceso?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Contratos.FirstOrDefault(c => c.Id == id && !c.EstaEliminado));

    public void Agregar(ContratoAcceso contrato) => Contratos.Add(contrato);
}

public class ContratosAccesoQueryContextFalso : IContratosAccesoQueryContext
{
    public List<ContratoAcceso> ListaContratos { get; } = [];

    // El filtro global de soft delete lo pone el DbContext real; el doble lo reproduce para que las consultas se
    // prueben contra lo mismo que verían.
    public IQueryable<ContratoAcceso> ContratosAcceso =>
        new TestAsyncQueryable<ContratoAcceso>(ListaContratos.Where(c => !c.EstaEliminado).AsQueryable());
}

public sealed class PoliticaNotaInternaFalsa(bool puedeLeer) : IPoliticaNotaInterna
{
    public Task<bool> PuedeLeerAsync(CancellationToken cancellationToken = default) => Task.FromResult(puedeLeer);

    public Task<bool> TieneAccesoTotalAlTenantAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
}
