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
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
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

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be("Administrador");
    }

    [Fact]
    public async Task Dentro_del_workspace_delegado_manda_el_rol_de_la_asignacion()
    {
        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: _clienteDelegante);

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be(
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

        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Un_usuario_sin_asignacion_en_ese_cliente_no_obtiene_rol()
    {
        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: _clienteDelegante, usuarioId: Guid.NewGuid());

        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
    }

    /// <summary>
    /// Decisión del propietario, 2026-09-23: una delegación solo da roles de
    /// Operación. Una fila heredada con Administrador o Dirección CAE —anterior
    /// a la decisión, o sembrada sin pasar por el validador— no concede ese rol
    /// en el Tenant propietario: falla cerrado.
    /// </summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Una_asignacion_heredada_con_rol_de_Propiedad_no_da_rol_en_el_delegante(string rol)
    {
        var usuario = Guid.NewGuid();
        await using (var preparacion = CrearContexto())
        {
            preparacion.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(_delegacionId, usuario, rol));
            await preparacion.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        var servicio = CrearServicio(contexto, tenantSeleccionado: _clienteDelegante, usuarioId: usuario);

        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
    }

    /// <summary>
    /// Lo mismo por la vía nueva (token de operación): una cartera externa
    /// heredada con un rol de Propiedad no da rol; con Consulta sí (control).
    /// </summary>
    [Theory]
    [InlineData("Administrador", null)]
    [InlineData("DireccionCae", null)]
    [InlineData("Consulta", "Consulta")]
    public async Task Una_cartera_externa_heredada_solo_da_roles_de_Operacion(string rol, string? esperado)
    {
        var usuario = Guid.NewGuid();
        Guid operacionId;
        Guid propietario;
        var tenantPropietario = new Tenant("Propietario", PerfilVocabularioTenant.ClienteDirecto);
        var tenantOperador = new Tenant("Operador", PerfilVocabularioTenant.Consultora);
        await using (var alta = CrearContexto())
        {
            alta.Tenants.AddRange(tenantPropietario, tenantOperador);
            await alta.SaveChangesAsync();
        }

        await using (var preparacion = CrearContexto(tenantPropietario.Id))
        {
            var ahora = DateTime.UtcNow;
            var operacion = CaeManager.Domain.Operaciones.AsignacionOperacion.Externa(
                tenantPropietario.Id, tenantOperador.Id, CaeManager.Domain.Operaciones.ServicioCae.Outbound,
                CaeManager.Domain.Operaciones.AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            preparacion.AsignacionesOperacion.Add(operacion);
            preparacion.AsignacionesCartera.Add(CaeManager.Domain.Operaciones.AsignacionCartera.Externa(
                operacion, usuario, rol, CaeManager.Domain.Operaciones.AmbitoAsignacion.Universal,
                ahora.AddDays(-1), null, ahora));
            await preparacion.SaveChangesAsync();
            operacionId = operacion.Id;
            propietario = tenantPropietario.Id;
        }

        await using var contexto = CrearContexto(propietario);
        var servicio = CrearServicio(contexto, tenantSeleccionado: propietario, usuarioId: usuario, asignacionOperacionId: operacionId);

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be(esperado);
    }

    private CurrentUserService CrearServicio(
        CaeManagerDbContext contexto, Guid? tenantSeleccionado, Guid? usuarioId = null, Guid? asignacionOperacionId = null)
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
        servicios.AddSingleton<ITenantsQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorFalso(),
            new ClienteActivoSeleccionadoFalso(tenantSeleccionado, asignacionOperacionId),
            servicios.BuildServiceProvider());
    }

    private CaeManagerDbContext CrearContexto(Guid? tenantSellado = null)
    {
        // Sellado contra el cliente delegante: es el tenant que se estaría
        // operando. DelegacionTenant/AsignacionOperadorDelegado son catálogo
        // global sin filtro, así que se leen igual desde cualquiera.
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado ?? _clienteDelegante };
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

        // Estos tests son de plano 2: ninguno abre una sesión privilegiada.
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
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
