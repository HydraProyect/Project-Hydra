using System.Data;
using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Una conexión y un contexto RLS firmado por operación de
/// <see cref="PuertaAccesoDatos"/> (decisión D3 del plan de rendimiento), contra
/// PostgreSQL real y autenticando como <c>cae_app_runtime</c>.
///
/// <para>
/// Lo que se mide es el mecanismo nuevo y que las garantías de antes siguen:
/// N comandos de una operación pagan un solo <c>set_config</c> firmado (el
/// token no cambia entre comandos y hay una sola apertura física); un cambio de
/// Tenant, de sesión privilegiada o de contexto a mitad de operación llega al
/// comando siguiente; una excepción a mitad cierra la conexión; una transacción
/// por la estrategia de reintentos sigue funcionando dentro de la operación; y
/// una conexión que tenía abierta otro no la cierra la puerta. La reutilización
/// de la conexión física entre usuarios va aparte, en
/// <see cref="ConexionPorOperacionPoolTests"/>, porque necesita el pool sin
/// vaciados concurrentes.
/// </para>
///
/// <para>
/// Las lecturas de filas usan <c>IgnoreQueryFilters</c> a propósito: así solo
/// filtra RLS con el contexto de la conexión, y lo que se ve demuestra qué
/// contexto lleva el comando (con el filtro de EF, el cambio de Tenant se vería
/// aunque la conexión siguiera con el contexto viejo).
/// </para>
/// </summary>
public class ConexionPorOperacionTests : IAsyncLifetime
{
    internal readonly string CadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    internal Guid TenantA { get; private set; }
    internal Guid TenantB { get; private set; }

    public async Task InitializeAsync()
    {
        // Mismo andamiaje que ContextoRlsFirmadoTests: Tenants reales y sus
        // Asignaciones de Operación raíz, que es lo que cada usuario ve por RLS.
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
        await contexto.Database.MigrateAsync();

        var a = new Tenant("Tenant de prueba A");
        var b = new Tenant("Tenant de prueba B");
        contexto.Tenants.AddRange(a, b);
        await contexto.SaveChangesAsync();
        await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
        TenantA = a.Id;
        TenantB = b.Id;
    }
    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(CadenaPropietario);

    [Fact]
    public async Task N_comandos_de_una_operacion_pagan_un_solo_contexto_firmado()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;

        // Control del instrumento: fuera de la puerta, cada comando abre la
        // conexión y firma un token nuevo (nonce aleatorio), así que dos
        // lecturas seguidas ven tokens distintos.
        var fueraPrimero = await TokenAsync(contexto);
        var fueraSegundo = await TokenAsync(contexto);
        fueraPrimero.Should().NotBeEmpty("sin token firmado el resto del test no mediría nada");
        fueraSegundo.Should().NotBe(fueraPrimero, "control: fuera de la operación, un set_config por comando");
        aperturas.Aperturas.Should().Be(2);

