using System.Security.Claims;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.AsistenteIa;

/// <summary>
/// Nivel 0 sobre la cartera del asistente (<see cref="ComprobarInstruccionIaCarteraQuery"/>)
/// contra PostgreSQL con <c>cae_app_runtime</c>: la instrucción de tratamiento con IA
/// de un Tenant beneficiario solo se ve desde su propio ámbito (filtro de Tenant y
/// RLS <c>aislamiento_tenant</c>). Si la Query la leyera fuera del
/// <see cref="AmbitoTenantExplicito"/> de cada vuelta, todo Tenant distinto del de la
/// petición saldría sin instrucción y el asistente fallaría cerrado siempre.
/// <para>
/// Escenario: Administrador en el Tenant de origen del Operador CAE externo, con
/// instrucción; Gestor CAE con Asignación de Cartera en dos Tenants beneficiarios,
/// uno con instrucción y otro sin ella; y un tercer Tenant beneficiario delegado
/// sin cartera y sin instrucción, que también cuenta: la comprobación no descarta
/// ningún Tenant autorizado por alcance.
/// </para>
/// </summary>
public class InstruccionIaCarteraBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantConInstruccion;
    private Guid _tenantSinInstruccion;
    private Guid _tenantAlcanceCero;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var origen = new Tenant("Operador CAE externo de prueba");
        var conInstruccion = new Tenant("Beneficiario con instrucción");
        var sinInstruccion = new Tenant("Beneficiario sin instrucción");
        var alcanceCero = new Tenant("Beneficiario sin cartera");
        _propietario.Tenants.AddRange(origen, conInstruccion, sinInstruccion, alcanceCero);
        (_tenantOrigen, _tenantConInstruccion, _tenantSinInstruccion, _tenantAlcanceCero) =
            (origen.Id, conInstruccion.Id, sinInstruccion.Id, alcanceCero.Id);
        foreach (var tenant in new[] { _tenantConInstruccion, _tenantSinInstruccion, _tenantAlcanceCero })
        {
            var delegacion = new DelegacionTenant(_tenantOrigen, tenant);
            _propietario.DelegacionesTenant.Add(delegacion);
            _propietario.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(delegacion.Id, _usuario, Roles.GestorCae));
        }
        await _propietario.SaveChangesAsync();

        // Cartera real en dos de los tres Tenants beneficiarios: la comprobación
        // no la mira, pero el escenario es el de producción.
        var ahora = DateTime.UtcNow;
        var cifs = new Queue<string>(["B10380186", "B10380194"]);
        foreach (var tenant in new[] { _tenantConInstruccion, _tenantSinInstruccion })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenant);
            var cliente = Empresa.CrearComoCliente("Cliente empresarial en cartera", cifs.Dequeue(), false, null, null);
            _propietario.Empresas.Add(cliente);
            await _propietario.SaveChangesAsync();

            var operacion = AsignacionOperacion.Externa(
                tenant, _tenantOrigen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora);
            _propietario.AsignacionesOperacion.Add(operacion);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _usuario, Roles.GestorCae, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora, null, ahora));
            await _propietario.SaveChangesAsync();
        }

        foreach (var tenant in new[] { _tenantOrigen, _tenantConInstruccion })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenant);
            _propietario.InstruccionesTratamientoIaTenantPropietario.Add(new InstruccionTratamientoIaTenantPropietario(
                "1.0", "1.0", ahora, OrigenInstruccionTratamientoIa.AltaManualPlataforma, _usuario));
            await _propietario.SaveChangesAsync();
        }

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        var usuarioInterceptor = new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenantOrigen);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuarioInterceptor, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);

        var usuarioReal = CrearCurrentUserService(_runtime);

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(_runtime);
        servicios.AddSingleton<ITenantsQueryContext>(_runtime);
        servicios.AddSingleton<IInstruccionTratamientoIaTenantPropietarioRepository>(
            new InstruccionTratamientoIaTenantPropietarioRepository(_runtime));
        servicios.AddSingleton<ICurrentUserService>(usuarioReal);
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>
    /// Control del instrumento: la instrucción de otro Tenant no se ve desde el de
    /// la petición, ni quitando el filtro de EF (RLS), y sí desde su ámbito.
    /// </summary>
    [Fact]
    public async Task La_instruccion_de_otro_Tenant_solo_se_ve_desde_su_ambito()
    {
        (await _runtime.InstruccionesTratamientoIaTenantPropietario.IgnoreQueryFilters()
            .CountAsync(i => i.TenantId == _tenantConInstruccion)).Should().Be(0, "RLS la oculta fuera de su Tenant");

        using var alcance = _servicios.CreateScope();
        var instruccion = alcance.ServiceProvider.GetRequiredService<IInstruccionTratamientoIaService>();
        (await instruccion.EstaHabilitadaAsync(_tenantConInstruccion)).Should().BeFalse(
            "leída desde el Tenant de la petición, la instrucción de otro Tenant parece ausente");

        using (AmbitoTenantExplicito.Establecer(_tenantConInstruccion))
            (await instruccion.EstaHabilitadaAsync(_tenantConInstruccion)).Should().BeTrue();
    }

    [Fact]
    public async Task Separa_la_cartera_por_instruccion_leyendo_cada_Tenant_en_su_ambito()
    {
        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ComprobarInstruccionIaCarteraQuery());

        resultado.ConInstruccion.Select(t => t.TenantId).Should().BeEquivalentTo([_tenantOrigen, _tenantConInstruccion]);
        resultado.SinInstruccion.Select(t => t.TenantId).Should().BeEquivalentTo(
            [_tenantSinInstruccion, _tenantAlcanceCero], "falla cerrado: también cuenta el Tenant autorizado sin Asignación de Cartera");
        resultado.ErrorSiFalta()!.Mensaje.Should().Contain("Beneficiario sin instrucción").And.Contain("Beneficiario sin cartera");
    }

    private CurrentUserService CrearCurrentUserService(ITenantsQueryContext contexto)
    {
        var identidad = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, _usuario.ToString()),
                new Claim(ClaimTypes.Role, "Administrador"),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, _tenantOrigen.ToString())
            ],
            "prueba");

        var servicios = new ServiceCollection();
        servicios.AddSingleton(contexto);

        return new CurrentUserService(
            new AuthenticationStateProviderFalso(new ClaimsPrincipal(identidad)),
            new HttpContextAccessorFalso(),
            new SinClienteActivo(),
            servicios.BuildServiceProvider());
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
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
