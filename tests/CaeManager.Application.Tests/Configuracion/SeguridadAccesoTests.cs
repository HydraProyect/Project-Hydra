using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.ActualizarSeguridadAcceso;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// Solo el Administrador del Tenant, leído en la base, cambia su seguridad de acceso. El Tenant es
/// siempre el actual, y el Tenant de plataforma queda fuera para no rebajar a los Actores de
/// Plataforma TALVEG (ASVS 4.0.3 V3.3.2, V2.8, V4.1.3).
/// </summary>
public class SeguridadAccesoTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();

    [Fact]
    public async Task El_Administrador_del_Tenant_guarda_el_plazo_y_la_exigencia()
    {
        var (tenant, tenants) = Tenants();
        var parametros = new ParametroSistema(30, 15);
        var unidad = new UnitOfWorkFalso();

        var resultado = await Manejador(tenant.Id, tenant.Id, tenants, parametros, unidad)
            .Handle(new ActualizarSeguridadAccesoCommand(45, true), default);

        resultado.EsExitoso.Should().BeTrue();
        parametros.MinutosInactividadSesion.Should().Be(45);
        parametros.ExigeSegundoFactorTodosLosPerfiles.Should().BeTrue();
    }

    /// <summary>Gestor CAE u Operador CAE externo con otro Tenant seleccionado, o Soporte TALVEG en Sesión Privilegiada.</summary>
    [Fact]
    public async Task Quien_no_es_Administrador_del_Tenant_actual_no_cambia_nada()
    {
        var (tenant, tenants) = Tenants();
        var parametros = new ParametroSistema(30, 15);

        var resultado = await Manejador(tenant.Id, OtroTenant, tenants, parametros, new UnitOfWorkFalso())
            .Handle(new ActualizarSeguridadAccesoCommand(480, true), default);

        resultado.Error.Should().Be(AutoridadSeguridadAcceso.SoloAdministradorDelTenant);
        parametros.MinutosInactividadSesion.Should().Be(30);
        parametros.ExigeSegundoFactorTodosLosPerfiles.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_Tenant_actual_no_se_cambia_nada()
    {
        var (_, tenants) = Tenants();
        var parametros = new ParametroSistema(30, 15);

        var resultado = await Manejador(null, OtroTenant, tenants, parametros, new UnitOfWorkFalso())
            .Handle(new ActualizarSeguridadAccesoCommand(480, true), default);

        resultado.Error.Should().Be(AutoridadSeguridadAcceso.SoloAdministradorDelTenant);
    }

    [Fact]
    public async Task El_Tenant_de_plataforma_no_se_puede_rebajar()
    {
        var (tenant, tenants) = Tenants(plataforma: true);
        var parametros = new ParametroSistema(30, 15);

        var resultado = await Manejador(tenant.Id, tenant.Id, tenants, parametros, new UnitOfWorkFalso())
            .Handle(new ActualizarSeguridadAccesoCommand(480, false), default);

        resultado.Error.Should().Be(AutoridadSeguridadAcceso.SoloAdministradorDelTenant);
        parametros.MinutosInactividadSesion.Should().Be(30);
    }

    [Theory]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(480, false)]
    [InlineData(481, true)]
    public void El_validador_acota_los_minutos(int minutos, bool conError)
    {
        var resultado = new ActualizarSeguridadAccesoCommandValidator()
            .TestValidate(new ActualizarSeguridadAccesoCommand(minutos, false));

        resultado.IsValid.Should().Be(!conError);
        if (conError)
            resultado.Errors.Should().ContainSingle(e => e.ErrorMessage == "Introduce un valor entre 5 y 480 minutos.");
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    public async Task La_consulta_dice_si_se_puede_editar(bool esAdministrador, bool plataforma, bool puedeEditar)
    {
        var (tenant, tenants) = Tenants(plataforma);
        var parametros = new ParametroSistema(30, 15);
        parametros.ActualizarSeguridadAcceso(45, true);

        var ajuste = await new ObtenerSeguridadAccesoQueryHandler(
                new ConfiguracionFalsa(parametros), new TenantActualFijo(tenant.Id), new CurrentUserServiceFalso(Usuario),
                new AdministradorFalso(esAdministrador ? tenant.Id : OtroTenant), tenants)
            .Handle(new ObtenerSeguridadAccesoQuery(), default);

        ajuste.Should().Be(new SeguridadAccesoDto(45, true, puedeEditar));
    }

    private static (Tenant Tenant, TenantsQueryContextFalso Contexto) Tenants(bool plataforma = false)
    {
        var tenant = new Tenant("Tenant de prueba");
        if (plataforma) tenant.MarcarComoPlataforma();
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return (tenant, contexto);
    }

    private static ActualizarSeguridadAccesoCommandHandler Manejador(
        Guid? tenantActual, Guid administradorDe, TenantsQueryContextFalso tenants, ParametroSistema parametros, UnitOfWorkFalso unidad) =>
        new(new TenantActualFijo(tenantActual), new CurrentUserServiceFalso(Usuario), new AdministradorFalso(administradorDe),
            tenants, new RepositorioFalso(parametros), unidad);

    private sealed class AdministradorFalso(Guid tenantDelAdministrador) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantDelAdministrador == tenantId);
    }

    private sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class RepositorioFalso(ParametroSistema parametros) : IParametroSistemaRepository
    {
        public Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(parametros);
        public void Agregar(ParametroSistema parametro) { }
    }

    private sealed class ConfiguracionFalsa(ParametroSistema parametros) : IConfiguracionQueryContext
    {
        public IQueryable<ParametroSistema> ParametrosSistema =>
            new CaeManager.Application.Tests.Integraciones.TestAsyncQueryable<ParametroSistema>(new[] { parametros }.AsQueryable());
        public IQueryable<PreferenciaDashboardUsuario> PreferenciasDashboardUsuario => throw new NotSupportedException();
        public IQueryable<FiltroGuardado> FiltrosGuardados => throw new NotSupportedException();
        public IQueryable<OrdenCajasFicha> OrdenesCajasFicha => throw new NotSupportedException();
        public IQueryable<EstadoAutomatizacion> EstadosAutomatizacion => throw new NotSupportedException();
    }
}
