using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Commands.FijarNotaContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Contactos;
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

namespace CaeManager.IntegrationTests.Contactos;

/// <summary>
/// La nota fija del contacto leída y escrita <b>como <c>cae_app_runtime</c></b>, con los interceptores de sellado y de
/// sesión RLS de producción y el <see cref="AlcanceDatosService"/> real (mismo arnés que
/// <c>NotaInternaDeEmpresaBajoRlsTests</c>). Dos Tenants propietarios con un Cliente empresarial y un contacto con nota
/// cada uno; la sesión es del primero. El contacto del Tenant ajeno no se lee ni se escribe, aunque se pida con sus
/// identificadores exactos.
/// </summary>
public class NotaContactoAgendaBajoRlsTests : IAsyncLifetime
{
    private const string NotaPropia = "Nota del Tenant de la sesión.";
    private const string NotaAjena = "Nota del Tenant ajeno.";
    private const string CorreoAjeno = "ajeno@cliente.test";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _autor = Guid.NewGuid();
    private CaeManagerDbContext _propietario = null!;
    private TenantActualDeLaPeticion _tenantDeLaPeticion = null!;
    private Guid _tenantSesion;
    private (Guid Cliente, Guid Contacto) _propio;
    private (Guid Cliente, Guid Contacto) _ajeno;

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

        _propio = await SembrarAsync(tenantSesion.Id, "Montajes Springfield", "propio@cliente.test", NotaPropia);
        _ajeno = await SembrarAsync(tenantAjeno.Id, "Montajes Shelbyville", CorreoAjeno, NotaAjena);

        _tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantSesion);
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    /// <summary>Control del instrumento: la conexión está de verdad bajo RLS y el contacto ajeno existe.</summary>
    [Fact]
    public async Task La_conexion_va_bajo_RLS_efectiva_y_el_contacto_ajeno_existe()
    {
        await using var runtime = CrearRuntime();

        (await runtime.ContactosAgenda.IgnoreQueryFilters().CountAsync(c => c.Id == _ajeno.Contacto)).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar el contacto del Tenant ajeno");
        (await runtime.ContactosAgenda.IgnoreQueryFilters().CountAsync(c => c.Id == _propio.Contacto)).Should().Be(1,
            "control positivo: el contacto del Tenant de la sesión sí se ve");
        (await LeerNotaComoPropietarioAsync(_ajeno.Contacto)).Should().Be(NotaAjena);
    }

    [Fact]
    public async Task La_nota_de_un_contacto_propio_se_lee_y_la_de_otro_Tenant_no_existe()
    {
        await using var runtime = CrearRuntime();
        var handler = new ObtenerNotaContactoQueryHandler(runtime, Alcance(runtime), PoliticaNotaInternaPruebas.Con(Roles.Administrador));

        var propia = await handler.Handle(
            new ObtenerNotaContactoQuery(TipoPropietarioAgenda.Cliente, _propio.Cliente, "propio@cliente.test"), CancellationToken.None);
        var ajena = await handler.Handle(
            new ObtenerNotaContactoQuery(TipoPropietarioAgenda.Cliente, _ajeno.Cliente, CorreoAjeno), CancellationToken.None);

        propia!.Notas.Should().Be(NotaPropia, "control positivo: sin él, el null de la ajena no probaría nada");
        ajena.Should().BeNull();
    }

    [Fact]
    public async Task Fijar_la_nota_de_un_contacto_de_otro_Tenant_falla_y_no_la_cambia()
    {
        await using (var runtime = CrearRuntime())
        {
            var resultado = await Handler(runtime).Handle(
                new FijarNotaContactoAgendaCommand(TipoPropietarioAgenda.Cliente, _ajeno.Cliente, _ajeno.Contacto, "Escrita desde otro Tenant."),
                CancellationToken.None);

            resultado.EsFallido.Should().BeTrue();
        }

        (await LeerNotaComoPropietarioAsync(_ajeno.Contacto)).Should().Be(NotaAjena);
    }

    /// <summary>Control positivo de la escritura: la misma orden sobre el contacto propio sí guarda, bajo RLS.</summary>
    [Fact]
    public async Task Fijar_la_nota_de_un_contacto_propio_la_persiste_con_su_autor()
    {
        await using (var runtime = CrearRuntime())
        {
            var resultado = await Handler(runtime).Handle(
                new FijarNotaContactoAgendaCommand(TipoPropietarioAgenda.Cliente, _propio.Cliente, _propio.Contacto, "Nota nueva."),
                CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue();
        }

        await using var comprobacion = CrearPropietarioLimpio();
        var contacto = await comprobacion.ContactosAgenda.IgnoreQueryFilters().SingleAsync(c => c.Id == _propio.Contacto);
        contacto.Notas.Should().Be("Nota nueva.");
        contacto.NotasAutorUsuarioId.Should().Be(_autor);
    }

    private FijarNotaContactoAgendaCommandHandler Handler(CaeManagerDbContext runtime) => new(
        new ContactoAgendaRepository(runtime), Alcance(runtime), Usuario(), runtime);

    private CurrentUserServiceFalso Usuario() => new(_autor, Roles.Administrador, tenantOrigenId: _tenantSesion);

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto) => new(
        contexto, Usuario(), _tenantDeLaPeticion, new SesionPrivilegiadaAusente());

    private async Task<(Guid Cliente, Guid Contacto)> SembrarAsync(Guid tenantId, string razonSocial, string email, string nota)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        var cliente = Empresa.CrearComoCliente(razonSocial, "B12345674", false, null, null);
        _propietario.Empresas.Add(cliente);
        await _propietario.SaveChangesAsync();

        var contacto = ContactoAgenda.DeCliente(cliente.Id, "Interlocutor", email, notas: nota);
        _propietario.ContactosAgenda.Add(contacto);
        await _propietario.SaveChangesAsync();
        return (cliente.Id, contacto.Id);
    }

    private async Task<string?> LeerNotaComoPropietarioAsync(Guid contactoId)
    {
        await using var comprobacion = CrearPropietarioLimpio();
        return await comprobacion.ContactosAgenda.IgnoreQueryFilters()
            .Where(c => c.Id == contactoId).Select(c => c.Notas).SingleAsync();
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

    private CaeManagerDbContext CrearRuntime()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(_tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(_tenantDeLaPeticion, new SinClienteActivo(), Usuario(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
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
