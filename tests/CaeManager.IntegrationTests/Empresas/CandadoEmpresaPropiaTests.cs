using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Empresas.Commands.CrearEmpresa;
using CaeManager.Application.Empresas.Commands.RestaurarEmpresa;
using CaeManager.Application.Importacion;
using CaeManager.Application.Importacion.Commands.EjecutarImportacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
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

    // ---- El guardado ocurre con el candado tomado (revisión Codex r2 de la PR #2193, MEDIO 3) ----
    // Las esperas de arriba prueban que el escritor pide el candado antes de contar, pero no que guarde antes de soltarlo:
    // un SaveChanges movido fuera de SerializarAsync seguiría esperando. Aquí un interceptor de SaveChanges de prueba mira,
    // justo antes de que cada guardado del escritor se envíe, si la sesión de PostgreSQL de ese mismo contexto tiene
    // concedido el candado de su Tenant (pg_locks por pid, desde otra conexión).

    [Fact]
    public async Task El_alta_guarda_con_el_candado_del_Tenant_tomado()
    {
        var observador = new ObservadorDelCandadoAlGuardar(() => CrearContexto(_tenantA), _tenantA);
        await using var circuito = CrearContexto(_tenantA, observador);

        var resultado = await Alta(circuito, _tenantA).Handle(new CrearEmpresaCommand("Eta Montajes", null, []), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        observador.Guardados.Should().NotBeEmpty().And.OnlyContain(tomado => tomado, "cada guardado del alta ocurre con el candado concedido");
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
    }

    [Fact]
    public async Task La_restauracion_guarda_con_el_candado_del_Tenant_tomado()
    {
        var eliminadaId = await SembrarEliminadaAsync("Theta Instalaciones");
        var observador = new ObservadorDelCandadoAlGuardar(() => CrearContexto(_tenantA), _tenantA);
        await using var circuito = CrearContexto(_tenantA, observador);

        var resultado = await new RestaurarEmpresaCommandHandler(
                circuito, new TenantActualAmbiental { TenantId = _tenantA }, new AlcanceDatosServiceFalso(),
                circuito, Limite(circuito, _tenantA))
            .Handle(new RestaurarEmpresaCommand(eliminadaId), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        observador.Guardados.Should().NotBeEmpty().And.OnlyContain(tomado => tomado, "la restauración guarda con el candado concedido");
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
    }

    [Fact]
    public async Task La_importacion_guarda_con_el_candado_del_Tenant_tomado()
    {
        var observador = new ObservadorDelCandadoAlGuardar(() => CrearContexto(_tenantA), _tenantA);
        await using var circuito = CrearContexto(_tenantA, observador);

        var resultado = await Importacion(circuito, _tenantA)
            .Handle(new EjecutarImportacionCommand(PlanConUnaEmpresaPropia(Guid.NewGuid(), "Iota Montajes")), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.EmpresasCreadas.Should().Be(1);
        observador.Guardados.Should().NotBeEmpty().And.OnlyContain(tomado => tomado, "la importación guarda con el candado concedido");
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
    }

    /// <summary>Control del observador: un guardado sin candado lo ve como tal. Sin él, «todo tomado» no diría nada.</summary>
    [Fact]
    public async Task El_observador_ve_un_guardado_sin_el_candado()
    {
        var observador = new ObservadorDelCandadoAlGuardar(() => CrearContexto(_tenantA), _tenantA);
        await using var circuito = CrearContexto(_tenantA, observador);

        circuito.Empresas.Add(new Empresa("Kappa Montajes"));
        await circuito.SaveChangesAsync();

        observador.Guardados.Should().Equal(false);
    }

    /// <summary>
    /// Revisión Codex r2 de la PR #2193, MEDIO 1, en PostgreSQL: dos confirmaciones de la MISMA importación con una
    /// Empresa propia nueva esperan el candado (lo tiene un testigo); al soltarlo, una guarda y la otra, que ya ve la marca
    /// de la operación, responde «ya ejecutada» en vez del error de límite.
    /// </summary>
    [Fact]
    public async Task Dos_confirmaciones_de_la_misma_importacion_dan_una_escritura_y_un_ya_ejecutada()
    {
        await using var testigo = CrearContexto(_tenantA);
        await using var circuitoUno = CrearContexto(_tenantA);
        await using var circuitoDos = CrearContexto(_tenantA);
        var operacionId = Guid.NewGuid();

        var transaccionTestigo = await TomarCandadoAsync(testigo, _tenantA);
        var confirmacionUno = Importacion(circuitoUno, _tenantA)
            .Handle(new EjecutarImportacionCommand(PlanConUnaEmpresaPropia(operacionId, "Lambda Obras")), CancellationToken.None);
        var confirmacionDos = Importacion(circuitoDos, _tenantA)
            .Handle(new EjecutarImportacionCommand(PlanConUnaEmpresaPropia(operacionId, "Lambda Obras")), CancellationToken.None);

        (await EsperanElCandadoAsync(_tenantA, 2, confirmacionUno, confirmacionDos))
            .Should().BeTrue("las dos confirmaciones esperan el candado del Tenant antes del recuento final");
        await transaccionTestigo.CommitAsync();

        var resultados = await Task.WhenAll(confirmacionUno, confirmacionDos);

        resultados.Should().OnlyContain(r => r.EsExitoso, "la segunda es la misma operación, no un rechazo del límite");
        resultados.Count(r => r.Valor.EmpresasCreadas == 1).Should().Be(1);
        resultados.Count(r => r.Valor.Omitidos.Any(o => o.Hoja == "Importación")).Should().Be(1, "la otra responde «ya ejecutada»");
        (await PropiasActivasAsync(_tenantA)).Should().Be(1);
    }

    private async Task<Guid> SembrarEliminadaAsync(string razonSocial)
    {
        await using var siembra = CrearContexto(_tenantA);
        var eliminada = new Empresa(razonSocial);
        eliminada.MarcarComoEliminado(Guid.NewGuid());
        siembra.Empresas.Add(eliminada);
        await siembra.SaveChangesAsync();
        return eliminada.Id;
    }

    private static PlanImportacionDto PlanConUnaEmpresaPropia(Guid operacionId, string razonSocial) =>
        new(operacionId, [], [new EmpresaImportadaDto(razonSocial, YaExiste: false, Fila: 2)], [], [], [], [], []);

    private static EjecutarImportacionCommandHandler Importacion(CaeManagerDbContext contexto, Guid tenantId) => new(
        new EmpresaRepository(contexto), new TrabajadorRepository(contexto), new DocumentoRepository(contexto),
        new AsignacionRepository(contexto), new OperacionImportacionRepository(contexto),
        contexto, contexto, contexto, contexto, contexto, contexto,
        new AutorizacionEscrituraEfectivaFalsa(), AltaAcreditacionesDePrueba.Con(contexto), new AlcanceDatosServiceFalso(),
        new SerializacionAltasSinBloqueo(), Limite(contexto, tenantId));

    /// <summary>
    /// En cada SaveChanges del contexto al que se añade, antes de enviarlo: ¿tiene la sesión de PostgreSQL de ese contexto
    /// concedido el candado de Empresa propia de <paramref name="tenantId"/>? Sin conexión abierta no hay transacción ni
    /// candado, y cuenta como no tomado.
    /// </summary>
    private sealed class ObservadorDelCandadoAlGuardar(Func<CaeManagerDbContext> crearLectura, Guid tenantId) : SaveChangesInterceptor
    {
        public List<bool> Guardados { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Guardados.Add(await TieneElCandadoAsync(eventData.Context!, cancellationToken));
            return result;
        }

        private async Task<bool> TieneElCandadoAsync(DbContext escritor, CancellationToken cancellationToken)
        {
            var conexion = escritor.Database.GetDbConnection();
            if (conexion.State != System.Data.ConnectionState.Open)
                return false;

            int pid;
            await using (var comando = conexion.CreateCommand())
            {
                comando.Transaction = escritor.Database.CurrentTransaction?.GetDbTransaction();
                comando.CommandText = "SELECT pg_backend_pid()";
                pid = Convert.ToInt32(await comando.ExecuteScalarAsync(cancellationToken));
            }

            var clave = CandadoEmpresaPropia.Espacio + tenantId;
            await using var lectura = crearLectura();
            var concedidos = await lectura.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks l
                JOIN pg_database d ON d.oid = l.database
                WHERE l.locktype = 'advisory' AND l.granted AND d.datname = current_database() AND l.pid = {pid}
                  AND ((l.classid::bigint << 32) | l.objid::bigint) = hashtextextended({clave}, 0)
                """).SingleAsync(cancellationToken);
            return concedidos > 0;
        }
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

    private CaeManagerDbContext CrearContexto(Guid tenantId, params IInterceptor[] interceptores)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors([new TenantSelladoInterceptor(tenantActual), .. interceptores])
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
