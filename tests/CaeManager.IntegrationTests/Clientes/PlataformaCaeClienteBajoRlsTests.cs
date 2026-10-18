using CaeManager.Application.Clientes.Queries.RevelarContrasenaAccesoPlataformaCae;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Clientes;

/// <summary>
/// Caja «Plataforma CAE» de Cliente 360 bajo la base real (decisión del 2026-10-18): la plataforma de cada Cliente
/// empresarial y el acceso de cada Empresa a ella, leídos <b>como <c>cae_app_runtime</c></b> con los interceptores de
/// sellado y de sesión RLS de producción (mismo arnés que <c>NotaInternaDeEmpresaBajoRlsTests</c>). Dos Tenants
/// propietarios, cada uno con su Cliente empresarial, su Empresa propia, su plataforma y su acceso; la sesión es del
/// primero. Lo que se mide: RLS oculta las filas del Tenant ajeno aunque se ignore el filtro global de EF; usuario y
/// contraseña se guardan cifrados en la columna; «Revelar» sobre los identificadores del Tenant ajeno no entrega nada
/// ni deja rastro; y el rol de soporte no tiene SELECT sobre los accesos. La siembra va como propietario de la base,
/// porque no es lo que se mide. Un único proveedor de Data Protection para todos los contextos, para que lo cifrado
/// por uno lo descifre otro.
/// </summary>
public class PlataformaCaeClienteBajoRlsTests : IAsyncLifetime
{
    private const string UsuarioPropio = "b12345674-propio";
    private const string ContrasenaPropia = "s3cr3ta-del-portal-propio";
    private const string ContrasenaAjena = "s3cr3ta-del-portal-ajeno";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly IDataProtectionProvider _proteccion = new EphemeralDataProtectionProvider();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _clientePropio;
    private Guid _empresaPropia;
    private Guid _clienteAjeno;
    private Guid _empresaAjena;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, _proteccion, tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario de la sesión");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        _tenantSesion = tenantSesion.Id;
        _tenantAjeno = tenantAjeno.Id;

        (_clientePropio, _empresaPropia) = await SembrarAsync(_tenantSesion, "Springfield", UsuarioPropio, ContrasenaPropia);
        (_clienteAjeno, _empresaAjena) = await SembrarAsync(_tenantAjeno, "Shelbyville", "usuario-ajeno", ContrasenaAjena);

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento y de RLS: sin filtro global de EF, solo RLS puede ocultar las filas ajenas.</summary>
    [Fact]
    public async Task Bajo_RLS_el_Tenant_de_la_sesion_ve_su_plataforma_y_su_acceso_y_no_los_del_Tenant_ajeno()
    {
        await using var runtime = CrearRuntime(Roles.GestorCae);

        (await runtime.PlataformasCaeClienteEmpresarial.IgnoreQueryFilters().CountAsync(p => p.ClienteEmpresarialId == _clienteAjeno))
            .Should().Be(0, "la plataforma del Tenant ajeno no la ve RLS");
        (await runtime.AccesosPlataformaCaeEmpresa.IgnoreQueryFilters().CountAsync(a => a.EmpresaId == _empresaAjena))
            .Should().Be(0, "el acceso del Tenant ajeno no lo ve RLS");

        (await runtime.PlataformasCaeClienteEmpresarial.IgnoreQueryFilters().CountAsync(p => p.ClienteEmpresarialId == _clientePropio))
            .Should().Be(1, "control positivo: la plataforma del Tenant de la sesión sí se ve");
        (await runtime.AccesosPlataformaCaeEmpresa.IgnoreQueryFilters().CountAsync(a => a.EmpresaId == _empresaPropia))
            .Should().Be(1, "control positivo: el acceso del Tenant de la sesión sí se ve");
        (await _propietario.AccesosPlataformaCaeEmpresa.IgnoreQueryFilters().CountAsync(a => a.EmpresaId == _empresaAjena))
            .Should().Be(1, "las filas ajenas existen: el cero de arriba no es una base vacía");
    }

