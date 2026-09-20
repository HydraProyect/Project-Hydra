using CaeManager.Application.Common;
using CaeManager.Web.Components.EstadoPersistido;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Sesión fija para los tests de componentes que montan una pantalla con
/// <c>EstadoDePantallaPersistido</c>. Con <see cref="Usuario"/> nulo la
/// huella de sesión no se resuelve y el patrón queda inerte (no guarda ni
/// recoge nada): es lo que necesitan los tests de comportamiento de la lista,
/// que no tratan de esto.
/// </summary>
public sealed class SesionDePruebaParaEstadoPersistido : ICurrentUserService, ITenantActual, IClienteActivoSeleccionado
{
    public static readonly Guid UsuarioFijo = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    public static readonly Guid TenantFijo = Guid.Parse("00000000-0000-0000-0000-0000000000b1");

    public Guid? Usuario { get; init; }
    public Guid? Tenant { get; init; }

    public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(Usuario);
    public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("GestorCae");
    public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(Tenant);
    public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    public Guid? TenantId => Tenant;
    public Guid? TenantIdSeleccionado => Tenant;
    public Guid? AsignacionOperacionIdSeleccionada => null;
    public Guid? SesionPrivilegiadaIdSeleccionada => null;
}

public static class EstadoPersistidoParaPruebas
{
    /// <summary>
    /// Como la sobrecarga de <see cref="IServiceCollection"/>, y además declara
    /// el modo de renderizado del contexto: las pantallas con estado
    /// persistido consultan <c>RendererInfo.IsInteractive</c> (el prerender
    /// persiste; el circuito interactivo no) y bUnit lanza si no se ha
    /// especificado. Por defecto, un circuito interactivo: es donde ocurren en
    /// producción los clics, la escritura y los atajos que ejercitan los
    /// tests de comportamiento de la lista.
    /// </summary>
    public static ComponentStatePersistenceManager AddEstadoDePantallaPersistidoParaPruebas(
        this Bunit.BunitContext contexto, bool interactivo = true, bool conSesion = false,
        Guid? tenant = null, Guid? usuario = null)
    {
        // Primero los servicios: acceder al Renderer inicializa el proveedor
        // y bUnit ya no admite registros después.
        var gestor = contexto.Services.AddEstadoDePantallaPersistidoParaPruebas(conSesion, tenant, usuario);
        // Como en producción: el prerender es ("Static", false); el circuito, ("Server", true).
        contexto.Renderer.SetRendererInfo(
            new Microsoft.AspNetCore.Components.RendererInfo(interactivo ? "Server" : "Static", interactivo));
        return gestor;
    }

    /// <summary>
    /// Registra lo que una pantalla con estado persistido inyecta. Devuelve el
    /// gestor de persistencia de este ámbito, para que un test pueda persistir
    /// y restaurar de verdad entre dos montajes.
    /// </summary>
    public static ComponentStatePersistenceManager AddEstadoDePantallaPersistidoParaPruebas(
        this IServiceCollection services, bool conSesion = false, Guid? tenant = null, Guid? usuario = null)
    {
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        var sesion = new SesionDePruebaParaEstadoPersistido
        {
            Usuario = conSesion ? usuario ?? SesionDePruebaParaEstadoPersistido.UsuarioFijo : null,
            Tenant = conSesion ? tenant ?? SesionDePruebaParaEstadoPersistido.TenantFijo : null,
        };

        services.AddScoped(_ => gestor.State);
        services.AddScoped(_ => new HuellaDeSesion(sesion, sesion, sesion));
        services.AddScoped(sp => new FabricaEstadoDePantallaPersistido(
            sp.GetRequiredService<Microsoft.AspNetCore.Components.PersistentComponentState>(),
            sp.GetRequiredService<HuellaDeSesion>(),
            TimeProvider.System));
        return gestor;
    }
}
