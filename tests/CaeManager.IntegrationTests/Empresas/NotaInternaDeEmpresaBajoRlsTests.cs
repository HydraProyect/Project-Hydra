using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.GuardarNotaInternaEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// La «Nota interna» de Empresa 360 (<c>Empresa.Notas</c>) leída y escrita <b>como <c>cae_app_runtime</c></b>, con los
/// interceptores de sellado y de sesión RLS de producción y el <see cref="AlcanceDatosService"/> real (mismo arnés que
/// <c>DesgloseDocumentalDeEmpresasBajoRlsTests</c>). Dos Tenants propietarios con una Empresa con nota cada uno; la
/// sesión es del primero. Lo que se mide: la Empresa del Tenant ajeno no se lee ni se escribe por Id, aunque se pida
/// con su identificador exacto. El control demuestra que, sin filtro global de EF, RLS la oculta en lectura; la
/// escritura pasa por el repositorio con su filtro global, así que su «no encontrada» no distingue entre ese filtro
/// y RLS (las dos barreras lo cumplen). La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class NotaInternaDeEmpresaBajoRlsTests : IAsyncLifetime
{
    private const string NotaPropia = "Nota del Tenant de la sesión.";
    private const string NotaAjena = "Nota del Tenant ajeno.";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _empresaPropia;
    private Guid _empresaAjena;
    private Guid _versionAjena;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        (_empresaPropia, _) = await SembrarEmpresaAsync(_tenantSesion, "Montajes Springfield", NotaPropia);
        (_empresaAjena, _versionAjena) = await SembrarEmpresaAsync(_tenantAjeno, "Montajes Shelbyville", NotaAjena);

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión está de verdad bajo RLS y la Empresa ajena existe.</summary>
    [Fact]
    public async Task La_conexion_va_bajo_RLS_efectiva_y_la_empresa_ajena_existe()
    {
        await using var runtime = CrearRuntime(Roles.Administrador);

        (await runtime.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _empresaAjena)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar la Empresa del Tenant ajeno");
        (await runtime.Empresas.IgnoreQueryFilters().CountAsync(e => e.Id == _empresaPropia)).Should().Be(1,
            "control positivo: la Empresa del Tenant de la sesión sí se ve");
        (await _propietario.Empresas.IgnoreQueryFilters().Where(e => e.Id == _empresaAjena).Select(e => e.Notas).SingleAsync())
            .Should().Be(NotaAjena);
    }

    [Fact]
    public async Task La_ficha_de_una_empresa_propia_trae_su_nota_y_la_de_otro_Tenant_no_existe()
    {
        await using var runtime = CrearRuntime(Roles.Administrador);
        var handler = new ObtenerEmpresaPorIdQueryHandler(runtime, Alcance(runtime, Roles.Administrador), PoliticaNotaInternaPruebas.Con(Roles.Administrador));

        var propia = await handler.Handle(new ObtenerEmpresaPorIdQuery(_empresaPropia), CancellationToken.None);
        var ajena = await handler.Handle(new ObtenerEmpresaPorIdQuery(_empresaAjena), CancellationToken.None);

        propia!.Notas.Should().Be(NotaPropia, "control positivo: sin él, el null de la ajena no probaría nada");
        propia.NotaInternaVisible.Should().BeTrue();
        ajena.Should().BeNull();
    }

    [Fact]
    public async Task Guardar_la_nota_de_una_empresa_de_otro_Tenant_responde_no_encontrada_y_no_la_cambia()
    {
        await using (var runtime = CrearRuntime(Roles.Administrador))
        {
            var handler = new GuardarNotaInternaEmpresaCommandHandler(
                new EmpresaRepository(runtime), Alcance(runtime, Roles.Administrador), runtime);

            var resultado = await handler.Handle(
                new GuardarNotaInternaEmpresaCommand(_empresaAjena, "Escrita desde otro Tenant.", _versionAjena), CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("Empresa.NoEncontrada");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Empresas.IgnoreQueryFilters().Where(e => e.Id == _empresaAjena).Select(e => e.Notas).SingleAsync())
            .Should().Be(NotaAjena);
    }

    /// <summary>Control positivo de la escritura: la misma orden sobre la Empresa propia sí guarda, bajo RLS.</summary>
    [Fact]
    public async Task Guardar_la_nota_de_una_empresa_propia_la_persiste()
    {
        Guid version;
        await using (var lectura = CrearPropietarioLimpio())
            version = await lectura.Empresas.IgnoreQueryFilters().Where(e => e.Id == _empresaPropia).Select(e => e.Version).SingleAsync();

        await using (var runtime = CrearRuntime(Roles.Administrador))
        {
            var handler = new GuardarNotaInternaEmpresaCommandHandler(
                new EmpresaRepository(runtime), Alcance(runtime, Roles.Administrador), runtime);

            var resultado = await handler.Handle(
                new GuardarNotaInternaEmpresaCommand(_empresaPropia, "Nota nueva.", version), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Empresas.IgnoreQueryFilters().Where(e => e.Id == _empresaPropia).Select(e => e.Notas).SingleAsync())
            .Should().Be("Nota nueva.");
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto, string rol) => new(
        contexto, new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenantOrigenId: _tenantSesion),
        _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task<(Guid Id, Guid Version)> SembrarEmpresaAsync(Guid tenantId, string razonSocial, string nota)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var empresa = Empresa.CrearComoCliente(razonSocial, "B12345674", false, nota, null);
        _propietario.Empresas.Add(empresa);
        await _propietario.SaveChangesAsync();
        return (empresa.Id, empresa.Version);
    }

    private CaeManagerDbContext CrearPropietarioLimpio()
    {
        var tenant = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private CaeManagerDbContext CrearRuntime(string rol)
    {
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenantOrigenId: _tenantSesion);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _tenantDeLaPeticion);
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

    private sealed class SesionPrivilegiadaAusente : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }
}
