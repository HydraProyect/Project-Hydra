using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.FijarPlazoRetencionDocumentos;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.TiposDocumento.Commands.FijarPlazoRetencionTipoDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using FluentValidation.TestHelper;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// Quién fija el plazo de conservación de los Documentos (decisión del 2026-11-18): solo el Administrador del Tenant
/// propietario actual, leído en la base, y nunca dentro de una Sesión Privilegiada. Un Gestor CAE de un Operador CAE
/// externo que opera el Tenant beneficiario no es Administrador de ese Tenant, y un Tenant no ve ni cambia el plazo de
/// otro. La auditoría con Actor real la pone el interceptor genérico de auditoría (probado en integración, en
/// <c>AuditoriaConIdentidadDualTests</c>).
/// </summary>
public class PlazoRetencionDocumentosCommandsTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid TenantOperadorCaeExterno = Guid.NewGuid();

    // ── Plazo del Tenant propietario ──

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_fija_su_plazo()
    {
        var (tenant, tenants) = Tenants();
        var parametros = Parametros(tenant.Id);
        var unidad = new UnitOfWorkFalso();

        var resultado = await ManejadorTenant(tenant.Id, administradorDe: tenant.Id, tenants, parametros, unidad)
            .Handle(new FijarPlazoRetencionDocumentosCommand(2), default);

        resultado.EsExitoso.Should().BeTrue();
        parametros.AniosRetencionDocumentos.Should().Be(2);
        unidad.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_un_Operador_CAE_externo_no_fija_el_plazo_del_Tenant_beneficiario()
    {
        var (tenant, tenants) = Tenants();
        var parametros = Parametros(tenant.Id);
        var unidad = new UnitOfWorkFalso();

        // Administrador en su propio Tenant (el del Operador CAE externo), no en el Tenant beneficiario que opera.
        var resultado = await ManejadorTenant(tenant.Id, administradorDe: TenantOperadorCaeExterno, tenants, parametros, unidad)
            .Handle(new FijarPlazoRetencionDocumentosCommand(1), default);

        resultado.Error.Should().Be(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);
        parametros.AniosRetencionDocumentos.Should().Be(5);
        unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_no_se_fija_el_plazo_aunque_se_sea_Administrador_en_base()
    {
        var (tenant, tenants) = Tenants();
        var parametros = Parametros(tenant.Id);
        var unidad = new UnitOfWorkFalso();

        var resultado = await ManejadorTenant(tenant.Id, administradorDe: tenant.Id, tenants, parametros, unidad, SesionActiva())
            .Handle(new FijarPlazoRetencionDocumentosCommand(1), default);

        resultado.Error.Should().Be(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);
        parametros.AniosRetencionDocumentos.Should().Be(5);
        unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_Tenant_actual_ni_en_el_Tenant_de_plataforma_se_fija_el_plazo()
    {
        var (_, tenants) = Tenants();
        var parametros = Parametros(Guid.NewGuid());

        (await ManejadorTenant(null, Guid.NewGuid(), tenants, parametros, new UnitOfWorkFalso())
                .Handle(new FijarPlazoRetencionDocumentosCommand(1), default))
            .Error.Should().Be(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);

        var (plataforma, tenantsPlataforma) = Tenants(plataforma: true);
        var parametrosPlataforma = Parametros(plataforma.Id);
        (await ManejadorTenant(plataforma.Id, plataforma.Id, tenantsPlataforma, parametrosPlataforma, new UnitOfWorkFalso())
                .Handle(new FijarPlazoRetencionDocumentosCommand(1), default))
            .Error.Should().Be(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);
        parametrosPlataforma.AniosRetencionDocumentos.Should().Be(5);
    }

    [Fact]
    public async Task Un_Tenant_no_cambia_los_parametros_de_otro()
    {
        // Defensa aunque el filtro de Tenant fallara: el repositorio devuelve los parámetros de otro Tenant propietario.
        var (tenant, tenants) = Tenants();
        var ajenos = Parametros(Guid.NewGuid());
        var unidad = new UnitOfWorkFalso();

        var resultado = await ManejadorTenant(tenant.Id, tenant.Id, tenants, ajenos, unidad)
            .Handle(new FijarPlazoRetencionDocumentosCommand(1), default);

        resultado.EsFallido.Should().BeTrue();
        ajenos.AniosRetencionDocumentos.Should().Be(5);
        unidad.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    public void El_validador_del_Tenant_acota_de_uno_a_cinco(int anios, bool valido)
    {
        new FijarPlazoRetencionDocumentosCommandValidator()
            .TestValidate(new FijarPlazoRetencionDocumentosCommand(anios)).IsValid.Should().Be(valido);
    }

    // ── Plazo de un tipo de documento ──

    [Fact]
    public async Task El_Administrador_fija_y_quita_el_plazo_de_un_tipo_de_su_Tenant()
    {
        var (tenant, tenants) = Tenants();
        var tipo = TipoDe(tenant.Id);
        var unidad = new UnitOfWorkFalso();

        (await ManejadorTipo(tenant.Id, tenant.Id, tenants, tipo, unidad)
                .Handle(new FijarPlazoRetencionTipoDocumentoCommand(tipo.Id, 3), default))
            .EsExitoso.Should().BeTrue();
        tipo.AniosRetencionDocumentos.Should().Be(3);

        (await ManejadorTipo(tenant.Id, tenant.Id, tenants, tipo, unidad)
                .Handle(new FijarPlazoRetencionTipoDocumentoCommand(tipo.Id, null), default))
            .EsExitoso.Should().BeTrue();
        tipo.AniosRetencionDocumentos.Should().BeNull("sin plazo propio vuelve a heredar el del Tenant propietario");
        unidad.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Un_tipo_de_otro_Tenant_responde_como_inexistente_y_no_cambia()
    {
        var (tenant, tenants) = Tenants();
        var ajeno = TipoDe(Guid.NewGuid());
        var unidad = new UnitOfWorkFalso();

        var resultado = await ManejadorTipo(tenant.Id, tenant.Id, tenants, ajeno, unidad)
            .Handle(new FijarPlazoRetencionTipoDocumentoCommand(ajeno.Id, 1), default);

        resultado.Error.Should().Be(FijarPlazoRetencionTipoDocumentoCommandHandler.NoEncontrado);
        ajeno.AniosRetencionDocumentos.Should().BeNull();
        unidad.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_un_Operador_CAE_externo_no_fija_el_plazo_de_un_tipo()
    {
        var (tenant, tenants) = Tenants();
        var tipo = TipoDe(tenant.Id);

        var resultado = await ManejadorTipo(tenant.Id, TenantOperadorCaeExterno, tenants, tipo, new UnitOfWorkFalso())
            .Handle(new FijarPlazoRetencionTipoDocumentoCommand(tipo.Id, 1), default);

        resultado.Error.Should().Be(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);
        tipo.AniosRetencionDocumentos.Should().BeNull();
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_no_se_fija_el_plazo_de_un_tipo()
    {
        var (tenant, tenants) = Tenants();
        var tipo = TipoDe(tenant.Id);

        var resultado = await ManejadorTipo(tenant.Id, tenant.Id, tenants, tipo, new UnitOfWorkFalso(), SesionActiva())
            .Handle(new FijarPlazoRetencionTipoDocumentoCommand(tipo.Id, 1), default);

        resultado.Error.Should().Be(AutoridadPlazoRetencionDocumentos.SoloAdministradorDelTenant);
        tipo.AniosRetencionDocumentos.Should().BeNull();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(null, true)]
    public void El_validador_del_tipo_acota_de_uno_a_cinco_o_nulo(int? anios, bool valido)
    {
        new FijarPlazoRetencionTipoDocumentoCommandValidator()
            .TestValidate(new FijarPlazoRetencionTipoDocumentoCommand(Guid.NewGuid(), anios)).IsValid.Should().Be(valido);
    }

    // ── Dobles ──

    private static ISesionPrivilegiadaActual SesionActiva() =>
        new SesionPrivilegiadaFija(new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), default(CapacidadPrivilegio), null));

    private static (Tenant Tenant, TenantsQueryContextFalso Contexto) Tenants(bool plataforma = false)
    {
        var tenant = new Tenant("Tenant propietario de prueba");
        if (plataforma) tenant.MarcarComoPlataforma();
        var contexto = new TenantsQueryContextFalso();
        contexto.ListaTenants.Add(tenant);
        return (tenant, contexto);
    }

    private static ParametroSistema Parametros(Guid tenantId) => ConTenant(new ParametroSistema(30, 15), tenantId);

    private static TipoDocumento TipoDe(Guid tenantId) =>
        ConTenant(new TipoDocumento("Seguro de responsabilidad civil", 12, true, 1, AmbitoAplicacion.Empresa), tenantId);

    private static T ConTenant<T>(T entidad, Guid tenantId) where T : EntidadConTenant
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenantId);
        return entidad;
    }

    private static FijarPlazoRetencionDocumentosCommandHandler ManejadorTenant(
        Guid? tenantActual, Guid administradorDe, TenantsQueryContextFalso tenants, ParametroSistema parametros,
        UnitOfWorkFalso unidad, ISesionPrivilegiadaActual? sesion = null) =>
        new(sesion ?? new SesionPrivilegiadaFija(null), new TenantActualFijo(tenantActual), new CurrentUserServiceFalso(Usuario),
            new AdministradorFalso(administradorDe), tenants, new ParametrosFalsos(parametros), unidad);

    private static FijarPlazoRetencionTipoDocumentoCommandHandler ManejadorTipo(
        Guid? tenantActual, Guid administradorDe, TenantsQueryContextFalso tenants, TipoDocumento tipo,
        UnitOfWorkFalso unidad, ISesionPrivilegiadaActual? sesion = null) =>
        new(sesion ?? new SesionPrivilegiadaFija(null), new TenantActualFijo(tenantActual), new CurrentUserServiceFalso(Usuario),
            new AdministradorFalso(administradorDe), tenants, new TiposFalsos(tipo), unidad);

    private sealed class SesionPrivilegiadaFija(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => Task.FromResult(sesion);
    }

    private sealed class AdministradorFalso(Guid tenantDelAdministrador) : IAdministradorDelTenantPropietario
    {
        public Task<bool> EsAdministradorEnBaseAsync(Guid usuarioId, Guid tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantDelAdministrador == tenantId);
    }

    private sealed class TenantActualFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class ParametrosFalsos(ParametroSistema parametros) : IParametroSistemaRepository
    {
        public Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult(parametros);
        public void Agregar(ParametroSistema parametro) { }
    }

    private sealed class TiposFalsos(TipoDocumento tipo) : ITipoDocumentoRepository
    {
        public Task<TipoDocumento?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<TipoDocumento?>(id == tipo.Id ? tipo : null);
        public Task<TipoDocumento?> ObtenerPorIdActualizadoAsync(Guid id, CancellationToken cancellationToken = default) =>
            ObtenerPorIdAsync(id, cancellationToken);
        public Task<bool> ExisteConNombreAsync(string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public void Agregar(TipoDocumento tipoDocumento) { }
    }
}
