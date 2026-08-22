using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Cobertura de P0-7 (docs/business/MATURITY_REVIEW.md): alta de un Cliente
/// Delegante nuevo, v1 restringida a Administrador de plataforma
/// (ADR-004 § 12.2). Contra Postgres real para probar tanto la autorización
/// (tenant de origen) como el sellado real de <c>TenantId</c> vía
/// <see cref="TenantSelladoInterceptor"/> en la fila de <c>ParametroSistema</c>
/// del tenant nuevo.
///
/// Usa <see cref="TenantActualDesdeAmbitoExplicito"/> en vez del
/// <c>TenantActualAmbiental</c> habitual de otros tests de integración:
/// el propio Command bajo prueba resuelve el tenant del <c>ParametroSistema</c>
/// nuevo con <c>AmbitoTenantExplicito</c> (mismo mecanismo que
/// <c>DelegacionDemoSeeder</c>), y <c>TenantActualAmbiental</c> es, a
/// propósito, un valor fijo que no lo consulta — aquí hace falta el mismo
/// comportamiento que la implementación Web real.
/// </summary>
public class CrearClienteDeleganteTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly ITenantActual _tenantActual = new TenantActualDesdeAmbitoExplicito();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(_tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    /// <summary>
    /// Desde A3 el alta la autoriza una concesión <b>global</b> de
    /// <c>AdminPlataforma</c>, no la pertenencia al tenant de plataforma. Global y
    /// no acotada porque el tenant objetivo todavía no existe: no hay nada a lo
    /// que acotar.
    /// </summary>
    private static async Task SembrarAdminPlataformaGlobalAsync(CaeManagerDbContext contexto, Guid usuarioId)
    {
        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
            usuarioId, vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: null));

        await contexto.SaveChangesAsync();
    }

    private CrearClienteDeleganteCommandHandler CrearHandler(CaeManagerDbContext contexto, Guid? tenantOrigenId, Guid? usuarioId) =>
        new(
            new TenantRepository(contexto),
            new DelegacionTenantRepository(contexto),
            new AsignacionOperadorDelegadoRepository(contexto),
            new ParametroSistemaRepository(contexto),
            // La implementación REAL contra las concesiones de la base: en un
            // test de integración un doble ocultaría justo lo que interesa, que
            // es si el alcance de la concesión autoriza esta operación.
            new AutorizacionAdminPlataformaPorConcesion(contexto),
            new CurrentUserServiceFalso(tenantOrigenId, usuarioId),
            // El writer real, no un doble: así el test cubre también que el
            // alta escribe la raíz del tenant nuevo, su operación delegada y la
            // cartera del operador — la doble escritura de este camino.
            new AsignacionesOperativasWriter(
                contexto, _tenantActual, new CurrentUserServiceFalso(tenantOrigenId, usuarioId)),
            contexto);

    [Fact]
    public async Task Un_administrador_de_plataforma_crea_el_tenant_la_delegacion_activa_y_su_propia_asignacion()
    {
        await using var contexto = CrearContexto();

        var tenantPlataforma = new Tenant("Hydra Plataforma de prueba", esPlataforma: true);
        contexto.Tenants.Add(tenantPlataforma);
        await contexto.SaveChangesAsync();

        var usuarioId = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, usuarioId);
        var handler = CrearHandler(contexto, tenantPlataforma.Id, usuarioId);

        var resultado = await handler.Handle(
            new CrearClienteDeleganteCommand($"Constructora de prueba {Guid.NewGuid():N}"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();

        var tenantClienteId = resultado.Valor;
        var tenantCliente = await contexto.Tenants.SingleAsync(t => t.Id == tenantClienteId);
        tenantCliente.EsPlataforma.Should().BeFalse();

        // IgnoreQueryFilters: el filtro global de EntidadConTenant exige
        // ITenantActual.TenantId == TenantId de la fila, y aquí ya salimos del
        // AmbitoTenantExplicito que el propio Command abrió para sellarla —
        // se re-aplica el filtro de tenant a mano con el Where explícito.
        var parametro = await contexto.ParametrosSistema.IgnoreQueryFilters()
            .SingleAsync(p => p.TenantId == tenantClienteId);
        parametro.Should().NotBeNull();

        var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.TenantClienteId == tenantClienteId);
        delegacion.TenantConsultoraId.Should().Be(tenantPlataforma.Id);
        delegacion.Activa.Should().BeTrue();

        var asignacion = await contexto.AsignacionesOperadorDelegado.SingleAsync(a => a.DelegacionTenantId == delegacion.Id);
        asignacion.UsuarioId.Should().Be(usuarioId);

        // --- La otra mitad de la doble escritura ---
        //
        // La operación externa se escribe con el propietario y el operador en
        // el orden correcto: los datos son del tenant nuevo, quien opera es la
        // plataforma. Es lo que encarna "delegación de acceso, no de propiedad".
        var operacion = await contexto.AsignacionesOperacion
            .SingleAsync(o => !o.EsRaiz && o.PropietarioTenantId == tenantClienteId);
        operacion.OperadorTenantId.Should().Be(tenantPlataforma.Id);
        operacion.Estado.Should().Be(EstadoAsignacion.Vigente);
        operacion.Ambito.EsUniversal.Should().BeTrue();

        // Y su raíz: el derecho del tenant nuevo a operarse a sí mismo el día
        // que internalice.
        (await contexto.AsignacionesOperacion.AnyAsync(o => o.EsRaiz && o.PropietarioTenantId == tenantClienteId))
            .Should().BeTrue();

        // La cartera del operador inicial NO se escribe, y es lo correcto: su
        // rol es GestorCae, un rol de cartera, y darle una cartera universal le
        // entregaría todos los clientes del tenant delegado. Como el tenant
        // acaba de nacer y no tiene ninguno, su alcance real es exactamente el
        // mismo. Sus carteras nacerán cliente a cliente al asignárselos.
        (await contexto.AsignacionesCartera.AnyAsync(c => c.AsignacionOperacionId == operacion.Id))
            .Should().BeFalse("un rol de cartera no recibe cartera universal");
    }

    [Fact]
    public async Task El_operador_inicial_con_rol_de_alcance_total_si_recibe_cartera_sobre_la_operacion_recien_creada()
    {
        // Prueba directa del defecto que tenía la doble escritura de este
        // camino: la operación se añadía al contexto sin guardar y la cartera
        // se resolvía con una consulta, que va a SQL y no ve entidades Added.
        // Resultado: operación creada y cartera perdida en silencio.
        await using var contexto = CrearContexto();

        var tenantPlataforma = new Tenant("Hydra Plataforma cartera", esPlataforma: true);
        contexto.Tenants.Add(tenantPlataforma);
        await contexto.SaveChangesAsync();

        var usuarioId = Guid.NewGuid();
        var writer = new AsignacionesOperativasWriter(
            contexto, _tenantActual, new CurrentUserServiceFalso(tenantPlataforma.Id, usuarioId));

        var tenantCliente = new Tenant("Cliente delegante cartera", PerfilVocabularioTenant.ClienteDirecto);
        contexto.Tenants.Add(tenantCliente);

        var operacion = await writer.AbrirOperacionDelegadaAsync(
            tenantCliente.Id, tenantPlataforma.Id, DateTime.UtcNow, vigenciaHasta: null);

        // La operación está solo en el ChangeTracker, todavía sin guardar.
        await writer.AbrirCarteraOperadorAsync(operacion, usuarioId, Roles.Consulta);

        await contexto.SaveChangesAsync();

        var cartera = await contexto.AsignacionesCartera.SingleAsync(c => c.AsignacionOperacionId == operacion.Id);
        cartera.UsuarioId.Should().Be(usuarioId);
        cartera.Rol.Should().Be(Roles.Consulta);
        cartera.Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    [Fact]
    public async Task Rechaza_el_alta_cuando_el_tenant_de_origen_no_es_la_plataforma()
    {
        await using var contexto = CrearContexto();

        var tenantNoPlataforma = new Tenant("Cliente normal de prueba");
        contexto.Tenants.Add(tenantNoPlataforma);
        await contexto.SaveChangesAsync();

        // SIN sembrar concesión: desde A3 lo que deniega es no tener
        // AdminPlataforma, no operar desde un tenant que no sea el de plataforma.
        var handler = CrearHandler(contexto, tenantNoPlataforma.Id, Guid.NewGuid());

        var resultado = await handler.Handle(
            new CrearClienteDeleganteCommand("Constructora rechazada"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClienteDelegante.SinPermiso");

        // 2, no 1: la migración siembra el tenant #1 (EsPlataforma = true) en
        // toda base de datos nueva — el de aquí es el segundo, y ninguno más
        // debió crearse tras el rechazo.
        (await contexto.Tenants.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Rechaza_un_nombre_de_tenant_duplicado()
    {
        await using var contexto = CrearContexto();

        var tenantPlataforma = new Tenant("Hydra Plataforma de prueba 2", esPlataforma: true);
        var nombreDuplicado = $"Constructora duplicada {Guid.NewGuid():N}";
        var tenantExistente = new Tenant(nombreDuplicado);
        contexto.Tenants.AddRange(tenantPlataforma, tenantExistente);
        await contexto.SaveChangesAsync();

        var usuarioAutorizado = Guid.NewGuid();
        await SembrarAdminPlataformaGlobalAsync(contexto, usuarioAutorizado);
        var handler = CrearHandler(contexto, tenantPlataforma.Id, usuarioAutorizado);

        var resultado = await handler.Handle(new CrearClienteDeleganteCommand(nombreDuplicado), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("ClienteDelegante.NombreDuplicado");
    }

    private sealed class CurrentUserServiceFalso(Guid? tenantOrigenId, Guid? usuarioId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class TenantActualDesdeAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
