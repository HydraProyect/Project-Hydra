using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Tests.Documentos;

public class SelloEmpresaRepositorioFalso : ISelloEmpresaRepository
{
    public List<SelloEmpresa> Sellos { get; } = [];

    public void Agregar(SelloEmpresa sello) => Sellos.Add(sello);

    public Task<SelloEmpresa?> ObtenerPorEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Sellos.FirstOrDefault(s => s.EmpresaId == empresaId));
}
