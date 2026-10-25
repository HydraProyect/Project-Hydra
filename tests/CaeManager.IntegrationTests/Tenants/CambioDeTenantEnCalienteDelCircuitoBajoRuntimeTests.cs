using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
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
/// Refuerzo del aislamiento multi-Tenant (2026-10-25): cambio de Tenant en caliente dentro de un mismo
/// circuito de Blazor Server, con el DbContext Scoped del circuito, bajo <c>cae_app_runtime</c>.
///
/// <para>
/// Elegir otro Tenant beneficiario en el selector recarga la página (circuito nuevo, ver
/// <see cref="ClienteActivoSeleccionado"/>). El único cambio de Tenant DENTRO de un circuito vivo es la
/// retirada de la selección (<see cref="ClienteActivoSeleccionado.Invalidar"/>, que llaman
/// <c>RevalidacionCircuitoActivoHandler</c> y el middleware cuando la Asignación de Operación o la de
/// Cartera dejan de valer): el <see cref="TenantActual"/> real pasa del Tenant beneficiario B al Tenant de
/// origen O del Gestor CAE, con el mismo DbContext y lo que ya tenga rastreado.
/// </para>
///
/// <para>
/// Escenario: el Operador CAE externo O, con una Asignación de Operación Outbound sobre el Tenant
/// beneficiario B y, dentro de ella, la Asignación de Cartera universal del Gestor CAE. Una Empresa en B y
/// otra en O, sembradas como propietario de la base. Todo lo que se prueba corre por el contenedor del
/// arnés, con los interceptores de producción y el <see cref="TenantActual"/>, la
/// <see cref="ClienteActivoSeleccionado"/> y la cookie protegida reales.
/// </para>
/// </summary>
public class CambioDeTenantEnCalienteDelCircuitoBajoRuntimeTests : IAsyncLifetime
{
    private readonly TenantDelCircuito _tenantActual = new();
    private readonly ActorDelCircuito _actor = new();
    private readonly CurrentUserServiceMutable _sesion = new() { Rol = Roles.GestorCae };
    private readonly Guid _gestor = Guid.NewGuid();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _operador;
    private Guid _beneficiario;
    private Guid _operacion;
    private Guid _empresaDeB;
    private Guid _empresaDeO;

    public async Task InitializeAsync()
    {
        _actor.Actual = _gestor;
        _sesion.UsuarioId = _gestor;
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: _tenantActual,
            actorAuditoriaPersonalizado: _actor,
            // Lo que la RLS toma de la sesión: el Gestor CAE y su Tenant de origen O (se fija al sembrar O).
            // Tras retirar la selección opera en su propio Tenant, que es justo lo que se mide.
            currentUserServicePersonalizado: _sesion);

        await using (var siembra = ContextoPropietario())
        {
            var operador = new Tenant($"Operador CAE externo {Guid.NewGuid():N}", PerfilVocabularioTenant.Consultora);
            operador.HabilitarComoOperadorCaeExterno();
            var beneficiario = new Tenant($"Tenant beneficiario {Guid.NewGuid():N}", PerfilVocabularioTenant.ClienteDirecto);
            siembra.Tenants.AddRange(operador, beneficiario);
            await siembra.SaveChangesAsync();
            (_operador, _beneficiario) = (operador.Id, beneficiario.Id);
        }
        _sesion.TenantOrigenId = _operador;

