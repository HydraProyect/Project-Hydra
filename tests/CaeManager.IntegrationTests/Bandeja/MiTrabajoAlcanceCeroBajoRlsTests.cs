using System.Security.Claims;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
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

namespace CaeManager.IntegrationTests.Bandeja;

/// <summary>
/// P2.3 de la demo a Dirección: Mi trabajo agregada marca por Tenant el
/// alcance cero (<see cref="MiTrabajoTenantDto.AlcanceCero"/>) para que la
/// pantalla no diga «Cartera al día» donde no hay nada que vigilar.
///
/// <para>
/// A diferencia de los demás tests de Mi trabajo bajo RLS, aquí el alcance es
/// el <b>real</b>: <see cref="AlcanceDatosService"/> y
/// <see cref="CurrentUserService"/> de producción, reutilizados por el fan-out
/// en cada vuelta de <see cref="AmbitoTenantExplicito"/>, con la lectura
/// autenticada como <c>cae_app_runtime</c> y los interceptores de sellado y de
/// sesión RLS. Un doble del alcance no podría demostrar lo que importa: que el
/// rol efectivo y la cartera se resuelven en el Tenant de cada vuelta y no se
/// heredan del anterior.
/// </para>
///
/// <para>
/// Escenario: el usuario es Administrador en el Tenant de origen del Operador
/// CAE externo (claim de sesión) y Gestor CAE en dos Tenants beneficiarios
/// delegantes. En uno tiene una Asignación de Cartera vigente sobre un Cliente
/// empresarial, sin ningún pendiente; en el otro, ninguna.
/// </para>
/// </summary>
public class MiTrabajoAlcanceCeroBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private ServiceProvider _servicios = null!;
    private Guid _tenantOrigen;
    private Guid _tenantConCartera;
    private Guid _tenantSinCartera;
    private Guid _clienteSinCartera;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantOrigen = new Tenant("Operador CAE externo de prueba");
        var tenantConCartera = new Tenant("Tenant beneficiario con cartera");
        var tenantSinCartera = new Tenant("Tenant beneficiario sin cartera");
        _propietario.Tenants.AddRange(tenantOrigen, tenantConCartera, tenantSinCartera);
        _tenantOrigen = tenantOrigen.Id;
        _tenantConCartera = tenantConCartera.Id;
        _tenantSinCartera = tenantSinCartera.Id;
        var delegacionConCartera = new DelegacionTenant(_tenantOrigen, _tenantConCartera);
        var delegacionSinCartera = new DelegacionTenant(_tenantOrigen, _tenantSinCartera);
        _propietario.DelegacionesTenant.AddRange(delegacionConCartera, delegacionSinCartera);
        _propietario.AsignacionesOperadorDelegado.AddRange(
            new AsignacionOperadorDelegado(delegacionConCartera.Id, _usuario, Roles.GestorCae),
            new AsignacionOperadorDelegado(delegacionSinCartera.Id, _usuario, Roles.GestorCae));
        await _propietario.SaveChangesAsync();

        foreach (var tenant in new[] { _tenantOrigen, _tenantConCartera, _tenantSinCartera })
        {
            using var ambito = AmbitoTenantExplicito.Establecer(tenant);
            _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            await _propietario.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;
        using (AmbitoTenantExplicito.Establecer(_tenantConCartera))
        {
            var cliente = Empresa.CrearComoCliente("Cervezas Duff con cartera", "B10380186", false, null, null);
            _propietario.Empresas.Add(cliente);
            await _propietario.SaveChangesAsync();

            var operacion = AsignacionOperacion.Externa(
                _tenantConCartera, _tenantOrigen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora);
            _propietario.AsignacionesOperacion.Add(operacion);
            _propietario.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _usuario, Roles.GestorCae, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora, null, ahora));
            await _propietario.SaveChangesAsync();
        }

        // Sin cartera: hay un Cliente empresarial, y hasta una operación del
        // Operador CAE sobre el Tenant, pero ninguna Asignación de Cartera del
        // usuario. Con cliente y operación presentes, alcance cero no puede
        // salir de un Tenant vacío: sale de la cartera.
        using (AmbitoTenantExplicito.Establecer(_tenantSinCartera))
        {
            var cliente = Empresa.CrearComoCliente("Cervezas Duff sin cartera", "B10380194", false, null, null);
            _propietario.Empresas.Add(cliente);
            _propietario.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _tenantSinCartera, _tenantOrigen, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora, null, ahora));
            await _propietario.SaveChangesAsync();
            _clienteSinCartera = cliente.Id;
        }

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantOrigen);
        var usuarioInterceptor = new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenantOrigen);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuarioInterceptor))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);

        var usuarioReal = CrearCurrentUserService(_runtime);

        var servicios = new ServiceCollection();
        servicios.AddApplication();
        servicios.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        servicios.AddSingleton<ITenantActual>(tenantDeLaPeticion);
        servicios.AddSingleton<IUnitOfWork>(_runtime);
        servicios.AddSingleton<ITenantsQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Empresas.IEmpresasQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Centros.ICentrosQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Trabajadores.ITrabajadoresQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.TiposDocumento.ITiposDocumentoQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Documentos.IDocumentosQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.DocumentosIa.IDocumentosIaQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Asignaciones.IAsignacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Configuracion.IConfiguracionQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Visitas.IVisitasQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Comunicaciones.IComunicacionesQueryContext>(_runtime);
        servicios.AddSingleton<CaeManager.Application.Integraciones.IProveedoresPlataformaCaeQueryContext>(_runtime);
        servicios.AddSingleton<ICurrentUserService>(usuarioReal);
        // Una sola instancia para todo el fan-out, como el ámbito scoped real:
        // es la reutilización entre vueltas lo que tiene que resolver bien.
        servicios.AddSingleton<IAlcanceDatosService>(
            new AlcanceDatosService(_runtime, usuarioReal, tenantDeLaPeticion, new SesionPrivilegiadaAusente()));
        _servicios = servicios.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        _servicios.Dispose();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la lectura va de verdad bajo RLS.</summary>
    [Fact]
    public async Task La_lectura_va_bajo_RLS_efectiva()
    {
        (await _runtime.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _clienteSinCartera)).Should().Be(0,
            "fuera del ámbito del delegante, app.tenant_id es el Tenant de origen y RLS oculta la Empresa");

        using (AmbitoTenantExplicito.Establecer(_tenantSinCartera))
            (await _runtime.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _clienteSinCartera)).Should().Be(1);
    }

    [Fact]
    public async Task Marca_alcance_cero_solo_en_el_Tenant_sin_Asignacion_de_Cartera()
    {
        var resultado = await _servicios.GetRequiredService<IMediator>().Send(new ObtenerMiTrabajoAgregadoQuery());

        resultado.Tenants.Select(t => t.TenantId).Should().BeEquivalentTo(
            [_tenantOrigen, _tenantConCartera, _tenantSinCartera], "los tres Tenants autorizados llegan, con o sin cartera");

        var sinCartera = resultado.Tenants.Single(t => t.TenantId == _tenantSinCartera);
        sinCartera.AlcanceCero.Should().BeTrue("Gestor CAE ahí, pero sin ninguna Asignación de Cartera vigente");
        sinCartera.Resumen.TotalAcciones.Should().Be(0);

        var conCartera = resultado.Tenants.Single(t => t.TenantId == _tenantConCartera);
        conCartera.AlcanceCero.Should().BeFalse(
            "tiene cartera sobre un Cliente empresarial: una cola vacía aquí sí es «al día»");
        conCartera.Resumen.TotalAcciones.Should().Be(0, "la siembra no deja ningún pendiente");

        resultado.Tenants.Single(t => t.TenantId == _tenantOrigen).AlcanceCero.Should().BeFalse(
            "Administrador en su Tenant de origen: acceso total, nunca alcance cero");
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
