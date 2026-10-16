using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Visitas.Commands.EditarContactoVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Visitas;
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

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Contacto de la Visita 360 (decisión de producto, 2026-10-16) leído y escrito <b>como <c>cae_app_runtime</c></b>,
/// con los interceptores de sellado y de sesión RLS de producción y el <see cref="AlcanceDatosService"/> real (mismo
/// arnés que <c>NotaInternaDeEmpresaBajoRlsTests</c>). Dos Tenants propietarios con una Visita con contacto cada uno;
/// la sesión es del primero. Lo que se mide: el contacto de la Visita del Tenant ajeno no se lee ni se escribe, aunque
/// se pida con su identificador exacto y su versión. El control demuestra que, sin filtro global de EF, RLS oculta la
/// fila; la escritura pasa por el repositorio con su filtro global, así que su «no encontrada» no distingue entre ese
/// filtro y RLS (las dos barreras lo cumplen). La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class ContactoDeVisitaBajoRlsTests : IAsyncLifetime
{
    private const string ContactoPropio = "Persona del Centro propio";
    private const string ContactoAjeno = "Persona del Centro ajeno";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _visitaPropia;
    private Guid _visitaAjena;
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

        (_visitaPropia, _) = await SembrarVisitaAsync(_tenantSesion, "Planta Springfield", ContactoPropio);
        (_visitaAjena, _versionAjena) = await SembrarVisitaAsync(_tenantAjeno, "Planta Shelbyville", ContactoAjeno);

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión está de verdad bajo RLS y la Visita ajena existe con su contacto.</summary>
    [Fact]
    public async Task La_conexion_va_bajo_RLS_efectiva_y_la_visita_ajena_existe()
    {
        await using var runtime = CrearRuntime(Roles.Administrador);

        (await runtime.Visitas.IgnoreQueryFilters().CountAsync(v => v.Id == _visitaAjena)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar la Visita del Tenant ajeno");
        (await runtime.Visitas.IgnoreQueryFilters().CountAsync(v => v.Id == _visitaPropia)).Should().Be(1,
            "control positivo: la Visita del Tenant de la sesión sí se ve");
        (await _propietario.Visitas.IgnoreQueryFilters().Where(v => v.Id == _visitaAjena).Select(v => v.ContactoNombre).SingleAsync())
            .Should().Be(ContactoAjeno);
    }

    [Fact]
    public async Task El_detalle_de_una_visita_propia_trae_su_contacto_y_el_de_otro_Tenant_no_existe()
    {
        await using var runtime = CrearRuntime(Roles.Administrador);
        var handler = new ObtenerDetalleVisitaQueryHandler(runtime, runtime, runtime, runtime,
            Alcance(runtime, Roles.Administrador), runtime, PoliticaNotaInternaPruebas.Con(Roles.Administrador));

        var propia = await handler.Handle(new ObtenerDetalleVisitaQuery(_visitaPropia), CancellationToken.None);
        var ajena = await handler.Handle(new ObtenerDetalleVisitaQuery(_visitaAjena), CancellationToken.None);

        propia!.ContactoVisible.Should().BeTrue("control positivo: sin él, el null de la ajena no probaría nada");
        propia.ContactoNombre.Should().Be(ContactoPropio);
        ajena.Should().BeNull();
    }

    [Fact]
    public async Task Editar_el_contacto_de_una_visita_de_otro_Tenant_responde_no_encontrada_y_no_lo_cambia()
    {
        await using (var runtime = CrearRuntime(Roles.Administrador))
        {
            var handler = new EditarContactoVisitaCommandHandler(new VisitaRepository(runtime), Alcance(runtime, Roles.Administrador), runtime);

            var resultado = await handler.Handle(
                new EditarContactoVisitaCommand(_visitaAjena, _versionAjena, "Escrito desde otro Tenant", "600000000", null),
                CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
            resultado.Error.Codigo.Should().Be("Visita.NoEncontrada");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var ajena = await comprobacion.Visitas.IgnoreQueryFilters().SingleAsync(v => v.Id == _visitaAjena);
        ajena.ContactoNombre.Should().Be(ContactoAjeno);
        ajena.ContactoTelefono.Should().BeNull();
    }

    /// <summary>Sin filtro global ni repositorio: un UPDATE directo bajo RLS sobre la Visita ajena no toca ninguna fila.</summary>
    [Fact]
    public async Task Un_UPDATE_directo_bajo_RLS_no_alcanza_el_contacto_de_la_visita_ajena()
    {
        await using (var runtime = CrearRuntime(Roles.Administrador))
        {
            var filas = await runtime.Visitas.IgnoreQueryFilters()
                .Where(v => v.Id == _visitaAjena)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.ContactoNombre, "Escrito por UPDATE directo"));
            filas.Should().Be(0, "RLS no deja ver, y por tanto no deja actualizar, la fila del Tenant ajeno");
        }

        await using var comprobacion = CrearPropietarioLimpio();
        (await comprobacion.Visitas.IgnoreQueryFilters().Where(v => v.Id == _visitaAjena).Select(v => v.ContactoNombre).SingleAsync())
            .Should().Be(ContactoAjeno);
    }

    /// <summary>Control positivo de la escritura: la misma orden sobre la Visita propia sí guarda, bajo RLS.</summary>
    [Fact]
    public async Task Editar_el_contacto_de_una_visita_propia_lo_persiste_sin_tocar_el_del_Centro()
    {
        Guid version;
        await using (var lectura = CrearPropietarioLimpio())
            version = await lectura.Visitas.IgnoreQueryFilters().Where(v => v.Id == _visitaPropia).Select(v => v.Version).SingleAsync();

        await using (var runtime = CrearRuntime(Roles.Administrador))
        {
            var handler = new EditarContactoVisitaCommandHandler(new VisitaRepository(runtime), Alcance(runtime, Roles.Administrador), runtime);

            var resultado = await handler.Handle(
                new EditarContactoVisitaCommand(_visitaPropia, version, "Otra persona", "954555010", "otra@centro.example"),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var visita = await comprobacion.Visitas.IgnoreQueryFilters().SingleAsync(v => v.Id == _visitaPropia);
        visita.ContactoNombre.Should().Be("Otra persona");
        visita.ContactoTelefono.Should().Be("954555010");
        visita.ContactoCorreo.Should().Be("otra@centro.example");
        (await comprobacion.Centros.IgnoreQueryFilters().Where(c => c.Id == visita.CentroId).Select(c => c.Contacto).SingleAsync())
            .Should().Be(ContactoPropio, "el contacto de la Visita es propio: cambiarlo no cambia el del Centro");
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto, string rol) => new(
        contexto, new CurrentUserServiceFalso(Guid.NewGuid(), rol, tenantOrigenId: _tenantSesion),
        _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task<(Guid Id, Guid Version)> SembrarVisitaAsync(Guid tenantId, string nombreCentro, string contactoCentro)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
        var cliente = Empresa.CrearComoCliente($"Cliente de {nombreCentro}", "B12345674", false, null, null);
        var empresa = new Empresa($"Empresa de {nombreCentro}", "B10380186");
        _propietario.Empresas.AddRange(cliente, empresa);
        await _propietario.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, nombreCentro, contacto: contactoCentro);
        _propietario.Centros.Add(centro);
        await _propietario.SaveChangesAsync();

        var visita = new Visita(centro.Id, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 3), null);
        visita.RellenarContactoDesdeCentro(centro.Contacto);
        _propietario.Visitas.Add(visita);
        await _propietario.SaveChangesAsync();
        return (visita.Id, visita.Version);
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
