using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Data.Common;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>El contexto del lote sellado vive solo dentro del ámbito transaccional del
/// propio lote</b> (revisión puente de la sexta ronda de la conexión por
/// operación, hallazgos 2 y 3).
///
/// <para>
/// <c>TenantSelladoInterceptor</c> prepara el lote al empezar el
/// <c>SaveChanges</c> y mueve <c>app.tenant_id</c> y <c>app.contexto</c> antes del
/// primer comando del lote, ya dentro de su transacción propia o del punto de
/// guardado que EF crea en la del llamador. Aquí se mide: que un lote preparado
/// que no llega a ejecutarse no mueve nada aunque el llamador abra después una
/// transacción o un punto de guardado; que un lote de un solo comando dentro de
/// una transacción del llamador tiene su punto de guardado; el éxito, el fallo no
/// de concurrencia, el fallo al volver al punto de guardado y un segundo lote tras
/// un conflicto, todos dentro de una transacción del llamador; los reintentos de
/// la estrategia de ejecución; y, aislado, el punto de guardado del llamador tras
/// un conflicto (quinta ronda de Codex, ALTO 1).
/// </para>
///
/// <para>
/// Cada lectura de la sesión se hace por un comando directo de Npgsql, que no pasa
/// por ningún interceptor, además de por EF donde importa: un comando directo no
/// dispara ninguna realineación que tape un contexto olvidado.
/// </para>
/// </summary>
public sealed partial class LoteMixtoIdentidadDominioFallaCerradoTests
{
    // ── Hallazgo 2: un lote preparado que no llega a ejecutarse ─────────────────

