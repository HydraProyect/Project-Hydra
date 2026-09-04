using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Cumplimiento.Commands.RegistrarInstruccionTratamientoIaTenantPropietario;
using CaeManager.Application.Cumplimiento.Commands.RevocarInstruccionTratamientoIaTenantPropietario;
using CaeManager.Application.Cumplimiento.Queries.ObtenerHistoricoInstruccionTratamientoIaTenantPropietario;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Cumplimiento;

/// <summary>
/// Nivel 0 (DEC-33, REC-035) contra Postgres real. Lo que una suite con
/// dobles en memoria no puede probar (ver
/// <c>RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandlerTests</c>,
/// Application.Tests): que <c>AmbitoTenantExplicito.Establecer</c> sella y
/// lee de verdad contra el Tenant propietario objetivo, no contra el tenant
/// de origen del Administrador de plataforma que ejecuta el comando —
/// exactamente el defecto que <c>InstruccionesTratamientoIaTenantPropietario</c>
/// tendría si el gate se leyera antes de abrir el ámbito (RLS + filtro
/// global de EF habrían visto siempre cero filas). Mismo criterio que
/// <c>GestionClavesApiTests</c> (P3-29): <see cref="TenantActualPorAmbito"/>
/// en vez de <c>TenantActualAmbiental</c>, porque los handlers bajo prueba
/// abren y cierran el ámbito ellos mismos.
/// </summary>
public class InstruccionTratamientoIaTenantPropietarioIntegrationTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioAdmin = Guid.NewGuid();
    private Guid _tenantPlataformaId;
    private Guid _tenantClienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var tenantPlataforma = new Tenant("TALVEG", esPlataforma: true);
        var tenantCliente = new Tenant("Refrielectric");
        contexto.Tenants.AddRange(tenantPlataforma, tenantCliente);
        await contexto.SaveChangesAsync();

        _tenantPlataformaId = tenantPlataforma.Id;
        _tenantClienteId = tenantCliente.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualPorAmbito();
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler CrearHandlerRegistrar(CaeManagerDbContext contexto) => new(
        contexto, AutorizacionGlobalFalsa.Instancia, new CurrentUserServiceFalso(_usuarioAdmin, _tenantPlataformaId),
        new InstruccionTratamientoIaTenantPropietarioRepository(contexto), contexto);

    private RevocarInstruccionTratamientoIaTenantPropietarioCommandHandler CrearHandlerRevocar(CaeManagerDbContext contexto) => new(
        AutorizacionGlobalFalsa.Instancia, new CurrentUserServiceFalso(_usuarioAdmin, _tenantPlataformaId),
        new InstruccionTratamientoIaTenantPropietarioRepository(contexto), contexto);

    /// <summary>
    /// Control positivo Y negativo del criterio de aceptación §15.1 de
    /// HO-035-02, en el mismo test para que el positivo no pueda pasar por
    /// casualidad: sin instrucción, el Nivel 0 deniega; con ella, autoriza —
    /// las dos mitades leídas del MISMO servicio (<see cref="IInstruccionTratamientoIaService"/>)
    /// que consumen los cinco tratamientos reales de IA.
    /// </summary>
    [Fact]
    public async Task Sin_instruccion_el_gate_deniega_y_tras_registrarla_autoriza()
    {
        await using var contextoLectura = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            var servicio = new InstruccionTratamientoIaService(new InstruccionTratamientoIaTenantPropietarioRepository(contextoLectura));

            (await servicio.EstaHabilitadaAsync(_tenantClienteId)).Should().BeFalse(
                "control negativo: sin fila vigente, el Nivel 0 tiene que denegar");
        }

        await using var contextoEscritura = CrearContexto();
        var handlerRegistrar = CrearHandlerRegistrar(contextoEscritura);
        var registrado = await handlerRegistrar.Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "Draft-2026-09-03", "Draft-2026-09-03"),
            CancellationToken.None);
        registrado.EsExitoso.Should().BeTrue();

        await using var contextoLecturaTrasRegistro = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            var servicio = new InstruccionTratamientoIaService(new InstruccionTratamientoIaTenantPropietarioRepository(contextoLecturaTrasRegistro));

            (await servicio.EstaHabilitadaAsync(_tenantClienteId)).Should().BeTrue(
                "control positivo: con una fila vigente para este tenant, el Nivel 0 tiene que autorizar");
        }
    }

    [Fact]
    public async Task La_instruccion_registrada_pertenece_al_tenant_cliente_no_al_de_plataforma()
    {
        await using var contextoEscritura = CrearContexto();
        var handlerRegistrar = CrearHandlerRegistrar(contextoEscritura);
        await handlerRegistrar.Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "Draft-2026-09-03", "Draft-2026-09-03"),
            CancellationToken.None);

        // Sin AmbitoTenantExplicito, el filtro global de EF (y RLS por
        // debajo) la esconderían si hubiera quedado sellada contra el
        // tenant de plataforma en vez del cliente — exactamente el defecto
        // que este test existe para descartar.
        await using var contextoComoCliente = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            var visible = await contextoComoCliente.InstruccionesTratamientoIaTenantPropietario.FirstOrDefaultAsync();
            visible.Should().NotBeNull();
            visible!.TenantId.Should().Be(_tenantClienteId);
        }

        await using var contextoComoPlataforma = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantPlataformaId))
        {
            var invisibleDesdeOtroTenant = await contextoComoPlataforma.InstruccionesTratamientoIaTenantPropietario.FirstOrDefaultAsync();
            invisibleDesdeOtroTenant.Should().BeNull(
                "el aislamiento por tenant (filtro global + RLS) tiene que impedir que el tenant de " +
                "plataforma vea la instrucción de otro tenant, aunque haya sido él quien la registró");
        }
    }

    /// <summary>
    /// El check-then-insert de RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler
    /// (¿ya hay una vigente?) es TOCTOU por construcción: dos altas
    /// concurrentes para el mismo tenant podrían pasar las dos la
    /// comprobación (hallazgo de la revisión adversarial de HO-035-02, §16).
    /// La defensa real no es el check de Application, es el índice único
    /// filtrado de <c>InstruccionTratamientoIaTenantPropietarioConfiguration</c>
    /// — este test lo prueba saltándose el comando y escribiendo directo por
    /// el repositorio, dos veces, para la misma fila vigente del mismo
    /// tenant: la segunda escritura tiene que fallar en la base, no en la
    /// aplicación.
    /// </summary>
    [Fact]
    public async Task El_indice_unico_impide_dos_filas_vigentes_para_el_mismo_tenant_aunque_la_aplicacion_no_lo_evite()
    {
        await using var primeraEscritura = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            new InstruccionTratamientoIaTenantPropietarioRepository(primeraEscritura).Agregar(
                new InstruccionTratamientoIaTenantPropietario("v1", "v1", DateTime.UtcNow, OrigenInstruccionTratamientoIa.AltaManualPlataforma, _usuarioAdmin));
            await primeraEscritura.SaveChangesAsync();
        }

        await using var segundaEscritura = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            new InstruccionTratamientoIaTenantPropietarioRepository(segundaEscritura).Agregar(
                new InstruccionTratamientoIaTenantPropietario("v2", "v2", DateTime.UtcNow, OrigenInstruccionTratamientoIa.AltaManualPlataforma, _usuarioAdmin));

            var intento = async () => await segundaEscritura.SaveChangesAsync();

            await intento.Should().ThrowAsync<DbUpdateException>(
                "el índice único IX_InstruccionesTratamientoIaTenantPropietario_TenantId_Vigente tiene que " +
                "rechazar una segunda fila no revocada para el mismo tenant, sin depender de que el comando " +
                "de Application la evitara primero");
        }
    }

    [Fact]
    public async Task Registrar_dos_veces_sin_revocar_falla_y_revocar_despues_permite_registrar_de_nuevo()
    {
        await using var primerRegistro = CrearContexto();
        var resultado1 = await CrearHandlerRegistrar(primerRegistro).Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "v1", "v1"), CancellationToken.None);
        resultado1.EsExitoso.Should().BeTrue();

        await using var segundoRegistro = CrearContexto();
        var resultado2 = await CrearHandlerRegistrar(segundoRegistro).Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "v2", "v2"), CancellationToken.None);
        resultado2.EsFallido.Should().BeTrue();
        resultado2.Error.Codigo.Should().Be("InstruccionTratamientoIa.YaVigente");

        await using var revocacion = CrearContexto();
        var resultadoRevocar = await CrearHandlerRevocar(revocacion).Handle(
            new RevocarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "Renovación de DPA"), CancellationToken.None);
        resultadoRevocar.EsExitoso.Should().BeTrue();

        await using var contextoLecturaTrasRevocar = CrearContexto();
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            var servicio = new InstruccionTratamientoIaService(new InstruccionTratamientoIaTenantPropietarioRepository(contextoLecturaTrasRevocar));
            (await servicio.EstaHabilitadaAsync(_tenantClienteId)).Should().BeFalse("revocar cierra la fila vigente, el Nivel 0 vuelve a denegar");
        }

        await using var tercerRegistro = CrearContexto();
        var resultado3 = await CrearHandlerRegistrar(tercerRegistro).Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "v3", "v3"), CancellationToken.None);
        resultado3.EsExitoso.Should().BeTrue("tras revocar la anterior, sí puede registrarse una nueva versión");
    }

    /// <summary>Criterio de aceptación §15.3: qué aceptó y cuándo, demostrable con una lectura.</summary>
    [Fact]
    public async Task El_historico_demuestra_que_aceptaciones_hubo_y_cuando()
    {
        await using var primerRegistro = CrearContexto();
        await CrearHandlerRegistrar(primerRegistro).Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "v1", "v1"), CancellationToken.None);

        await using var revocacion = CrearContexto();
        await CrearHandlerRevocar(revocacion).Handle(
            new RevocarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "Renovación de DPA"), CancellationToken.None);

        await using var segundoRegistro = CrearContexto();
        await CrearHandlerRegistrar(segundoRegistro).Handle(
            new RegistrarInstruccionTratamientoIaTenantPropietarioCommand(_tenantClienteId, "v2", "v2"), CancellationToken.None);

        await using var contextoConsulta = CrearContexto();
        var handlerConsulta = new ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQueryHandler(
            AutorizacionGlobalFalsa.Instancia, new CurrentUserServiceFalso(_usuarioAdmin, _tenantPlataformaId),
            new InstruccionTratamientoIaTenantPropietarioRepository(contextoConsulta));

        var resultado = await handlerConsulta.Handle(
            new ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQuery(_tenantClienteId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().HaveCount(2, "el histórico es append-only: la revocada sigue demostrando que hubo una instrucción y cuándo dejó de valer");
        resultado.Valor.Should().ContainSingle(i => i.VersionDpaAceptada == "v1" && !i.EstaVigente && i.MotivoRevocacion == "Renovación de DPA");
        resultado.Valor.Should().ContainSingle(i => i.VersionDpaAceptada == "v2" && i.EstaVigente);
    }

    private sealed class AutorizacionGlobalFalsa : IAutorizacionAdminPlataforma
    {
        public static readonly AutorizacionGlobalFalsa Instancia = new();

        public Task<bool> PuedeSobreTenantAsync(Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class CurrentUserServiceFalso(Guid usuarioId, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
