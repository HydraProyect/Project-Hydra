using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.EstadoMaterializado.Frescura;
using CaeManager.Web.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

internal sealed class CircuitoFrescuraDePrueba : ICurrentUserService, ITenantActual, ILectorVersionEstadoTenant
{
    public static readonly TimeSpan EsperaNegativa = RepintadoPorCambioDeEstado.Amortiguacion * 4;
    public static readonly TimeSpan EsperaPositiva = TimeSpan.FromSeconds(5);
    public Guid? TenantId { get; } = Guid.NewGuid();
    public NotificadorEstadoTenant Notificador { get; } = new();
    public CacheEstadoCircuito Cache { get; }

    private CircuitoFrescuraDePrueba() => Cache = new(this, this, this, Notificador);

    public static async Task<CircuitoFrescuraDePrueba> RegistrarAsync(BunitContext contexto)
    {
        var circuito = new CircuitoFrescuraDePrueba();
        contexto.Services.AddScoped(_ => circuito.Cache);
        // Simula la lectura previa de un lector del circuito: los avisos de este Tenant llegan a la caché.
        await circuito.Cache.ObtenerAsync(new ConsultaEstadoEnCache<Guid>(
            "prueba", f => [f], _ => Task.FromResult<IReadOnlyList<Guid>>([]),
            (_, _) => Task.FromResult<IReadOnlyList<Guid>>([])), CancellationToken.None);
        return circuito;
    }

    public void Publicar(long version) => Notificador.Publicar(new AvisoCambioEstado(TenantId!.Value, version, []));
    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
    public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
    public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantId);
    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    public Task<VersionEstadoLeida?> LeerAsync(CancellationToken cancellationToken) =>
        Task.FromResult<VersionEstadoLeida?>(new VersionEstadoLeida(TenantId!.Value, 1));
}