    [Fact]
    public async Task Usuario_y_contrasena_se_guardan_cifrados_y_el_contexto_los_descifra()
    {
        var columnas = await _propietario.Database
            .SqlQueryRaw<ColumnasCifradas>(
                """SELECT "Usuario" AS "Usuario", "Contrasena" AS "Contrasena" FROM "AccesosPlataformaCaeEmpresa" WHERE "EmpresaId" = {0}""",
                _empresaPropia)
            .SingleAsync();

        columnas.Contrasena.Should().NotBeNullOrEmpty();
        columnas.Contrasena.Should().NotContain(ContrasenaPropia, "la contraseña no puede quedar en claro en la base");
        columnas.Usuario.Should().NotBeNullOrEmpty();
        columnas.Usuario.Should().NotContain(UsuarioPropio, "el usuario del portal tampoco");

        await using var runtime = CrearRuntime(Roles.GestorCae);
        var acceso = await runtime.AccesosPlataformaCaeEmpresa.SingleAsync(a => a.EmpresaId == _empresaPropia);
        acceso.Contrasena.Should().Be(ContrasenaPropia, "control positivo: la aplicación sí la descifra");
        acceso.Usuario.Should().Be(UsuarioPropio);
    }

    [Fact]
    public async Task Revelar_sobre_el_Cliente_empresarial_y_la_Empresa_de_otro_Tenant_no_entrega_nada_ni_audita()
    {
        await using var runtime = CrearRuntime(Roles.GestorCae);
        var registro = new RegistroAccesoDatoSensibleQueAnota();
        var handler = new RevelarContrasenaAccesoPlataformaCaeQueryHandler(runtime, Alcance(runtime, Roles.GestorCae), registro);

        var ajena = await handler.Handle(
            new RevelarContrasenaAccesoPlataformaCaeQuery(_clienteAjeno, _empresaAjena), CancellationToken.None);

        ajena.Should().BeNull();
        registro.Registrados.Should().BeEmpty("lo que no se entrega no se audita como leído");
    }

    [Fact]
    public async Task El_rol_de_soporte_no_tiene_SELECT_sobre_los_accesos_y_si_sobre_la_plataforma()
    {
        var sobreAccesos = await _propietario.Database
            .SqlQueryRaw<bool>("""SELECT has_table_privilege('cae_app_soporte', '"AccesosPlataformaCaeEmpresa"', 'SELECT') AS "Value" """)
            .SingleAsync();
        var sobrePlataforma = await _propietario.Database
            .SqlQueryRaw<bool>("""SELECT has_table_privilege('cae_app_soporte', '"PlataformasCaeClienteEmpresarial"', 'SELECT') AS "Value" """)
            .SingleAsync();

        sobreAccesos.Should().BeFalse("las credenciales de portal de un Tenant no las lee Soporte TALVEG");
        sobrePlataforma.Should().BeTrue("control positivo: la plataforma (sin secretos) sí entra en la lectura de soporte");
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto, string rol) => new(
        contexto, new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenantOrigenId: _tenantSesion),
        _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task<(Guid ClienteEmpresarialId, Guid EmpresaId)> SembrarAsync(
        Guid tenantId, string sufijo, string usuario, string contrasena)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var cliente = Empresa.CrearComoCliente($"Cliente empresarial {sufijo}", "B12345674", false, null, null);
        var empresa = Empresa.CrearComoCliente($"Montajes {sufijo}", "B12345674", false, null, null);
        _propietario.Empresas.AddRange(cliente, empresa);
        await _propietario.SaveChangesAsync();

        var acceso = new AccesoPlataformaCaeEmpresa(cliente.Id, empresa.Id);
        acceso.CambiarUsuario(usuario);
        acceso.CambiarContrasena(contrasena, null, new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc));
        _propietario.PlataformasCaeClienteEmpresarial.Add(
            new PlataformaCaeClienteEmpresarial(cliente.Id, "Nalanda", "https://portal.example/acceso", "CLI-" + sufijo));
        _propietario.AccesosPlataformaCaeEmpresa.Add(acceso);
        await _propietario.SaveChangesAsync();
        return (cliente.Id, empresa.Id);
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
        return new CaeManagerDbContext(opciones, _proteccion, _tenantDeLaPeticion);
    }

    private sealed record ColumnasCifradas(string? Usuario, string? Contrasena);

    private sealed class RegistroAccesoDatoSensibleQueAnota : IRegistroAccesoDatoSensibleService
    {
        public List<(string EntidadTipo, Guid EntidadId)> Registrados { get; } = [];

        public Task RegistrarAsync(string entidadTipo, Guid entidadId, CancellationToken cancellationToken = default)
        {
            Registrados.Add((entidadTipo, entidadId));
            return Task.CompletedTask;
        }
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