        aperturas.Aperturas = 0;
        var tokens = await puerta.EjecutarAsync(async () =>
        {
            var leidos = new List<string>();
            for (var i = 0; i < 4; i++)
                leidos.Add(await TokenAsync(contexto));
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);
            leidos.Add(await TokenAsync(contexto));
            return leidos;
        });

        tokens.Distinct().Should().ContainSingle(
            "dentro de la operación ningún comando vuelve a firmar: un solo set_config para todos");
        aperturas.Aperturas.Should().Be(1, "una sola apertura física por operación");
        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed,
            "al salir de la operación la conexión vuelve al pool");
    }

    [Fact]
    public async Task Un_cambio_de_Tenant_a_mitad_de_operacion_llega_al_comando_siguiente()
    {
        var puerta = new PuertaAccesoDatos();
        var tenant = new TenantMutable(TenantA);
        var (contexto, aperturas) = CrearContextoRuntime(tenant, new SeleccionMutable(), puerta);
        await using var _ = contexto;

        await puerta.EjecutarAsync(async () =>
        {
            var tokenA = await TokenAsync(contexto);
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA], "control positivo antes del cambio");

            tenant.TenantId = TenantB;

            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantB],
                "el comando siguiente al cambio lleva el contexto nuevo, nunca el viejo");
            (await TokenAsync(contexto)).Should().NotBe(tokenA, "el cambio obliga a firmar de nuevo");

            tenant.TenantId = null;
            (await TenantsVisiblesAsync(contexto)).Should().BeEmpty("sin Tenant, fallo cerrado también a mitad");
        });

        aperturas.Aperturas.Should().Be(1, "se realinea sobre la misma conexión, sin reabrirla");
    }

    [Fact]
    public async Task Guardar_tras_un_cambio_de_Tenant_a_mitad_de_operacion_escribe_con_el_contexto_nuevo()
    {
        var puerta = new PuertaAccesoDatos();
        var tenant = new TenantMutable(TenantA);
        var (contexto, _) = CrearContextoRuntime(tenant, new SeleccionMutable(), puerta);
        await using var __ = contexto;
        var empresa = Empresa.CrearComoCliente(
            "Escrita tras el cambio S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);

        await puerta.EjecutarAsync(async () =>
        {
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA], "la operación retiene la conexión con A");

            tenant.TenantId = TenantB;
            contexto.Empresas.Add(empresa);

            // Con el contexto de A, el WITH CHECK rechazaría la fila sellada con B (42501).
            await contexto.SaveChangesAsync();
        });

        (await TenantDeEmpresaComoPropietarioAsync(empresa.Id)).Should().Be(TenantB);
    }

    [Fact]
    public async Task Retirar_la_sesion_privilegiada_a_mitad_de_operacion_devuelve_el_rol_en_el_comando_siguiente()
    {
        var puerta = new PuertaAccesoDatos();
        var seleccion = new SeleccionMutable { SesionPrivilegiadaIdSeleccionada = Guid.NewGuid() };
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), seleccion, puerta);
        await using var _ = contexto;

        await puerta.EjecutarAsync(async () =>
        {
            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "control: con sesión privilegiada la conexión adopta el rol de solo lectura");
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA], "y sigue acotada por RLS");

            seleccion.SesionPrivilegiadaIdSeleccionada = null;
            var (rol, esSoporte) = await RolActualAsync(contexto);
            esSoporte.Should().BeFalse($"retirada la sesión, el comando siguiente ya no corre como soporte (corrió como {rol})");
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);

            seleccion.SesionPrivilegiadaIdSeleccionada = Guid.NewGuid();
            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "y al volver a seleccionarla, el siguiente vuelve a ser de solo lectura");
        });

        aperturas.Aperturas.Should().Be(1);
    }

    /// <summary>
    /// La elevación de Aprovisionamiento con la conexión retenida: antes casi
    /// siempre la encontraba cerrada; ahora la encuentra abierta, hace su
    /// <c>SET ROLE</c> crudo y lo anota. El rol tiene que seguir al ámbito en
    /// el comando siguiente —dentro, aprovisionamiento; fuera, o con un ámbito
    /// de otro Tenant, el de soporte— y la anotación tiene que evitar volver a
    /// firmar (si faltara, el token cambiaría tras la elevación).
    /// </summary>
    [Fact]
    public async Task La_elevacion_de_aprovisionamiento_sigue_al_ambito_con_la_conexion_retenida()
    {
        var puerta = new PuertaAccesoDatos();
        var sesion = Guid.NewGuid();
        var seleccion = new SeleccionMutable { SesionPrivilegiadaIdSeleccionada = sesion };
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), seleccion, puerta);
        await using var _ = contexto;
        var elevacion = new ElevacionEscrituraPrivilegiada(contexto);

        await puerta.EjecutarAsync(async () =>
        {
            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true), "control: soporte antes del ámbito");

            using (AmbitoEscrituraPrivilegiada.Establecer(sesion, TenantA))
            {
                await elevacion.ElevarSiConexionAbiertaAsync();
                var tokenTrasElevar = await TokenAsync(contexto);
                (await RolActualAsync(contexto)).Rol.Should().Be("cae_app_aprovisionamiento",
                    "dentro del ámbito, con la conexión ya abierta, el comando corre con el rol de escritura acotada");
                (await TokenAsync(contexto)).Should().Be(tokenTrasElevar,
                    "el rol adoptado por la elevación queda anotado: no hay que volver a firmar");
                await elevacion.DevolverSiConexionAbiertaAsync();
            }

            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "fuera del ámbito el rol vuelve al de solo lectura");

            using (AmbitoEscrituraPrivilegiada.Establecer(sesion, TenantB))
            {
                (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                    "un ámbito de otro Tenant no eleva nada");
            }

            using (AmbitoEscrituraPrivilegiada.Establecer(sesion, TenantA))
            {
                (await RolActualAsync(contexto)).Rol.Should().Be("cae_app_aprovisionamiento",
                    "sin la llamada explícita a Elevar, la realineación del comando adopta el rol del ámbito");
            }

            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "y sin Devolver, el comando siguiente al ámbito lo deshace");
        });

        aperturas.Aperturas.Should().Be(1);
    }

    /// <summary>
    /// PostgreSQL deshace con el ROLLBACK el <c>SET ROLE</c> de vuelta hecho
    /// dentro de la transacción y devuelve la conexión al rol de
    /// aprovisionamiento que tenía al empezarla. Lo anotado (soporte) ya no es
    /// la verdad: el comando siguiente, fuera de la transacción, tiene que
    /// volver a aplicar la sesión y no correr con escritura elevada.
    /// </summary>
    [Fact]
    public async Task Un_rollback_que_deshace_la_vuelta_del_rol_no_deja_escritura_elevada_fuera_del_ambito()
    {
        var puerta = new PuertaAccesoDatos();
        var sesion = Guid.NewGuid();
        var seleccion = new SeleccionMutable { SesionPrivilegiadaIdSeleccionada = sesion };
        var (contexto, _) = CrearContextoRuntime(new TenantMutable(TenantA), seleccion, puerta);
        await using var __ = contexto;
        var elevacion = new ElevacionEscrituraPrivilegiada(contexto);
        var estrategia = contexto.Database.CreateExecutionStrategy();

        await puerta.EjecutarAsync(async () =>
        {
            var ambito = AmbitoEscrituraPrivilegiada.Establecer(sesion, TenantA);
            (await RolActualAsync(contexto)).Rol.Should().Be("cae_app_aprovisionamiento", "control: dentro del ámbito");

            var rolDentroTrasDevolver = await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await contexto.Database.BeginTransactionAsync();
                ambito.Dispose();
                await elevacion.DevolverSiConexionAbiertaAsync();
                var rol = (await RolActualAsync(contexto)).Rol;
                await transaccion.RollbackAsync();
                return rol;
            });
            rolDentroTrasDevolver.Should().Be("cae_app_soporte", "control: la vuelta dentro de la transacción se aplicó");

            (await RolActualAsync(contexto)).Should().Be(("cae_app_soporte", true),
                "tras el ROLLBACK la conexión volvería a aprovisionamiento si nada realineara");
        });
    }

    /// <summary>
    /// Un error transitorio dentro de la transacción: la estrategia de
    /// reintentos repite la unidad entera sobre la conexión retenida (sin
    /// reabrirla) y el segundo intento, tras el ROLLBACK del primero, vuelve a
    /// aplicar la sesión y ve lo del Tenant correcto.
    /// </summary>
    [Fact]
    public async Task Un_reintento_por_error_transitorio_dentro_de_la_operacion_reaplica_la_sesion_y_no_reabre()
    {
        var puerta = new PuertaAccesoDatos();
        var fallo = new FalloTransitorioUnaVez();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta, extra: fallo);
        await using var _ = contexto;
        var estrategia = contexto.Database.CreateExecutionStrategy();
        var intentos = 0;

        var visibles = await puerta.EjecutarAsync(() => estrategia.ExecuteAsync(async () =>
        {
            intentos++;
            await using var transaccion = await contexto.Database.BeginTransactionAsync();
            fallo.Armado = intentos == 1;
            var leidos = await TenantsVisiblesAsync(contexto);
            await transaccion.CommitAsync();
            return leidos;
        }));

        fallo.Lanzados.Should().Be(1, "control: el fallo transitorio se produjo de verdad");
        intentos.Should().Be(2, "la estrategia repitió la unidad");
        visibles.Should().Equal([TenantA]);
        aperturas.Aperturas.Should().Be(1, "el reintento reutiliza la conexión retenida");
        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
    }

    [Fact]
    public async Task Un_cambio_de_Tenant_entre_dos_transacciones_de_la_misma_operacion_llega_a_la_segunda()
    {
        var puerta = new PuertaAccesoDatos();
        var tenant = new TenantMutable(TenantA);
        var (contexto, aperturas) = CrearContextoRuntime(tenant, new SeleccionMutable(), puerta);
        await using var _ = contexto;
        var estrategia = contexto.Database.CreateExecutionStrategy();

        var (primera, segunda) = await puerta.EjecutarAsync(async () =>
        {
            var enPrimera = await EnTransaccionAsync();
            tenant.TenantId = TenantB;
            // Sin comando entre medias: la realineación tiene que ocurrir al
            // empezar la transacción, porque dentro de ella ya no se realinea.
            var enSegunda = await EnTransaccionAsync();
            return (enPrimera, enSegunda);
        });

        primera.Should().Equal([TenantA], "control positivo de la primera transacción");
        segunda.Should().Equal([TenantB], "la segunda transacción empieza con el contexto nuevo");
        aperturas.Aperturas.Should().Be(1);

        Task<List<Guid>> EnTransaccionAsync() => estrategia.ExecuteAsync(async () =>
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync();
            var visibles = await TenantsVisiblesAsync(contexto);
            await transaccion.CommitAsync();
            return visibles;
        });
    }

    [Fact]
    public async Task Una_excepcion_a_mitad_de_operacion_cierra_la_conexion()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;

        var accion = () => puerta.EjecutarAsync(async () =>
        {
            await TokenAsync(contexto);
            contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Open, "control: retenida");
            throw new InvalidOperationException("fallo a mitad de la operación");
        });

        await accion.Should().ThrowAsync<InvalidOperationException>();
        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed,
            "una excepción no deja la conexión abierta fuera de la operación");

        (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA],
            "el contexto sigue utilizable: la apertura siguiente prepara la sesión de cero");
        aperturas.Aperturas.Should().Be(2);
    }

    [Fact]
    public async Task Una_transaccion_por_la_estrategia_de_reintentos_funciona_dentro_de_la_operacion()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;
        var empresa = Empresa.CrearComoCliente(
            "Escrita en transacción S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);

        await puerta.EjecutarAsync(async () =>
        {
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);

            var estrategia = contexto.Database.CreateExecutionStrategy();
            estrategia.RetriesOnFailure.Should().BeTrue("control: la estrategia es la de reintentos de producción");
            await estrategia.ExecuteAsync(async () =>
            {
                await using var transaccion = await contexto.Database.BeginTransactionAsync();
                contexto.Empresas.Add(empresa);
                await contexto.SaveChangesAsync();
                await transaccion.CommitAsync();
            });

            contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Open,
                "cerrar la transacción no devuelve la conexión retenida por la operación");
        });

        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        aperturas.Aperturas.Should().Be(1);
        (await TenantDeEmpresaComoPropietarioAsync(empresa.Id)).Should().Be(TenantA);
    }

    [Fact]
    public async Task Una_conexion_que_abrio_otro_no_la_cierra_la_puerta()
    {
        var puerta = new PuertaAccesoDatos();
        var (contexto, aperturas) = CrearContextoRuntime(new TenantMutable(TenantA), new SeleccionMutable(), puerta);
        await using var _ = contexto;

        await contexto.Database.OpenConnectionAsync();
        try
        {
            await puerta.EjecutarAsync(async () =>
            {
                (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);
                await TokenAsync(contexto);
            });

            contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Open,
                "la apertura de fuera sigue siendo de quien la hizo");
            (await TenantsVisiblesAsync(contexto)).Should().Equal([TenantA]);
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
        }

        contexto.Database.GetDbConnection().State.Should().Be(ConnectionState.Closed);
        aperturas.Aperturas.Should().Be(1);
    }

    // ── Andamiaje (también lo usa ConexionPorOperacionPoolTests) ───────────

    internal (CaeManagerDbContext Contexto, ContadorAperturas Aperturas) CrearContextoRuntime(
        TenantMutable tenant, SeleccionMutable seleccion, PuertaAccesoDatos puerta, string? cadena = null,
        Guid? usuarioId = null, IInterceptor? extra = null)
    {
        var aperturas = new ContadorAperturas();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(CadenaPropietario),
                npgsql => npgsql.EnableRetryOnFailure())
            // Mismo orden que ConfiguracionDeContexto: el interceptor RLS delante del sellado.
            .AddInterceptors(
                new TenantRlsConnectionInterceptor(
                    tenant, seleccion, new UsuarioDelTenant(usuarioId ?? Guid.NewGuid(), () => tenant.TenantId),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls, puerta),
                new TenantSelladoInterceptor(tenant, puertaAccesoDatos: puerta),
                aperturas)
            .AddInterceptors(extra is null ? Array.Empty<IInterceptor>() : new[] { extra })
            .Options;

        return (new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant), aperturas);
    }

    internal static Task<string> TokenAsync(CaeManagerDbContext contexto) =>
        contexto.Database
            .SqlQueryRaw<string>("SELECT coalesce(current_setting('app.contexto', true), '') AS \"Value\"")
            .SingleAsync();

    internal static Task<int> PidAsync(CaeManagerDbContext contexto) =>
        contexto.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    internal static Task<List<Guid>> TenantsVisiblesAsync(CaeManagerDbContext contexto) =>
        contexto.AsignacionesOperacion.IgnoreQueryFilters().Where(a => a.EsRaiz).Select(a => a.PropietarioTenantId).ToListAsync();

    private static async Task<(string Rol, bool EsSoporte)> RolActualAsync(CaeManagerDbContext contexto)
    {
        var rol = await contexto.Database
            .SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync();
        return (rol, rol == "cae_app_soporte");
    }

    private async Task<Guid> TenantDeEmpresaComoPropietarioAsync(Guid empresaId)
    {
        await using var conexion = new NpgsqlConnection(CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT \"TenantId\" FROM \"Empresas\" WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", empresaId);
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    internal sealed class TenantMutable(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; set; } = tenantId;
    }

    internal sealed class SeleccionMutable : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada { get; set; }
    }

    /// <summary>
    /// Usuario cuyo Tenant de origen es el Tenant activo en cada instante: así un
    /// cambio de Tenant a mitad de operación cambia el contexto entero (objetivo y
    /// origen) sin depender de Asignaciones de Operación entre Tenants, que no son
    /// lo que este test mide. Lo que mide es que el contexto nuevo llegue al
    /// comando siguiente, sea cual sea la coordenada que cambió.
    /// </summary>
    private sealed class UsuarioDelTenant(Guid usuarioId, Func<Guid?> tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
    /// <summary>
    /// Lanza una vez, mientras está armado, el mismo tipo de error que la
    /// estrategia de reintentos de Npgsql trata como transitorio (una
    /// <see cref="NpgsqlException"/> por E/S), antes de que el comando llegue a
    /// la base. Va registrado detrás del interceptor RLS, así que la conexión ya
    /// está alineada y retenida cuando falla.
    /// </summary>
    internal sealed class FalloTransitorioUnaVez : DbCommandInterceptor
    {
        public bool Armado;
        public int Lanzados;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Armado)
            {
                Armado = false;
                Lanzados++;
                throw new NpgsqlException("Fallo transitorio de prueba", new System.IO.IOException("corte simulado"));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
    /// <summary>Aperturas físicas de la conexión que EF hizo para este contexto.</summary>
    internal sealed class ContadorAperturas : DbConnectionInterceptor
    {
        public int Aperturas;

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
            Interlocked.Increment(ref Aperturas);

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Aperturas);
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// La conexión física que una operación de la puerta retuvo vuelve al pool sin
/// el contexto ni el rol de su usuario, y el siguiente usuario que la saca solo
/// ve lo suyo. Necesita que el pool devuelva el mismo backend, así que corre en
/// <see cref="ColeccionPoolNpgsqlSinVaciadosConcurrentes"/> (ver su porqué) y
/// reutiliza la base y el andamiaje de <see cref="ConexionPorOperacionTests"/>
/// por composición. Clase de primer nivel por el mismo motivo que
/// <see cref="ContextoRlsFirmadoPoolTests"/>.
/// </summary>
[Collection(ColeccionPoolNpgsqlSinVaciadosConcurrentes.Nombre)]
public sealed class ConexionPorOperacionPoolTests : IAsyncLifetime
{
    private readonly ConexionPorOperacionTests _clase = new();

    public Task InitializeAsync() => _clase.InitializeAsync();

    public Task DisposeAsync() => _clase.DisposeAsync();

    [Fact]
    public async Task Dos_usuarios_que_reutilizan_la_misma_conexion_fisica_solo_ven_lo_suyo()
    {
        var cadena = CadenaConUnSoloHueco("conexion-por-operacion-dos-usuarios");

        var puertaA = new PuertaAccesoDatos();
        var (contextoA, _) = _clase.CrearContextoRuntime(
            new ConexionPorOperacionTests.TenantMutable(_clase.TenantA), new ConexionPorOperacionTests.SeleccionMutable(),
            puertaA, cadena);
        await using var a = contextoA;

        var puertaB = new PuertaAccesoDatos();
        var (contextoB, _) = _clase.CrearContextoRuntime(
            new ConexionPorOperacionTests.TenantMutable(_clase.TenantB), new ConexionPorOperacionTests.SeleccionMutable(),
            puertaB, cadena);
        await using var b = contextoB;

        var (pidA, tokenA) = await puertaA.EjecutarAsync(async () =>
        {
            (await ConexionPorOperacionTests.TenantsVisiblesAsync(contextoA)).Should().Equal([_clase.TenantA]);
            return (await ConexionPorOperacionTests.PidAsync(contextoA), await ConexionPorOperacionTests.TokenAsync(contextoA));
        });

        var (pidB, tokenB, visiblesB) = await puertaB.EjecutarAsync(async () =>
        {
            var pid = await ConexionPorOperacionTests.PidAsync(contextoB);
            var token = await ConexionPorOperacionTests.TokenAsync(contextoB);
            return (pid, token, await ConexionPorOperacionTests.TenantsVisiblesAsync(contextoB));
        });

        pidB.Should().Be(pidA, "el test solo demuestra algo si es el mismo backend físico sacado del pool");
        tokenB.Should().NotBe(tokenA);
        visiblesB.Should().Equal([_clase.TenantB], "el usuario B nunca hereda el contexto de la operación de A");
    }

    [Fact]
    public async Task Tras_una_excepcion_a_mitad_la_conexion_vuelve_al_pool_sin_contexto_ni_rol()
    {
        var cadena = CadenaConUnSoloHueco("conexion-por-operacion-excepcion");
        var puerta = new PuertaAccesoDatos();
        var seleccion = new ConexionPorOperacionTests.SeleccionMutable { SesionPrivilegiadaIdSeleccionada = Guid.NewGuid() };
        var (contexto, _) = _clase.CrearContextoRuntime(
            new ConexionPorOperacionTests.TenantMutable(_clase.TenantA), seleccion, puerta, cadena);
        await using var _c = contexto;
        var pid = 0;

        var accion = () => puerta.EjecutarAsync(async () =>
        {
            pid = await ConexionPorOperacionTests.PidAsync(contexto);
            (await ConexionPorOperacionTests.TenantsVisiblesAsync(contexto)).Should().Equal([_clase.TenantA],
                "control positivo: la conexión lleva el contexto de A y el rol de soporte");
            throw new InvalidOperationException("fallo a mitad de la operación");
        });
        await accion.Should().ThrowAsync<InvalidOperationException>();

        await using var reutilizada = new NpgsqlConnection(cadena);
        await reutilizada.OpenAsync();
        reutilizada.ProcessID.Should().Be(pid, "el mismo backend físico, devuelto al pool por la puerta");

        await using var comando = reutilizada.CreateCommand();
        comando.CommandText =
            "SELECT coalesce(current_setting('app.contexto', true), ''), current_user::text = session_user::text, " +
            "(SELECT count(*) FROM \"AsignacionesOperacion\" WHERE \"EsRaiz\");";
        await using var lector = await comando.ExecuteReaderAsync();
        await lector.ReadAsync();
        lector.GetString(0).Should().BeEmpty("el contexto firmado no sobrevive a la devolución al pool");
        lector.GetBoolean(1).Should().BeTrue("ni el rol de soporte");
        lector.GetInt64(2).Should().Be(0, "sin contexto, RLS no deja ver ninguna fila");
    }

    /// <summary>
    /// Pool propio (Application Name distinto = cadena distinta = pool distinto)
    /// con un solo hueco: la segunda apertura tiene que ser el mismo backend.
    /// </summary>
    private string CadenaConUnSoloHueco(string nombre) =>
        new NpgsqlConnectionStringBuilder(BaseDatosPostgresDePruebas.CadenaComoRuntime(_clase.CadenaPropietario))
        {
            ApplicationName = nombre,
            MaxPoolSize = 1,
        }.ConnectionString;
}
