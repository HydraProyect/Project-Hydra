using CaeManager.Application.Plataforma.Queries.PuedeInicializarPlataforma;
using CaeManager.Application.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La puerta por la que la identidad raíz ejerce el acto fundacional.
///
/// <para>
/// Existe porque A2 construyó el mecanismo y <b>nada lo invocaba</b>: el
/// despliegue designaba la raíz, el comando funcionaba, y la concesión
/// fundacional no se creaba nunca. El síntoma apareció en E2E —el botón "Nueva
/// delegación" no llegaba a mostrarse— y no en integración, porque la suite no
/// ejercita la UI.
/// </para>
///
/// <para>
/// <b>Descubrimiento y autoridad son cosas distintas</b>, y esa separación es lo
/// que se prueba aquí: la consulta responde "¿enseño la puerta?" y el comando
/// "¿puedo cruzarla?". Que la primera se quede desfasada no es un bypass.
/// </para>
/// </summary>
public class PuertaDelActoFundacionalTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _raiz = Guid.NewGuid();
    private readonly Guid _otroUsuario = Guid.NewGuid();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var plataforma = new Domain.Tenants.Tenant("Plataforma de pruebas");
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!
            .SetValue(plataforma, _tenantPlataforma);
        plataforma.MarcarComoPlataforma();
        contexto.Tenants.Add(plataforma);

        contexto.EstadoBootstrapPlataforma.Add(
            EstadoBootstrapPlataforma.Designar(_raiz, DateTime.UtcNow));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Descubrimiento ─────────────────────────────────────────────────────

    [Fact]
    public async Task La_raiz_ve_la_puerta_mientras_el_bootstrap_esta_pendiente()
        => (await DescubrirAsync(_raiz)).Should().BeTrue();

    [Fact]
    public async Task Consumido_el_bootstrap_la_puerta_desaparece()
    {
        await using (var contexto = CrearContexto())
        {
            var estado = await contexto.EstadoBootstrapPlataforma.SingleAsync();
            estado.Consumir(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        (await DescubrirAsync(_raiz)).Should().BeFalse(
            "el acto es único: una puerta que siguiera visible invitaría a intentarlo otra vez");
    }

    [Fact]
    public async Task Quien_no_es_la_raiz_no_ve_la_puerta()
        => (await DescubrirAsync(_otroUsuario)).Should().BeFalse();

    /// <summary>
    /// La distinción que A2 introdujo: la raíz es una <b>persona</b>, no una
    /// organización. Pertenecer al tenant de plataforma no da acceso a la puerta.
    /// </summary>
    [Fact]
    public async Task Pertenecer_al_tenant_de_plataforma_no_basta_para_ver_la_puerta()
        => (await DescubrirAsync(_otroUsuario, tenantOrigen: _tenantPlataforma)).Should().BeFalse();

    [Fact]
    public async Task Sin_usuario_identificado_no_se_ve_la_puerta()
        => (await DescubrirAsync(usuario: null)).Should().BeFalse();

    // ── La carrera: descubrimiento desfasado no es bypass ──────────────────

    /// <summary>
    /// El principio que sostiene toda la separación: la consulta puede decir que
    /// sí y el comando denegar igualmente, porque entre una y otro el bootstrap
    /// pudo consumirse. Si esto fallara, la consulta sería un segundo camino de
    /// autoridad.
    /// </summary>
    [Fact]
    public async Task Si_el_bootstrap_se_consume_tras_el_descubrimiento_el_comando_deniega()
    {
        (await DescubrirAsync(_raiz)).Should().BeTrue("la puerta estaba visible");

        await using (var contexto = CrearContexto())
        {
            var estado = await contexto.EstadoBootstrapPlataforma.SingleAsync();
            estado.Consumir(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarActoAsync(_raiz);

        resultado.EsFallido.Should().BeTrue(
            "la autoridad final es el comando; el descubrimiento solo decide si se enseña el botón");

        await using var comprobacion = CrearContexto();
        (await comprobacion.ConcesionesPrivilegio.CountAsync()).Should().Be(0);
    }

    // ── Auditoría: ya la produce el interceptor, y se comprueba ────────────

    /// <summary>
    /// El acto fundacional no necesita auditoría propia: el interceptor audita
    /// toda entidad de <c>CaeManager.Domain</c>, así que la creación de la
    /// concesión y el consumo del estado dejan sus dos entradas <b>en el mismo
    /// SaveChanges</b> que las escribe. Se comprueba en vez de suponerlo.
    /// </summary>
    [Fact]
    public async Task El_acto_fundacional_deja_rastro_de_auditoria_de_sus_dos_mitades()
    {
        (await EjecutarActoAsync(_raiz)).EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var entradas = await contexto.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(ConcesionPrivilegio)
                        || r.EntidadTipo == nameof(EstadoBootstrapPlataforma))
            .ToListAsync();

        entradas.Should().Contain(r => r.EntidadTipo == nameof(ConcesionPrivilegio) && r.Accion == "Creado");
        entradas.Should().Contain(r => r.EntidadTipo == nameof(EstadoBootstrapPlataforma) && r.Accion == "Modificado");
    }

    [Fact]
    public async Task Un_acto_denegado_no_deja_ni_concesion_ni_rastro()
    {
        (await EjecutarActoAsync(_otroUsuario)).EsFallido.Should().BeTrue();

        await using var contexto = CrearContexto();
        (await contexto.ConcesionesPrivilegio.CountAsync()).Should().Be(0);
        (await contexto.RegistrosAuditoria.CountAsync(r => r.EntidadTipo == nameof(ConcesionPrivilegio)))
            .Should().Be(0);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<bool> DescubrirAsync(Guid? usuario, Guid? tenantOrigen = null)
    {
        await using var contexto = CrearContexto();
        var currentUser = new CurrentUserServiceFalso(usuario, rol: null, tenantOrigenId: tenantOrigen);

        var handler = new PuedeInicializarPlataformaQueryHandler(
            new RaizBootstrapPorIdentidadDesignada(contexto), currentUser);

        return await handler.Handle(new PuedeInicializarPlataformaQuery(), CancellationToken.None);
    }

    private async Task<Domain.Common.Result<Guid>> EjecutarActoAsync(Guid usuario)
    {
        await using var contexto = CrearContexto();
        var currentUser = new CurrentUserServiceFalso(usuario, rol: null, tenantOrigenId: _tenantPlataforma);

        var handler = new Application.Plataforma.Commands.AutoConcederPrivilegio.AutoConcederPrivilegioCommandHandler(
            new PlataformaWriter(contexto),
            new AutorizacionAutoConcesionPorMatriz(
                new RaizBootstrapPorIdentidadDesignada(contexto), contexto),
            contexto, currentUser, contexto);

        return await handler.Handle(
            new Application.Plataforma.Commands.AutoConcederPrivilegio.AutoConcederPrivilegioCommand(
                Guid.Empty, CapacidadPrivilegio.AdminPlataforma, DiasDeVigencia: 1),
            CancellationToken.None);
    }

    /// <summary>
    /// Con el interceptor de auditoría montado, y en el MISMO orden que
    /// producción —auditoría primero, sellado después—. Sin él, el test de
    /// auditoría no observaba el fenómeno que decía comprobar: no encontraba
    /// entradas porque nadie las escribía en el arnés, no porque el sistema no
    /// las produzca.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid? actorUsuarioId = null)
    {
        // Con tenant resuelto, como en produccion: RegistroAuditoria lo exige, y
        // ahi siempre sale del claim de la sesion. El acto fundacional es global,
        // pero su AUDITORIA se archiva en el tenant desde el que se ejerce.
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantPlataforma };
        var actor = new ActorFijo(ActorAuditoria.Normal(actorUsuarioId ?? _raiz));

        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>Actor de auditoría fijo: aquí solo interesa que el interceptor tenga a quién atribuir.</summary>
    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