        await using (var siembra = ContextoPropietario())
        {
            var ahora = DateTime.UtcNow;
            var operacion = AsignacionOperacion.Externa(
                _beneficiario, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-30), vigenciaHasta: null, ahora);
            siembra.AsignacionesOperacion.Add(operacion);
            siembra.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _gestor, Roles.GestorCae, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
            await siembra.SaveChangesAsync();
            _operacion = operacion.Id;
        }

        _empresaDeB = await SembrarEmpresaAsync(_beneficiario, "Empresa del Tenant beneficiario", "B10380186");
        _empresaDeO = await SembrarEmpresaAsync(_operador, "Empresa del Operador CAE externo", "B10380194");
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Tras_retirar_la_seleccion_ninguna_consulta_devuelve_datos_del_Tenant_anterior()
    {
        var circuito = AbrirCircuito();
        await using var ambito = _arnes.Servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        circuito.Tenant.TenantId.Should().Be(_beneficiario, "control positivo: la cookie selecciona el Tenant beneficiario");
        var antes = await contexto.Empresas.Select(e => e.Id).ToListAsync();
        antes.Should().Contain(_empresaDeB, "control positivo: el Gestor CAE con cartera lee B por la Asignación de Operación");
        antes.Should().NotContain(_empresaDeO);
        await contexto.Empresas.SingleAsync(e => e.Id == _empresaDeB); // queda rastreada en el DbContext del circuito

        circuito.Seleccion.Invalidar();

        circuito.Tenant.TenantId.Should().Be(_operador, "sin selección, el Tenant vuelve al de origen del claim");
        var despues = await contexto.Empresas.Select(e => e.Id).ToListAsync();
        despues.Should().Contain(_empresaDeO, "control positivo: ahora lee su propio Tenant");
        despues.Should().NotContain(_empresaDeB);

        (await contexto.Empresas.IgnoreQueryFilters().Select(e => e.Id).ToListAsync())
            .Should().NotContain(_empresaDeB, "sin el filtro de EF, la RLS de la conexión reabierta ya es la de O");
        (await contexto.Empresas.FirstOrDefaultAsync(e => e.Id == _empresaDeB))
            .Should().BeNull("una consulta por el Id rastreado no resucita la entidad: la base no devuelve la fila");
        (await contexto.Empresas.AsNoTracking().AnyAsync(e => e.Id == _empresaDeB)).Should().BeFalse();
    }

    [Fact]
    public async Task Una_entidad_del_Tenant_anterior_que_seguia_rastreada_no_se_guarda()
    {
        var circuito = AbrirCircuito();
        await using var ambito = _arnes.Servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var deB = await contexto.Empresas.SingleAsync(e => e.Id == _empresaDeB);

        circuito.Seleccion.Invalidar();
        deB.Actualizar("Renombrada desde el Tenant de origen", "B10380186");

        var guardar = () => contexto.SaveChangesAsync();
        await guardar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*perteneciente a otro tenant*");

        await using var propietario = ContextoPropietario();
        (await propietario.Empresas.IgnoreQueryFilters().SingleAsync(e => e.Id == _empresaDeB)).RazonSocial
            .Should().Be("Empresa del Tenant beneficiario", "la fila de B no cambió");
    }

    [Fact]
    public async Task Una_entidad_del_Tenant_anterior_que_seguia_rastreada_no_se_borra()
    {
        var circuito = AbrirCircuito();
        await using var ambito = _arnes.Servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var deB = await contexto.Empresas.SingleAsync(e => e.Id == _empresaDeB);

        circuito.Seleccion.Invalidar();
        contexto.Empresas.Remove(deB);

        var guardar = () => contexto.SaveChangesAsync();
        await guardar.Should().ThrowAsync<InvalidOperationException>().WithMessage("*perteneciente a otro tenant*");

        await using var propietario = ContextoPropietario();
        (await propietario.Empresas.IgnoreQueryFilters().AnyAsync(e => e.Id == _empresaDeB)).Should().BeTrue("la fila de B sigue ahí");
    }

    /// <summary>
    /// Por qué <c>EndpointsQueSirvenBinariosEstanCatalogadosTests</c> tiene un hermano que congela
    /// <c>Find</c> y <c>.Local</c>: el rastreador de EF no pasa por el filtro de Tenant ni por la RLS. Tras el
    /// cambio en caliente, <c>FindAsync</c> devuelve la entidad de B sin tocar la base. Producción no lo usa
    /// (ver <c>LecturasDelRastreadorDeEfEstanCatalogadasTests</c>); este test documenta el riesgo que esa
    /// regla cierra.
    /// </summary>
    [Fact]
    public async Task El_rastreador_si_conserva_la_entidad_anterior_por_eso_produccion_no_lee_de_el()
    {
        var circuito = AbrirCircuito();
        await using var ambito = _arnes.Servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Empresas.SingleAsync(e => e.Id == _empresaDeB);

        circuito.Seleccion.Invalidar();

        (await contexto.Empresas.FindAsync(_empresaDeB)).Should().NotBeNull("Find sirve desde el rastreador, sin filtro ni RLS");
        contexto.Empresas.Local.Select(e => e.Id).Should().Contain(_empresaDeB);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private sealed record Circuito(TenantActual Tenant, ClienteActivoSeleccionado Seleccion);

    /// <summary>
    /// El circuito de Blazor del Gestor CAE: principal congelado con el claim de su Tenant de origen O, la
    /// cookie con el token emitido por la propia clase de producción para B y su Asignación de Operación, y
    /// el <see cref="TenantActual"/> real encima. El DbContext del arnés lo lee a través de
    /// <see cref="TenantDelCircuito"/>.
    /// </summary>
    private Circuito AbrirCircuito()
    {
        var proteccion = new EphemeralDataProtectionProvider();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, _gestor.ToString()),
            new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _operador.ToString()),
            new Claim(ClaimTypes.Role, Roles.GestorCae),
        ], "prueba"));

        var http = new DefaultHttpContext { User = principal };
        http.Request.Headers.Cookie =
            $"{ClienteActivoSeleccionado.NombreCookie}={ClienteActivoSeleccionado.Proteger(proteccion, _gestor, _beneficiario, _operacion)}";
        var accesor = new HttpContextAccessor { HttpContext = http };
        var seleccion = new ClienteActivoSeleccionado(accesor, proteccion);
        var tenant = new TenantActual(new PrincipalDelCircuito(principal), accesor, seleccion);

        _tenantActual.Circuito = tenant;
        return new Circuito(tenant, seleccion);
    }

    private async Task<Guid> SembrarEmpresaAsync(Guid tenant, string razonSocial, string cif)
    {
        await using var siembra = ContextoPropietario(tenant);
        var empresa = new Empresa(razonSocial, cif);
        siembra.Empresas.Add(empresa);
        await siembra.SaveChangesAsync();
        return empresa.Id;
    }

    /// <summary>Rol propietario de la base: solo siembra y comprueba lo que quedó escrito.</summary>
    private CaeManagerDbContext ContextoPropietario(Guid? tenant = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// El <c>ITenantActual</c> del contenedor del arnés (singleton) delega en el <see cref="TenantActual"/>
    /// del circuito abierto; el ámbito explícito manda, como en producción.
    /// </summary>
    private sealed class TenantDelCircuito : ITenantActual
    {
        public ITenantActual? Circuito { get; set; }
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? Circuito?.TenantId;
    }

    private sealed class ActorDelCircuito : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }

    private sealed class PrincipalDelCircuito(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
