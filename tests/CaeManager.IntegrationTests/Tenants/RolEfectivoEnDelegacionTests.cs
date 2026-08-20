using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Hallazgo N-5 de INFORME-AUDITORIA-2.md: <c>AsignacionOperadorDelegado.Rol</c>
/// se validaba, se persistía y no se leía jamás. La puerta de escritura
/// decidía con el rol del claim —el del tenant de origen—, así que un operador
/// asignado como Consulta sobre el tenant B pero Administrador en el suyo
/// escribía en B, justo lo contrario de lo que promete ADR-004 § 5.3.
/// </summary>
public class RolEfectivoEnDelegacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _consultora = Guid.NewGuid();
    private readonly Guid _clienteDelegante = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var delegacion = new DelegacionTenant(_consultora, _clienteDelegante);
        contexto.DelegacionesTenant.Add(delegacion);
        // Administrador en su casa, mero Consulta sobre el cliente delegante.
        contexto.AsignacionesOperadorDelegado.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _usuario, "Consulta"));

        await contexto.SaveChangesAsync();
        _delegacionId = delegacion.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Fuera_de_un_workspace_delegado_manda_el_rol_del_claim()
    {
        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: null);

        (await servicio.ObtenerRolActualAsync()).Should().Be("Administrador");
    }

    [Fact]
    public async Task Dentro_del_workspace_delegado_manda_el_rol_de_la_asignacion()
    {
        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: _clienteDelegante);

        (await servicio.ObtenerRolActualAsync()).Should().Be(
            "Consulta", "ser Administrador en la consultora no da privilegios sobre el cliente");
    }

    [Fact]
    public async Task Con_la_delegacion_revocada_no_queda_ningun_rol()
    {
        // El token de selección vive hasta 12 h, así que este caso ocurre de
        // verdad entre la revocación y su caducidad: tiene que fallar cerrado.
        await using (var contextoRevocacion = CrearContexto())
        {
            var delegacion = await contextoRevocacion.DelegacionesTenant.FirstAsync(d => d.Id == _delegacionId);
            delegacion.Desactivar();
            await contextoRevocacion.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: _clienteDelegante);

        (await servicio.ObtenerRolActualAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Un_usuario_sin_asignacion_en_ese_cliente_no_obtiene_rol()
    {
        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: _clienteDelegante, usuarioId: Guid.NewGuid());

        (await servicio.ObtenerRolActualAsync()).Should().BeNull();
    }

    private CurrentUserService CrearServicio(
        ITenantsQueryContext contexto, Guid? tenantSeleccionado, Guid? usuarioId = null)
    {
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, (usuarioId ?? _usuario).ToString()),
                new Claim(ClaimTypes.Role, "Administrador")
            ],
            "prueba");

        // El contexto se resuelve del contenedor, no por constructor — ver
        // CurrentUserService: por constructor cerraría un ciclo de DI con
        // AuditoriaInterceptor.
        var servicios = new ServiceCollection();
        servicios.AddSingleton(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorFalso(),
            new ClienteActivoSeleccionadoFalso(tenantSeleccionado),
            servicios.BuildServiceProvider());
    }

    private CaeManagerDbContext CrearContexto()
    {
        // Sellado contra el cliente delegante: es el tenant que se estaría
        // operando. DelegacionTenant/AsignacionOperadorDelegado son catálogo
        // global sin filtro, así que se leen igual desde cualquiera.
        var tenantActual = new TenantActualAmbiental { TenantId = _clienteDelegante };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? asignacionOperacionId = null)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        // null = vía heredada, que es la que estos tests ejercitan: el rol
        // efectivo resuelto contra DelegacionTenant/AsignacionOperadorDelegado.
        public Guid? AsignacionOperacionIdSeleccionada => asignacionOperacionId;
    }

    private sealed class AuthenticationStateProviderFalso(ClaimsPrincipal usuario) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(usuario));
    }

    private sealed class HttpContextAccessorFalso : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => null;
            set => throw new NotSupportedException();
        }
    }
}
