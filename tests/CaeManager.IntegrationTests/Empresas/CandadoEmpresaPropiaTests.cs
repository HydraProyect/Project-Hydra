using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// D-P11 en PostgreSQL de verdad: el recuento de Empresas propias y su guardado van bajo el candado de transacción por
/// Tenant propietario de <see cref="CandadoEmpresaPropia"/>, así que dos escritores concurrentes del mismo Tenant no
/// pueden contar cero los dos (revisión Codex r1 de la PR #2193, ALTO 1). Para que la concurrencia sea real y no dependa
/// de un plazo, una transacción testigo toma antes el candado del Tenant: los dos escritores tienen que quedarse
/// esperándolo (lo dice <c>pg_locks</c>) antes de contar nada, y al soltarlo uno guarda y el otro cuenta lo guardado. Sin
/// el candado no esperarían, y la prueba falla en la espera.
/// </summary>
public class CandadoEmpresaPropiaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Dos_altas_concurrentes_en_el_mismo_Tenant_dejan_una_sola_Empresa_propia()
    {
        await using var testigo = CrearContexto(_tenantA);
        await using var circuitoUno = CrearContexto(_tenantA);
        await using var circuitoDos = CrearContexto(_tenantA);

        var transaccionTestigo = await TomarCandadoAsync(testigo, _tenantA);
        var altaUno = Alta(circuitoUno, _tenantA).Handle(new CrearEmpresaCommand("Alfa Montajes", null, []), CancellationToken.None);
        var altaDos = Alta(circuitoDos, _tenantA).Handle(new CrearEmpresaCommand("Beta Montajes", null, []), CancellationToken.None);

        (await EsperanElCandadoAsync(_tenantA, 2, altaUno, altaDos))
            .Should().BeTrue("las dos altas esperan el candado del Tenant antes de contar sus Empresas propias");
        await transaccionTestigo.CommitAsync();

        var resultados = await Task.WhenAll(altaUno, altaDos);

        resultados.Count(r => r.EsExitoso).Should().Be(1, "solo cabe una Empresa propia por Tenant propietario");
        resultados.Single(r => r.EsFallido).Error.Codigo.Should().Be(ErroresLimiteEmpresaPropia.UnaSolaEmpresaPropia.Codigo);
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
    }

    [Fact]
    public async Task Un_alta_y_una_restauracion_concurrentes_dejan_una_sola_Empresa_propia()
    {
        Guid eliminadaId;
        await using (var siembra = CrearContexto(_tenantA))
        {
            var eliminada = new Empresa("Gamma Instalaciones");
            eliminada.MarcarComoEliminado(Guid.NewGuid());
            siembra.Empresas.Add(eliminada);
            await siembra.SaveChangesAsync();
            eliminadaId = eliminada.Id;
        }

        await using var testigo = CrearContexto(_tenantA);
        await using var circuitoAlta = CrearContexto(_tenantA);
        await using var circuitoRestauracion = CrearContexto(_tenantA);

        var transaccionTestigo = await TomarCandadoAsync(testigo, _tenantA);
        var alta = Alta(circuitoAlta, _tenantA).Handle(new CrearEmpresaCommand("Delta Instalaciones", null, []), CancellationToken.None);
        var restauracion = new RestaurarEmpresaCommandHandler(
                circuitoRestauracion, new TenantActualAmbiental { TenantId = _tenantA }, new AlcanceDatosServiceFalso(),
                circuitoRestauracion, Limite(circuitoRestauracion, _tenantA))
            .Handle(new RestaurarEmpresaCommand(eliminadaId), CancellationToken.None);

        (await EsperanElCandadoAsync(_tenantA, 2, alta, restauracion))
            .Should().BeTrue("el alta y la restauración esperan el mismo candado del Tenant");
        await transaccionTestigo.CommitAsync();

        var exitos = (await alta).EsExitoso ? 1 : 0;
        exitos += (await restauracion).EsExitoso ? 1 : 0;

        exitos.Should().Be(1, "la restauración cuenta como una Empresa propia más");
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
    }

    /// <summary>
    /// Control: el candado es por Tenant propietario. Con el de A tomado, un alta en B termina sin esperar, mientras que
    /// una de A sigue esperando hasta que se suelta.
    /// </summary>
    [Fact]
    public async Task Las_altas_de_dos_Tenants_distintos_no_se_bloquean()
    {
        await using var testigo = CrearContexto(_tenantA);
        await using var circuitoA = CrearContexto(_tenantA);
        await using var circuitoB = CrearContexto(_tenantB);

        var transaccionTestigo = await TomarCandadoAsync(testigo, _tenantA);
        var altaA = Alta(circuitoA, _tenantA).Handle(new CrearEmpresaCommand("Epsilon Obras", null, []), CancellationToken.None);
        (await EsperanElCandadoAsync(_tenantA, 1, altaA)).Should().BeTrue("el alta de A espera el candado de A");

        var altaB = Alta(circuitoB, _tenantB).Handle(new CrearEmpresaCommand("Zeta Obras", null, []), CancellationToken.None);
        var primera = await Task.WhenAny(altaB, Task.Delay(TimeSpan.FromSeconds(30)));

        primera.Should().BeSameAs(altaB, "el candado de A no detiene a B");
        (await altaB).EsExitoso.Should().BeTrue();
        altaA.IsCompleted.Should().BeFalse("el alta de A sigue esperando mientras A tenga el candado tomado");

        await transaccionTestigo.CommitAsync();
        (await altaA).EsExitoso.Should().BeTrue();
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
        (await PropiasActivasAsync(_tenantB)).Should().Be(1);
    }

    [Fact]
    public async Task El_candado_se_toma_en_la_transaccion_del_escritor_y_dura_hasta_su_fin()
    {
        await using var circuito = CrearContexto(_tenantA);
        await using var otro = CrearContexto(_tenantA);
        var candado = Candado(circuito, _tenantA);

        bool? libreDentro = null;
        var resultado = await candado.EjecutarAsync(async ct =>
        {
            circuito.Database.CurrentTransaction.Should().NotBeNull("sin transacción el candado se soltaría al acabar la sentencia");
            libreDentro = await IntentarCandadoAsync(otro, _tenantA);
            return Result.Exito();
        });

        resultado.EsExitoso.Should().BeTrue();
        libreDentro.Should().BeFalse("mientras dura la operación, otra transacción no puede tomar el candado del Tenant");
        circuito.Database.CurrentTransaction.Should().BeNull("la transacción abierta por el candado se confirma al terminar");
        (await IntentarCandadoAsync(otro, _tenantA)).Should().BeTrue("el candado se suelta con el COMMIT");
    }

    private CrearEmpresaCommandHandler Alta(CaeManagerDbContext contexto, Guid tenantId) => new(
        new EmpresaRepository(contexto), new RelacionEmpresarialRepository(contexto), contexto,
        new AlcanceDatosServiceFalso(), contexto, Limite(contexto, tenantId));

    private static LimiteEmpresaPropia Limite(CaeManagerDbContext contexto, Guid tenantId) =>
        new(contexto, new TenantActualAmbiental { TenantId = tenantId }, new AlertaOperativaInerte(), Candado(contexto, tenantId));

    private static CandadoEmpresaPropia Candado(CaeManagerDbContext contexto, Guid tenantId) =>
        new(contexto, new TransaccionDeComando(contexto), new TenantActualAmbiental { TenantId = tenantId });

    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> TomarCandadoAsync(
        CaeManagerDbContext contexto, Guid tenantId)
    {
        var transaccion = await contexto.Database.BeginTransactionAsync();
        var clave = CandadoEmpresaPropia.Espacio + tenantId;
        await contexto.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))");
        return transaccion;
    }

    private static async Task<bool> IntentarCandadoAsync(CaeManagerDbContext contexto, Guid tenantId)
    {
        await using var transaccion = await contexto.Database.BeginTransactionAsync();
        var clave = CandadoEmpresaPropia.Espacio + tenantId;
        var tomado = await contexto.Database
            .SqlQuery<bool>($"""SELECT pg_try_advisory_xact_lock(hashtextextended({clave}, 0)) AS "Value" """)
            .SingleAsync();
        await transaccion.RollbackAsync();
        return tomado;
    }

    /// <summary>
    /// Lo que discrimina es el catálogo y no un plazo: sin candado los escritores terminan sin que aparezca ninguna
    /// espera, y un plazo pasaría igual en una máquina lenta. Mismo patrón que <c>BloqueoSuscripcionStripeTests</c>.
    /// </summary>
    private async Task<bool> EsperanElCandadoAsync(Guid tenantId, int esperados, params Task[] operaciones)
    {
        var clave = CandadoEmpresaPropia.Espacio + tenantId;
        await using var lectura = CrearContexto(tenantId);
        for (var intento = 0; intento < 300 && !operaciones.Any(o => o.IsCompleted); intento++)
        {
            var esperando = await lectura.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks l
                JOIN pg_database d ON d.oid = l.database
                WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()
                  AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended({clave}, 0)
                """).SingleAsync();
            if (esperando >= esperados) return true;

            await Task.Delay(100);
        }

        return false;
    }

    private async Task<int> PropiasActivasAsync(Guid tenantId)
    {
        await using var lectura = CrearContexto(tenantId);
        return await lectura.Empresas.IgnoreQueryFilters()
            .CountAsync(e => e.TenantId == tenantId && e.EsPropia && !e.EstaEliminado);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
