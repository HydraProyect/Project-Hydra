using System.Reflection;
using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Auto-concesión: el acto explícito que hace ejercitable la ceremonia de
/// apertura sin abrir todavía la semántica de delegar privilegios a terceros.
///
/// <b>La garantía principal de este comando no se comprueba, se construye.</b>
/// No existe ningún parámetro para el beneficiario: sale de la sesión. Por eso
/// "yo → otro" no es un caso rechazado sino un caso <i>irrepresentable</i>, y el
/// test que lo afirma mira la forma del comando, no su comportamiento — un test
/// de comportamiento solo podría probar los beneficiarios que se le ocurran al
/// que lo escribe.
/// </summary>
public class AutoConcederPrivilegioTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        contexto.Tenants.Add(CrearTenantDePlataforma());
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── La garantía estructural ────────────────────────────────────────────

    [Fact]
    public void El_comando_no_admite_beneficiario_asi_que_conceder_a_otro_es_irrepresentable()
    {
        // Si alguien añadiera un parámetro de usuario a este comando, dejaría de
        // ser auto-concesión y pasaría a ser la operación genérica de conceder
        // —quién concede, a quién, qué capacidad, cómo se revoca— que es un
        // contrato propio y que además exigiría relajar el WITH CHECK de RLS.
        // Este test hace que ese cambio tenga que ser deliberado.
        var parametros = typeof(AutoConcederPrivilegioCommand)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.Name).ToList();

        parametros.Should().BeEquivalentTo(["TenantObjetivoId", "Capacidad", "DiasDeVigencia"],
            "el beneficiario sale de la sesión; en cuanto sea un parámetro, esto deja de ser auto-concesión");

        typeof(AutoConcederPrivilegioCommand).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Usuario", StringComparison.OrdinalIgnoreCase));
    }

    // ── Comportamiento ─────────────────────────────────────────────────────

    [Fact]
    public async Task El_usuario_se_concede_a_si_mismo_y_queda_registrada_la_autoria()
    {
        var resultado = await EjecutarAsync();

        resultado.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var concesion = await contexto.ConcesionesPrivilegio
            .Include(c => c.TenantsAlcanzados)
            .SingleAsync();

        concesion.UsuarioPlataformaId.Should().Be(_tecnico, "yo → yo");
        concesion.ConcedidaPorUsuarioId.Should().Be(_tecnico,
            "la autoría se registra desde el primer día, aunque hoy coincida con el beneficiario");
        concesion.Capacidad.Should().Be(CapacidadPrivilegio.SoporteLectura);
        concesion.EsAlcanceGlobal.Should().BeFalse("una auto-concesión nunca es global");
        concesion.TenantsAlcanzados.Should().ContainSingle()
            .Which.TenantId.Should().Be(_tenantVisitado);
    }

    [Fact]
    public async Task La_concesion_creada_habilita_de_verdad_la_apertura()
    {
        // El circuito completo, que es la razón de que esta operación entre en
        // F2b-6: sin ella la ceremonia quedaba formalmente implementada y
        // operacionalmente huérfana.
        var concesionId = (await EjecutarAsync()).Valor;

        await using var contexto = CrearContexto();
        var concesion = await contexto.ConcesionesPrivilegio
            .Include(c => c.TenantsAlcanzados)
            .SingleAsync(c => c.Id == concesionId);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, _tenantVisitado, "Reproducir la incidencia", DateTime.UtcNow, TimeSpan.FromDays(1));

        abrir.Should().NotThrow("auto-concederse y abrir tienen que encadenar");
    }

    [Fact]
    public async Task Sin_autoridad_de_plataforma_no_se_concede_nada()
    {
        var resultado = await EjecutarAsync(tenantOrigen: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.NoAutorizado");
        await NoHayNingunaConcesionAsync();
    }

    [Fact]
    public async Task Sin_doble_factor_no_se_concede_nada()
    {
        // La ceremonia se comprueba en cada paso que CREA autoridad, no solo al
        // abrir: si no, quedaría un camino para dejar la concesión preparada sin
        // segundo factor y usarla después.
        var resultado = await EjecutarAsync(dobleFactor: false);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.SinDobleFactor");
        await NoHayNingunaConcesionAsync();
    }

    [Fact]
    public async Task Nadie_se_concede_privilegio_sobre_su_propio_tenant()
    {
        var resultado = await EjecutarAsync(tenantObjetivo: _tenantPlataforma);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.NoAutorizado");
        await NoHayNingunaConcesionAsync();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Domain.Common.Result<Guid>> EjecutarAsync(
        Guid? tenantObjetivo = null, Guid? tenantOrigen = null, bool dobleFactor = true)
    {
        await using var contexto = CrearContexto();

        var currentUser = new CurrentUserServiceFalso(
            _tecnico, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, dobleFactor);

        var handler = new AutoConcederPrivilegioCommandHandler(
            new PlataformaWriter(contexto),
            new AutorizacionAperturaSesionPorTenantDePlataforma(contexto, currentUser),
            currentUser,
            contexto);

        return await handler.Handle(
            new AutoConcederPrivilegioCommand(
                tenantObjetivo ?? _tenantVisitado, CapacidadPrivilegio.SoporteLectura, DiasDeVigencia: 7),
            CancellationToken.None);
    }

    private async Task NoHayNingunaConcesionAsync()
    {
        await using var contexto = CrearContexto();
        (await contexto.ConcesionesPrivilegio.CountAsync()).Should().Be(0);
    }

    private Domain.Tenants.Tenant CrearTenantDePlataforma()
    {
        var tenant = new Domain.Tenants.Tenant("Plataforma de pruebas");
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!
            .SetValue(tenant, _tenantPlataforma);
        tenant.MarcarComoPlataforma();
        return tenant;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantPlataforma };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
