using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
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
/// P8 (decisión del propietario 2026-09-23): una
/// <see cref="AsignacionOperadorDelegado"/> revocada es historial y no concede
/// nada — ni rol efectivo en el Tenant propietario (lectura ni escritura) ni
/// entrada en el selector de Context Workspace. Se prueba con un Gestor CAE
/// revocado a propósito: así el mecanismo de revocación queda demostrado por
/// sí mismo, sin apoyarse en el filtro de roles delegables de
/// <c>CurrentUserService</c>, que ya descarta Administrador y Dirección CAE.
/// </summary>
public class RevocacionAsignacionOperadorDelegadoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestoraRevocada = Guid.NewGuid();
    private readonly Guid _gestorValido = Guid.NewGuid();
    private readonly Guid _consultaValida = Guid.NewGuid();
    private Guid _operadorCae;   // ArcosSPA: Operador CAE externo
    private Guid _propietario;   // Refrielectric: Tenant propietario
    private Guid _delegacionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        await contexto.Database.MigrateAsync();

        var operador = new Tenant("ArcosSPA (Operador CAE externo)", PerfilVocabularioTenant.Consultora);
        var propietario = new Tenant("Refrielectric (Tenant propietario)", PerfilVocabularioTenant.ClienteDirecto);
        contexto.Tenants.AddRange(operador, propietario);
        _operadorCae = operador.Id;
        _propietario = propietario.Id;

        var delegacion = new DelegacionTenant(_operadorCae, _propietario);
        contexto.DelegacionesTenant.Add(delegacion);
        _delegacionId = delegacion.Id;

        var revocada = new AsignacionOperadorDelegado(delegacion.Id, _gestoraRevocada, "GestorCae");
        revocada.Revocar("Prueba de revocación", DateTime.UtcNow);
        contexto.AsignacionesOperadorDelegadoConRevocadas.AddRange(
            revocada,
            new AsignacionOperadorDelegado(delegacion.Id, _gestorValido, "GestorCae"),
            new AsignacionOperadorDelegado(delegacion.Id, _consultaValida, "Consulta"));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_asignacion_revocada_no_da_rol_efectivo_en_el_Tenant_propietario()
    {
        await using var contexto = CrearContexto(_propietario);
        var servicio = CrearServicio(contexto, _gestoraRevocada);

        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull(
            "sin rol efectivo no hay ni lectura ni escritura en el Tenant propietario");
    }

    [Theory]
    [InlineData("gestor", "GestorCae")]
    [InlineData("consulta", "Consulta")]
    public async Task Control_positivo_un_Gestor_CAE_o_una_Consulta_validos_siguen_operativos(string quien, string rolEsperado)
    {
        var usuario = quien == "gestor" ? _gestorValido : _consultaValida;
        await using var contexto = CrearContexto(_propietario);
        var servicio = CrearServicio(contexto, usuario);

        (await servicio.ObtenerRolEfectivoAsync()).Should().Be(rolEsperado);
    }

    [Fact]
    public async Task La_revocada_no_aparece_en_el_selector_de_Context_Workspace()
    {
        await using var contexto = CrearContexto(_operadorCae);

        var revocada = await new ObtenerClientesAutorizadosQueryHandler(
                contexto, new CurrentUserServiceFalso(_gestoraRevocada, tenantOrigenId: _operadorCae))
            .Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);
        var valida = await new ObtenerClientesAutorizadosQueryHandler(
                contexto, new CurrentUserServiceFalso(_gestorValido, tenantOrigenId: _operadorCae))
            .Handle(new ObtenerClientesAutorizadosQuery(), CancellationToken.None);

        revocada.Should().NotContain(c => c.TenantId == _propietario);
        valida.Should().ContainSingle(c => c.TenantId == _propietario, "control positivo");
    }

    [Fact]
    public async Task La_vista_de_lectura_oculta_la_revocada_y_la_tabla_la_conserva()
    {
        await using var contexto = CrearContexto(_propietario);

        (await contexto.AsignacionesOperadorDelegado.AnyAsync(a => a.UsuarioId == _gestoraRevocada))
            .Should().BeFalse();
        var historial = await contexto.AsignacionesOperadorDelegadoConRevocadas
            .SingleAsync(a => a.UsuarioId == _gestoraRevocada);
        historial.EstaRevocada.Should().BeTrue("revocar no borra: la fila queda como historial");
        historial.MotivoRevocacion.Should().Be("Prueba de revocación");
    }

    /// <summary>
    /// La única forma de devolverle un rol a la persona es una asignación nueva
    /// —con un rol delegable— que convive con la revocada: el índice único solo
    /// cubre las vigentes. La revocada no se reactiva.
    /// </summary>
    [Fact]
    public async Task Tras_revocar_se_puede_conceder_una_asignacion_nueva_sin_reactivar_la_revocada()
    {
        await using (var alta = CrearContexto(_propietario))
        {
            alta.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacionId, _gestoraRevocada, "Consulta"));
            await alta.SaveChangesAsync();
        }

        await using var contexto = CrearContexto(_propietario);
        var filas = await contexto.AsignacionesOperadorDelegadoConRevocadas
            .Where(a => a.UsuarioId == _gestoraRevocada).ToListAsync();
        filas.Should().HaveCount(2);
        filas.Should().ContainSingle(a => a.EstaRevocada).Which.Rol.Should().Be("GestorCae");

        (await CrearServicio(contexto, _gestoraRevocada).ObtenerRolEfectivoAsync()).Should().Be("Consulta");
    }

    [Fact]
    public async Task El_indice_unico_sigue_impidiendo_dos_asignaciones_vigentes()
    {
        await using var contexto = CrearContexto(_propietario);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(_delegacionId, _gestorValido, "Consulta"));

        await contexto.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    private CurrentUserService CrearServicio(CaeManagerDbContext contexto, Guid usuarioId)
    {
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()),
                new Claim(ClaimTypes.Role, "Administrador")
            ],
            "prueba");

        var servicios = new ServiceCollection();
        servicios.AddSingleton<ITenantsQueryContext>(contexto);
        servicios.AddSingleton<CaeManager.Application.Operaciones.IOperacionesQueryContext>(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorFalso(),
            new ClienteActivoSeleccionadoFalso(_propietario),
            servicios.BuildServiceProvider());
    }

    private CaeManagerDbContext CrearContexto(Guid tenantSellado)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid tenantId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        // Vía heredada (DelegacionTenant + AsignacionOperadorDelegado).
        public Guid? AsignacionOperacionIdSeleccionada => null;

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
