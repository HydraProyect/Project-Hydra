using CaeManager.Application.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>Fake en memoria — sin candidatos salvo que el test registre explícitamente una plataforma.</summary>
public class ResolucionProveedorPlataformaCaeServiceFalso : IResolucionProveedorPlataformaCaeService
{
    private readonly List<ProveedorPlataformaCaeCandidatoDto> _candidatosPorDominioCorreo = [];

    public void RegistrarPlataformaPorDominioCorreo(ProveedorPlataformaCaeCandidatoDto candidato) =>
        _candidatosPorDominioCorreo.Add(candidato);

    public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorUrlAsync(
        string url, CancellationToken cancellationToken = default) =>
        Task.FromResult((IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>)[]);

    public Task<IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>> ResolverPorDominioCorreoAsync(
        string email, CancellationToken cancellationToken = default) =>
        Task.FromResult((IReadOnlyList<ProveedorPlataformaCaeCandidatoDto>)_candidatosPorDominioCorreo);
}
