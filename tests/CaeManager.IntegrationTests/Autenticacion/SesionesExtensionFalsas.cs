using CaeManager.Infrastructure.Identity;

namespace CaeManager.IntegrationTests.Autenticacion;

internal sealed class SesionesExtensionFalsas : IRegistroSesionesUsuario
{
    public Task<bool> EstaVigenteAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) => Task.FromResult(true);
    public Task RegistrarAsync(Guid usuarioId, Guid tenantPropietarioId, Guid sid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<bool> RevocarAsync(Guid usuarioId, Guid sid, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<int> CerrarTodasAsync(Guid usuarioId, CancellationToken ct = default) => throw new NotImplementedException();
}
