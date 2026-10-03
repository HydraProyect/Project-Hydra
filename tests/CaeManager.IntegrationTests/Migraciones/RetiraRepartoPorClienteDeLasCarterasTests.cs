using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// D-7, incremento 3 (contracción): la migración <c>RetiraRepartoPorClienteDeLasCarteras</c> añade el CHECK
/// <c>CK_AsignacionesCartera_TenantEnteroSalvoCerrada</c> y borra el índice transitorio
/// <c>IX_AsignacionesCartera_ResponsableRelacionVigente</c>. <b>Esta clase prueba la propiedad en la capa que
/// la garantiza, PostgreSQL real</b>: la guarda de dominio vive en <c>AsignacionResponsabilidadTests</c> y no
/// presta evidencia a esta.
///
/// <para>
/// Cada prueba hace un <c>INSERT</c>/<c>UPDATE</c> que la base de datos debe rechazar (SQLSTATE 23514 con el
/// nombre exacto de la restricción: un 23514 de otro CHECK no cuenta) y su control positivo, que debe
/// aceptar, para que un CHECK ausente o un arnés que no escribe nada no pasen por verde.
/// </para>
/// </summary>
public class RetiraRepartoPorClienteDeLasCarterasTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261003130422_RetiraRepartoPorClienteDeLasCarteras";
    private const string Restriccion = "CK_AsignacionesCartera_TenantEnteroSalvoCerrada";
    private const string IndiceRetirado = "IX_AsignacionesCartera_ResponsableRelacionVigente";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _tenant = new("Tenant de la contracción");

    private string MigracionAnterior
    {
        get
        {
            using var contexto = NuevoContexto(null);
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
            return migraciones[indice - 1];
        }
    }

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);
        await using var contexto = NuevoContexto(null);
        contexto.Tenants.Add(_tenant);
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    // ── PostgreSQL: el CHECK en el esquema final ───────────────────────────

    [Theory]
    [InlineData("Vigente")]
    [InlineData("Programada")]
    [InlineData("Suspendida")]
    public async Task Una_cartera_no_cerrada_por_Cliente_empresarial_no_se_puede_insertar(string estado)
    {
        var (operacion, cliente) = await SembrarOperacionAcotadaAsync();

        var cartera = CarteraLegadaPorCliente.Interna(
            operacion, Guid.NewGuid(), cliente, DateTime.UtcNow.AddDays(-1), null, DateTime.UtcNow);
        FijarEstado(cartera, estado);

        var error = await IntentarEscribirAsync(c => { c.AsignacionesCartera.Add(cartera); return c.SaveChangesAsync(); });

        error.SqlState.Should().Be("23514");
        error.ConstraintName.Should().Be(Restriccion);
    }

    [Fact]
    public async Task Control_positivo_la_misma_fila_universal_y_vigente_si_se_inserta()
    {
        var (operacion, _) = await SembrarOperacionAcotadaAsync();

        await using var contexto = NuevoContexto(_tenant.Id);
        var ahora = DateTime.UtcNow;
        contexto.AsignacionesCartera.Add(
            AsignacionCartera.Interna(operacion, Guid.NewGuid(), AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync(
            "el CHECK rechaza el reparto por Cliente empresarial, no la cartera: sin esto un esquema que rechazase todo pasaría la prueba anterior");

        (await contexto.AsignacionesCartera.CountAsync(c => c.Estado == EstadoAsignacion.Vigente)).Should().Be(1);
    }

    [Fact]
    public async Task Una_cartera_por_Cliente_empresarial_cerrada_se_conserva_como_historia()
    {
        var (operacion, cliente) = await SembrarOperacionAcotadaAsync();
        var ahora = DateTime.UtcNow;

        var historica = CarteraLegadaPorCliente.Interna(operacion, Guid.NewGuid(), cliente, ahora.AddDays(-30), null, ahora);
        historica.Cerrar(MotivoCierreAsignacion.Reorganizada, ahora);

        await using var contexto = NuevoContexto(_tenant.Id);
        contexto.AsignacionesCartera.Add(historica);
        await contexto.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync(
            "el histórico cerrado que dejó ConvierteCarterasPorClienteATenantEntero conserva su ámbito");
    }

    [Fact]
    public async Task Una_cartera_universal_no_se_puede_repartir_por_Cliente_empresarial_despues_con_un_UPDATE()
    {
        var (operacion, cliente) = await SembrarOperacionAcotadaAsync();
        var ahora = DateTime.UtcNow;
        var universal = AsignacionCartera.Interna(operacion, Guid.NewGuid(), AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        await using (var contexto = NuevoContexto(_tenant.Id))
        {
            contexto.AsignacionesCartera.Add(universal);
            await contexto.SaveChangesAsync();
        }

        await using var escritura = NuevoContexto(_tenant.Id);
        var error = await IntentarEscribirAsync(c => c.Database.ExecuteSqlRawAsync(
            "UPDATE \"AsignacionesCartera\" SET \"AmbitoRelacionClienteId\" = {0} WHERE \"Id\" = {1}", cliente, universal.Id), escritura);

        error.SqlState.Should().Be("23514");
        error.ConstraintName.Should().Be(Restriccion);
    }

    [Fact]
    public async Task El_indice_unico_transitorio_ya_no_existe_y_el_CHECK_si()
    {
        await using var contexto = NuevoContexto(null);

        (await ExisteIndice(contexto, IndiceRetirado)).Should().BeFalse(
            "el índice solo existía mientras el reparto era posible");
        (await ExisteIndice(contexto, "IX_AsignacionesCartera_UsuarioUniversalVigente")).Should().BeTrue(
            "control positivo: el instrumento ve un índice que sí tiene que existir; el único de la cartera universal sigue");
        (await ExisteRestriccion(contexto, Restriccion)).Should().BeTrue();
    }

    // ── La migración sobre datos ───────────────────────────────────────────

    [Fact]
    public async Task La_migracion_acepta_los_datos_ya_convertidos_conserva_las_filas_y_Down_restaura_el_indice()
    {
        await DeshacerElCambioAsync();
        var (operacion, cliente) = await SembrarOperacionAcotadaAsync();
        var ahora = DateTime.UtcNow;

        // Estado posterior a ConvierteCarterasPorClienteATenantEntero: el reparto, cerrado; la universal, abierta.
        var historica = CarteraLegadaPorCliente.Interna(operacion, Guid.NewGuid(), cliente, ahora.AddDays(-30), null, ahora);
        historica.Cerrar(MotivoCierreAsignacion.Reorganizada, ahora);
        var universal = AsignacionCartera.Interna(operacion, Guid.NewGuid(), AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
        await using (var contexto = NuevoContexto(_tenant.Id))
        {
            contexto.AsignacionesCartera.AddRange(historica, universal);
            await contexto.SaveChangesAsync();
        }

        await using (var contexto = NuevoContexto(null))
        {
            (await ExisteIndice(contexto, IndiceRetirado)).Should().BeTrue("control del estado previo");
            (await ExisteRestriccion(contexto, Restriccion)).Should().BeFalse("control del estado previo");
        }

        await MigrarAsync();

        await using (var contexto = NuevoContexto(null))
        {
            (await contexto.AsignacionesCartera.CountAsync()).Should().Be(2, "la migración no toca datos");
            (await ExisteIndice(contexto, IndiceRetirado)).Should().BeFalse();
            (await ExisteRestriccion(contexto, Restriccion)).Should().BeTrue();
        }

        await DeshacerElCambioAsync();

        await using (var contexto = NuevoContexto(null))
        {
            (await ExisteIndice(contexto, IndiceRetirado)).Should().BeTrue("Down recrea el índice");
            (await ExisteRestriccion(contexto, Restriccion)).Should().BeFalse("Down quita el CHECK");
            (await contexto.AsignacionesCartera.CountAsync()).Should().Be(2);
        }
    }

    [Theory]
    [InlineData("Vigente")]
    [InlineData("Programada")]
    [InlineData("Suspendida")]
    public async Task La_migracion_falla_sin_cambiar_nada_si_queda_una_cartera_por_Cliente_no_cerrada(string estado)
    {
        await DeshacerElCambioAsync();
        var (operacion, cliente) = await SembrarOperacionAcotadaAsync();
        var cartera = CarteraLegadaPorCliente.Interna(
            operacion, Guid.NewGuid(), cliente, DateTime.UtcNow.AddDays(-1), null, DateTime.UtcNow);
        FijarEstado(cartera, estado);
        await using (var contexto = NuevoContexto(_tenant.Id))
        {
            contexto.AsignacionesCartera.Add(cartera);
            await contexto.SaveChangesAsync();
        }

        var migrar = () => MigrarAsync();

        var error = (await migrar.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be("23514", "es la verificación de seguridad: no se aplica sobre datos que la violan");
        error.ConstraintName.Should().Be(Restriccion);

        await using var verificacion = NuevoContexto(null);
        (await ExisteIndice(verificacion, IndiceRetirado)).Should().BeTrue(
            "la migración es transaccional: al fallar no queda a medias");
        (await ExisteRestriccion(verificacion, Restriccion)).Should().BeFalse();
        (await verificacion.Database.GetAppliedMigrationsAsync()).Should().NotContain(MigracionDelCambio);
    }

    // ── Arnés ──────────────────────────────────────────────────────────────

    private static int _secuenciaCif;

    /// <summary>Un Cliente empresarial del Tenant y una Asignación de Operación interna acotada a él.</summary>
    private async Task<(AsignacionOperacion Operacion, Guid Cliente)> SembrarOperacionAcotadaAsync()
    {
        await using var contexto = NuevoContexto(_tenant.Id);
        var cliente = Empresa.CrearComoCliente(
            "Cliente empresarial de la contracción",
            DatosPruebaSeeder.GenerarCifValido(8_500_000 + Interlocked.Increment(ref _secuenciaCif)), false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Interna(
            _tenant.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(cliente.Id), ahora.AddDays(-60), null, ahora);
        contexto.AsignacionesOperacion.Add(operacion);
        await contexto.SaveChangesAsync();
        return (operacion, cliente.Id);
    }

    private static void FijarEstado(AsignacionCartera cartera, string estado)
    {
        switch (estado)
        {
            case "Vigente":
                break;
            case "Programada":
                typeof(AsignacionResponsabilidad).GetProperty(nameof(AsignacionResponsabilidad.Estado))!
                    .SetValue(cartera, EstadoAsignacion.Programada);
                break;
            case "Suspendida":
                cartera.Suspender();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(estado), estado, null);
        }

        cartera.Estado.ToString().Should().Be(estado, "el arnés tiene que dejar la fila en el estado que dice probar");
    }

    private async Task<PostgresException> IntentarEscribirAsync(Func<CaeManagerDbContext, Task> escritura, CaeManagerDbContext? contexto = null)
    {
        await using var propio = contexto is null ? NuevoContexto(_tenant.Id) : null;
        var uso = contexto ?? propio!;
        try
        {
            await escritura(uso);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException pg)
        {
            return pg;
        }
        catch (PostgresException pg)
        {
            return pg;
        }

        throw new Xunit.Sdk.XunitException("La escritura no falló: la base de datos aceptó una cartera por Cliente empresarial no cerrada.");
    }

    private static async Task<bool> ExisteIndice(CaeManagerDbContext contexto, string nombre) =>
        (await contexto.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_indexes WHERE indexname = {nombre}")
            .ToListAsync()).Single() > 0;

    private static async Task<bool> ExisteRestriccion(CaeManagerDbContext contexto, string nombre) =>
        (await contexto.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_constraint WHERE conname = {nombre}")
            .ToListAsync()).Single() > 0;

    private async Task DeshacerElCambioAsync()
    {
        await using var contexto = NuevoContexto(null);
        await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);
    }

    private async Task MigrarAsync()
    {
        await using var contexto = NuevoContexto(null);
        await contexto.GetService<IMigrator>().MigrateAsync();
    }

    private CaeManagerDbContext NuevoContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