    /// <summary>
    /// Otro interceptor de <c>SavingChanges</c> registrado detrás del sellado lanza
    /// con el lote ya preparado: EF no avisa por ningún final. Sin transacción del
    /// llamador, el lote preparado esperaba el <c>BEGIN</c> de su transacción
    /// propia; un <c>BeginTransaction</c> del llamador antes del <c>SaveChanges</c>
    /// siguiente no puede mover el contexto al Tenant de la cuenta.
    /// </summary>
    [Fact]
    public async Task B_Un_lote_preparado_que_no_llega_a_ejecutarse_no_mueve_el_contexto_en_una_transaccion_posterior_del_llamador()
    {
        var (tenantCuenta, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var lanzador = new LanzaEnSavingChangesUnaVez { Armado = true };

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion, lanzador);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000031";

        (await CapturarAsync(() => sesion.Contexto.SaveChangesAsync()))
            .Should().BeOfType<FalloDePruebaEnSavingChanges>("precondición: el interceptor de detrás del sellado lanzó");
        sesion.Contexto.Database.AutoTransactionBehavior.Should().Be(AutoTransactionBehavior.Always,
            "control del instrumento: el sellado preparó el lote y ningún final de EF lo cerró");

        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "el BEGIN del llamador no es el del lote: un comando directo sigue con el Tenant de sesión");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "y con el contexto firmado del Tenant de sesión");
            (await LeerTenantDeSesionPorEfAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "un comando de EF dentro de la transacción del llamador también");
            await transaccion.RollbackAsync();
        }

        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
            "tras el ROLLBACK del llamador la conexión sigue con el Tenant de sesión");

        // El SaveChanges siguiente cierra el lote preparado y ejecuta el suyo.
        await sesion.Contexto.SaveChangesAsync();

        sesion.Contexto.Database.AutoTransactionBehavior.Should().Be(AutoTransactionBehavior.WhenNeeded,
            "el lote siguiente devuelve al contexto el comportamiento del llamador");
        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
        (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
        (await TenantDeLaUltimaAuditoriaAsync(cuentaId)).Should().Be(tenantCuenta,
            "control: el lote siguiente sí se ejecutó con el Tenant de la cuenta");
    }

    /// <summary>
    /// Lo mismo dentro de una transacción del llamador: el lote preparado esperaba
    /// el punto de guardado de EF, y un punto de guardado manual del llamador
    /// (como los de <c>RecalculoEstadoEnLinea</c>) antes del <c>SaveChanges</c>
    /// siguiente no puede mover el contexto, ni al crearlo ni al volver a él.
    /// </summary>
    [Fact]
    public async Task B_Un_lote_preparado_que_no_llega_a_ejecutarse_no_mueve_el_contexto_en_un_punto_de_guardado_manual()
    {
        var (tenantCuenta, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var lanzador = new LanzaEnSavingChangesUnaVez { Armado = true };

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion, lanzador);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);

        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            cuenta.PhoneNumber = "600000032";
            (await CapturarAsync(() => sesion.Contexto.SaveChangesAsync()))
                .Should().BeOfType<FalloDePruebaEnSavingChanges>("precondición: el interceptor de detrás del sellado lanzó");

            await transaccion.CreateSavepointAsync("punto_manual");
            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "el punto de guardado manual no es el del lote: un comando directo sigue con el Tenant de sesión");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "y con el contexto firmado del Tenant de sesión");
            (await LeerTenantDeSesionPorEfAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "un comando de EF tras el punto manual también");
            await transaccion.RollbackToSavepointAsync("punto_manual");
            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "volver al punto manual no reinstala nada del lote");

            // El SaveChanges siguiente, en la misma transacción, ejecuta su lote.
            await sesion.Contexto.SaveChangesAsync();

            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
            await transaccion.CommitAsync();
        }

        (await TenantDeLaUltimaAuditoriaAsync(cuentaId)).Should().Be(tenantCuenta);
        (await FotoAsync(cuentaId, tenantSesion)).TelefonoDeLaCuenta.Should().Be("600000032");
    }

    // ── Hallazgo 3: el lote sellado dentro de una transacción del llamador ──────

    /// <summary>
    /// Un lote de un solo comando que mueve el contexto dentro de una transacción
    /// del llamador (el ámbito cambió tras abrirla, como en el alta de un Gestor
    /// CAE con su Asignación de Cartera): con <see cref="AutoTransactionBehavior.WhenNeeded"/>
    /// EF no crearía punto de guardado para una sola sentencia; el sellado fuerza
    /// <see cref="AutoTransactionBehavior.Always"/> y el comando corre con el Tenant
    /// del ámbito dentro de ese punto, y la conexión vuelve al de antes.
    /// </summary>
    [Fact]
    public async Task B_Un_lote_sellado_de_un_solo_comando_en_una_transaccion_del_llamador_tiene_su_punto_de_guardado_y_restaura_la_sesion()
    {
        var (_, tenantSesion, _) = await EscenarioAsync(conParametroDeSesion: false);
        var tenantDelAmbito = await CrearTenantAsync();
        var puntos = new ContadorDePuntosDeGuardado();
        var lector = new LectorDelLote();

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(
            tenantSesion, puntos, masTrasElSellado: [lector]);
        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            (await LeerTenantDeSesionPorEfAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "precondición: la transacción del llamador empezó con el Tenant de sesión");

            _sesion.TenantId = tenantDelAmbito;
            sesion.Contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarInicial, UmbralRojoInicial));
            await sesion.Contexto.SaveChangesAsync();

            puntos.Creados.Should().Be(1, "EF creó el punto de guardado del lote aunque es de un solo comando");
            lector.Tenants.Should().ContainSingle("el lote es de un solo comando")
                .Which.Should().Be(tenantDelAmbito.ToString(),
                    "el único comando del lote corrió con el Tenant del ámbito, dentro de su punto de guardado");
            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras el éxito, un comando directo vuelve a ver lo que la conexión tenía antes del lote");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "y el contexto firmado de antes del lote");
            await transaccion.CommitAsync();
        }

        (await FotoAsync(Guid.Empty, tenantDelAmbito)).FilasDeParametro.Should().Be(1,
            "el parámetro quedó escrito y sellado con el Tenant del ámbito");
    }

    /// <summary>
    /// Un lote sellado que falla sin conflicto de concurrencia (42501) dentro de
    /// una transacción del llamador: EF vuelve a su punto de guardado, que deshace
    /// el movimiento, y la transacción sigue usable con el Tenant de sesión.
    /// </summary>
    [Fact]
    public async Task B_Un_fallo_no_de_concurrencia_de_un_lote_sellado_en_una_transaccion_del_llamador_restaura_la_sesion_y_la_deja_usable()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            sesion.Contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarInicial, UmbralRojoInicial));
            var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
            cuenta.PhoneNumber = "600000034";

            var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

            excepcion.Should().BeOfType<DbUpdateException>("precondición: el lote falla sin conflicto de concurrencia");
            SqlStateDe(excepcion).Should().Be("42501");
            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras el fallo, un comando directo dentro de la transacción ve el Tenant de sesión");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "y el contexto firmado del Tenant de sesión");
            (await LeerTenantFirmadoPorEfAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "también por EF");

            sesion.Contexto.ChangeTracker.Clear();
            sesion.Contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarRecuperacion, UmbralRojoRecuperacion));
            await sesion.Contexto.SaveChangesAsync();
            await transaccion.CommitAsync();
        }

        var despues = await FotoAsync(cuentaId, tenantSesion);
        despues.FilasDeAuditoria.Should().Be(antes.FilasDeAuditoria, "el lote fallido no dejó su auditoría");
        despues.TelefonoDeLaCuenta.Should().Be(antes.TelefonoDeLaCuenta, "ni el cambio de la cuenta");
        despues.UmbralAmbar.Should().Be(UmbralAmbarRecuperacion,
            "la escritura posterior en la misma transacción corrió con el Tenant de sesión");
    }

    /// <summary>
    /// Rama <c>TransactionFailed</c> del sellado: tras un conflicto de concurrencia
    /// dentro de una transacción del llamador EF no puede volver al punto de
    /// guardado del lote (lo registra y se lo traga). El conflicto no es un error
    /// de la base, así que la transacción sigue viva con el contexto movido: el
    /// sellado lo devuelve en ese momento, antes de que la excepción llegue al
    /// llamador.
    /// </summary>
    [Fact]
    public async Task B_Si_EF_no_puede_volver_al_punto_de_guardado_tras_un_conflicto_el_sellado_restaura_la_sesion_en_ese_momento()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);
        var fallo = new FallaAlVolverAlPuntoDeGuardado();

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion, fallo);
        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            await PrepararConflictoAsync(sesion, cuentaId, "600000035");
            fallo.Armado = true;

            (await CapturarAsync(() => sesion.Contexto.SaveChangesAsync()))
                .Should().BeOfType<DbUpdateConcurrencyException>("precondición: el lote termina en conflicto");
            fallo.Lanzados.Should().Be(1, "control del instrumento: EF intentó volver al punto de guardado del lote y no pudo");
            fallo.AccionesFallidas.Should().Contain(a => a.Contains("RollbackToSavepoint"),
                "control del instrumento: EF avisó por TransactionFailed, la rama que mide este test");

            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "sin vuelta al punto de guardado, el sellado devolvió el Tenant de sesión: un comando directo lo ve");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "y el contexto firmado del Tenant de sesión");
            await transaccion.RollbackAsync();
        }

        (await FotoAsync(cuentaId, tenantSesion)).Should().Be(antes,
            "el ROLLBACK del llamador deshace lo que el punto de guardado no pudo");
        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
    }

    /// <summary>
    /// Un segundo lote sellado en la misma transacción del llamador tras un
    /// conflicto: la vuelta al punto de guardado del primero no deja nada pendiente
    /// que impida o contamine el segundo, que vuelve a mover el contexto en su
    /// propio punto de guardado y lo devuelve.
    /// </summary>
    [Fact]
    public async Task B_Tras_un_conflicto_en_una_transaccion_del_llamador_un_segundo_lote_sellado_en_ella_se_ejecuta_y_restaura_la_sesion()
    {
        var (tenantCuenta, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            await PrepararConflictoAsync(sesion, cuentaId, "600000036");
            (await CapturarAsync(() => sesion.Contexto.SaveChangesAsync()))
                .Should().BeOfType<DbUpdateConcurrencyException>("precondición: el primer lote termina en conflicto");

            sesion.Contexto.ChangeTracker.Clear();
            var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
            cuenta.PhoneNumber = "600000037";
            await sesion.Contexto.SaveChangesAsync();

            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras el segundo lote, un comando directo ve el Tenant de sesión");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "y el contexto firmado del Tenant de sesión");
            await transaccion.CommitAsync();
        }

        (await TenantDeLaUltimaAuditoriaAsync(cuentaId)).Should().Be(tenantCuenta,
            "el segundo lote se ejecutó con el Tenant de la cuenta");
        (await FotoAsync(cuentaId, tenantSesion)).TelefonoDeLaCuenta.Should().Be("600000037");
    }

    /// <summary>
    /// La estrategia de reintentos repite el lote sellado tras un fallo
    /// transitorio en su primer comando: cada intento abre su transacción, vuelve a
    /// mover el contexto antes de su primer comando (el anterior lo deshizo su
    /// <c>ROLLBACK</c>) y el éxito final lo devuelve.
    /// </summary>
    [Fact]
    public async Task B_Un_lote_sellado_que_la_estrategia_de_reintentos_repite_mueve_el_contexto_en_cada_intento_y_lo_restaura()
    {
        var (tenantCuenta, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var lector = new LectorDelLote { FallarUnaVez = true };

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion, lector, conReintentos: true);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000038";

        await sesion.Contexto.SaveChangesAsync();

        lector.Fallos.Should().Be(1, "control del instrumento: el primer intento falló con un error transitorio y se repitió");
        lector.Tenants.Should().HaveCountGreaterThanOrEqualTo(2)
            .And.OnlyContain(t => t == tenantCuenta.ToString(),
                "cada intento movió el contexto dentro de su propia transacción, también el repetido");
        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
            "tras el éxito, un comando directo ve el Tenant de sesión");
        (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
            "y el contexto firmado del Tenant de sesión");
        (await TenantDeLaUltimaAuditoriaAsync(cuentaId)).Should().Be(tenantCuenta);
        (await FotoAsync(cuentaId, tenantSesion)).TelefonoDeLaCuenta.Should().Be("600000038");
    }

    /// <summary>
    /// Quinta ronda de Codex, ALTO 1, aislado: un punto de guardado del llamador
    /// creado justo tras un conflicto, sin ninguna lectura antes (una lectura por
    /// EF ejecutaría la restauración diferida de antes y taparía el defecto), un
    /// comando de EF y la vuelta a ese punto. Si el conflicto dejara el contexto
    /// sellado puesto, el punto lo capturaría y su <c>ROLLBACK TO SAVEPOINT</c> lo
    /// reinstalaría.
    /// </summary>
    [Fact]
    public async Task B_Un_punto_de_guardado_del_llamador_creado_justo_tras_un_conflicto_no_reinstala_el_contexto_sellado()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        await using (var transaccion = await sesion.Contexto.Database.BeginTransactionAsync())
        {
            await PrepararConflictoAsync(sesion, cuentaId, "600000039");
            (await CapturarAsync(() => sesion.Contexto.SaveChangesAsync()))
                .Should().BeOfType<DbUpdateConcurrencyException>("precondición: el lote termina en conflicto");

            await transaccion.CreateSavepointAsync("punto_del_llamador");
            (await sesion.Contexto.ParametrosSistema.CountAsync()).Should().Be(1,
                "control: el comando de EF entre el punto y la vuelta corre con el Tenant de sesión y ve su parámetro");
            await transaccion.RollbackToSavepointAsync("punto_del_llamador");

            (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras volver al punto del llamador, un comando directo sigue con el Tenant de sesión");
            (await LeerTenantFirmadoAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras volver al punto del llamador, un comando directo sigue con el contexto firmado del Tenant de sesión");
            (await LeerTenantDeSesionPorEfAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras volver al punto del llamador, un comando de EF sigue con el Tenant de sesión");
            (await LeerTenantFirmadoPorEfAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
                "tras volver al punto del llamador, un comando de EF sigue con el contexto firmado del Tenant de sesión");
            await transaccion.RollbackAsync();
        }
    }

    // ── Infraestructura de estos tests ───────────────────────────────────────────

    /// <summary>
    /// Un UPDATE del parámetro de Y y un cambio de la cuenta de X en el mismo lote:
    /// con el contexto de X la fila de Y es invisible y EF lanza
    /// <see cref="DbUpdateConcurrencyException"/>.
    /// </summary>
    private static async Task PrepararConflictoAsync(SesionDeTest sesion, Guid cuentaId, string telefono)
    {
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = telefono;
    }

    private sealed class FalloDePruebaEnSavingChanges() : Exception("Fallo de prueba en SavingChanges, detrás del sellado.");

    /// <summary>
    /// Va detrás del sellado y lanza una vez en <c>SavingChanges</c>, con el lote
    /// ya preparado: fuera del <c>try</c> de EF, que no avisa por ningún final.
    /// </summary>
    private sealed class LanzaEnSavingChangesUnaVez : SaveChangesInterceptor
    {
        public bool Armado;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armado)
            {
                Armado = false;
                throw new FalloDePruebaEnSavingChanges();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Puntos de guardado que EF creó (los del lote; el test no crea ninguno a mano).</summary>
    private sealed class ContadorDePuntosDeGuardado : DbTransactionInterceptor
    {
        public int Creados;

        public override Task CreatedSavepointAsync(
            DbTransaction transaction, TransactionEventData eventData, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Creados);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Va detrás del sellado: en cada comando de <c>SaveChanges</c> anota el
    /// <c>app.tenant_id</c> con que va a correr (por un comando directo) y, si se
    /// le pide, lanza una vez el error de E/S que la estrategia de reintentos de
    /// Npgsql trata como transitorio, antes de que el comando llegue a la base.
    /// </summary>
    private sealed class LectorDelLote : DbCommandInterceptor
    {
        public bool FallarUnaVez;
        public int Fallos;
        public List<string?> Tenants { get; } = [];

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await AnotarAsync(command, eventData);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await AnotarAsync(command, eventData);
            return result;
        }

        private async Task AnotarAsync(DbCommand command, CommandEventData eventData)
        {
            if (eventData.CommandSource != CommandSource.SaveChanges)
                return;

            Tenants.Add(await LeerTenantDeConexionAsync(command.Connection!));
            if (FallarUnaVez)
            {
                FallarUnaVez = false;
                Fallos++;
                throw new NpgsqlException("Fallo transitorio de prueba", new System.IO.IOException("corte simulado"));
            }
        }
    }

    /// <summary>
    /// Mientras está armado, impide una vez que EF vuelva a un punto de guardado
    /// (lanza antes de que el <c>ROLLBACK TO SAVEPOINT</c> llegue a la base) y
    /// anota las acciones con que EF avisa de un fallo de transacción.
    /// </summary>
    private sealed class FallaAlVolverAlPuntoDeGuardado : DbTransactionInterceptor
    {
        public bool Armado;
        public int Lanzados;
        public List<string> AccionesFallidas { get; } = [];

        public override ValueTask<InterceptionResult> RollingBackToSavepointAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Armado)
            {
                Armado = false;
                Lanzados++;
                throw new InvalidOperationException("Fallo de prueba al volver al punto de guardado.");
            }

            return base.RollingBackToSavepointAsync(transaction, eventData, result, cancellationToken);
        }

        public override Task TransactionFailedAsync(
            DbTransaction transaction, TransactionErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            AccionesFallidas.Add(eventData.Action);
            return Task.CompletedTask;
        }
    }
}
