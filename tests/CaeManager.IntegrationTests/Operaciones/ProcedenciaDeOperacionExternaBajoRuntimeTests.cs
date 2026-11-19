using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Procedencia de la Asignación de Operación Outbound externa (hallazgos altos de Codex en la PR #2223; opción A
/// de Chris del 2026-11-14) contra Postgres real: los triggers de la migración
/// <c>AnadeProcedenciaDeOperacionExterna</c> y el predicado <c>AmparoDeOrigen</c> de los lectores.
/// <list type="bullet">
/// <item>Una operación que sobrevive a una revocación hecha por la ranura anterior (que solo escribe
/// <c>Activa = false</c>) no vuelve a autorizar al reactivarse la delegación.</item>
/// <item>Otra DelegacionTenant vigente del par no valida una operación nacida de otra.</item>
/// <item>Una operación externa sin origen falla cerrada, aunque el par tenga una delegación vigente.</item>
/// <item>El contador de revocaciones no se puede rebajar y el origen fijado es inmutable.</item>
/// </list>
/// <para>
/// La delegación nace por el alta real (<see cref="CrearDelegacionTenantCommandHandler"/>) bajo el cableado de
/// producción del <see cref="ArnesDeArranqueRuntime"/>. Las operaciones acotadas, que ningún comando de hoy escribe,
/// las siembra el propietario de la base sin fijar el origen, como la ranura anterior: el trigger lo deduce. El
/// lector se ejerce como <c>cae_app_runtime</c>, desde el Workspace del Operador CAE externo.
/// </para>
/// </summary>
public class ProcedenciaDeOperacionExternaBajoRuntimeTests : IAsyncLifetime
{
    private const string Contrasena = "Arnes#2026Seguro";

    private readonly ActorMutable _actor = new();
    private readonly CurrentUserServiceMutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _propietario;
    private Guid _operador;
    private Guid _cliente;
    private ApplicationUser _administradorPropietario = null!;
    private ApplicationUser _gestoraOperador = null!;
    private Guid _delegacion;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, actorAuditoriaPersonalizado: _actor, currentUserServicePersonalizado: _sesion);

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            _propietario = await SembrarTenantAsync(propietarioDeLaBase, "Propietario", PerfilVocabularioTenant.ClienteDirecto);
            _operador = await SembrarTenantAsync(propietarioDeLaBase, "Operador CAE", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);
        }

        await using (var sembrado = ContextoPropietarioDeLaBase(_propietario))
        {
            var cliente = Empresa.CrearComoCliente("Cliente procedencia S.A.", "B10380236", false, null, null);
            sembrado.Empresas.Add(cliente);
            await sembrado.SaveChangesAsync();
            _cliente = cliente.Id;
        }

        _administradorPropietario = await SembrarUsuarioAsync("admin-propietario", _propietario, Roles.Administrador);
        _gestoraOperador = await SembrarUsuarioAsync("gestora-operador", _operador, Roles.GestorCae);

        var alta = await AutorizarAsync(_administradorPropietario);
        alta.EsExitoso.Should().BeTrue(alta.EsFallido ? alta.Error.Codigo : string.Empty);
        _delegacion = alta.Valor;
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task El_alta_real_fija_el_origen_de_la_operacion_del_Tenant_entero_y_la_insercion_sin_origen_lo_deduce()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();

        await using var verificacion = ContextoPropietarioDeLaBase();
        var operaciones = await verificacion.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound)
            .ToListAsync();
        operaciones.Should().HaveCount(2);
        operaciones.Should().OnlyContain(o => o.OrigenId == _delegacion && o.RevocacionesDelOrigen == 0,
            "el writer fija el origen en el alta y el trigger lo deduce en la inserción que no lo trae");
        (await CarterasAmparadasDelOperadorAsync()).Should().Contain(acotada, "control: con su delegación de origen vigente autoriza");
    }

    [Fact]
    public async Task Revocar_en_la_ranura_anterior_y_reactivar_deja_sin_amparo_a_la_operacion_superviviente()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        (await CarterasAmparadasDelOperadorAsync()).Should().Contain(acotada, "control");

        // La ranura anterior solo escribe Activa: el trigger cuenta la revocación igualmente.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        await using (var verificacion = ContextoPropietarioDeLaBase())
        {
            (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones.Should().Be(1);
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).Estado
                .Should().Be(EstadoAsignacion.Vigente, "control: la operación sobrevivió a la revocación");
        }

        (await CarterasAmparadasDelOperadorAsync()).Should().NotContain(acotada,
            "la delegación de origen es la misma y está activa, pero se revocó después de nacer la operación");
    }

    [Fact]
    public async Task Otra_DelegacionTenant_vigente_del_par_no_valida_una_operacion_de_otro_origen()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.DelegacionesTenant.Add(new DelegacionTenant(_operador, _propietario));
            await propietarioDeLaBase.SaveChangesAsync();
        }

        (await CarterasAmparadasDelOperadorAsync()).Should().NotContain(acotada,
            "la operación nació de la delegación revocada; la nueva del par no la sostiene");
    }

    [Fact]
    public async Task Una_operacion_externa_sin_origen_falla_cerrada_aunque_el_par_tenga_una_delegacion_vigente()
    {
        // Sin delegación activa al insertar, el trigger no tiene de dónde deducir el origen.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        var acotada = await SembrarAcotadaConCarteraAsync();
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).OrigenId
                .Should().BeNull("control: nació sin delegación vigente que la amparase");

        (await CarterasAmparadasDelOperadorAsync()).Should().NotContain(acotada, "sin origen, la operación externa no autoriza");
    }

    [Fact]
    public async Task El_contador_de_revocaciones_no_se_rebaja_y_el_origen_fijado_es_inmutable()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true, "Revocaciones" = 0 WHERE "Id" = '{_delegacion}'""");

        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones
                .Should().Be(1, "ningún UPDATE rebaja el contador");

        var resellar = () => EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "RevocacionesDelOrigen" = 1 WHERE "Id" = '{acotada}'""");
        (await resellar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
            "volver a sellar el contador repondría la operación superviviente");

        var reorigen = () => EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "OrigenId" = NULL WHERE "Id" = '{acotada}'""");
        (await reorigen.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        // Cerrarla sí se puede: el trigger solo protege el origen.
        await EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "Estado" = 'Cerrada' WHERE "Id" = '{acotada}'""");
    }

    // ── Ronda 2 tras Codex r1 (PR #2237) ─────────────────────────────────────

    // El test de la ronda 2 «Una_operacion_insertada_antes_que_su_delegacion_en_la_misma_transaccion_queda_sin_origen»
    // se retira al fusionar la #2253: su trigger AFTER INSERT de DelegacionesTenant sella la operación nacida en la misma
    // transacción, con la excepción acotada por TransaccionDeAlta e InicioTransaccionDeAlta, y esta PR lo conserva. El
    // comportamiento vigente lo fija OrigenDeOperacionExternaEscritoresBajoRuntimeTests
    // .La_operacion_insertada_antes_que_su_origen_en_la_misma_transaccion_queda_sellada.

    [Fact]
    public async Task Una_operacion_sin_origen_no_se_sella_despues_ni_con_la_marca_de_la_deduccion_diferida()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        var acotada = await SembrarAcotadaConCarteraAsync();
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        var sellar = () => EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "OrigenId" = '{_delegacion}', "RevocacionesDelOrigen" = 1 WHERE "Id" = '{acotada}'""");
        (await sellar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
            "sellar después repondría una operación que sobrevivió a una revocación");

        // Ninguna marca de sesión abre una excepción (ronda 2 de Codex, alta 1): el trigger no tiene ninguna.
        var conMarca = () => EjecutarComoPropietarioAsync(
            $"""
             SELECT set_config('app.deduccion_origen', '{acotada}', true);
             UPDATE "AsignacionesOperacion" SET "OrigenId" = '{_delegacion}', "RevocacionesDelOrigen" = 1 WHERE "Id" = '{acotada}';
             """);
        (await conMarca.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Una_operacion_sellada_no_cambia_de_par()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();

        var convertirEnInterna = () => EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "OperadorTenantId" = "PropietarioTenantId" WHERE "Id" = '{acotada}'""");
        (await convertirEnInterna.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
            "la rama de las internas borraría el sello");
    }

    [Fact]
    public async Task No_nace_una_operacion_sellada_con_una_delegacion_ya_revocada()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");

        var insertar = () => InsertarConOrigenAsync(_delegacion);

        (await insertar.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(
            e => EsViolacionDeComprobacion(e), "el BEFORE INSERT la rechaza: la delegación de origen ya no está activa");
    }

    [Fact]
    public async Task Una_revocacion_en_vuelo_no_deja_sellar_una_operacion_con_el_contador_viejo()
    {
        await using var revocacion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await revocacion.OpenAsync();
        await using var transaccion = await revocacion.BeginTransactionAsync();
        await using (var comando = new NpgsqlCommand(
                         $"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""", revocacion, transaccion))
            await comando.ExecuteNonQueryAsync();

        // El sellado espera al cerrojo de la revocación: no puede leer el contador anterior a ella.
        var insercion = Task.Run(() => InsertarConOrigenAsync(_delegacion));
        await EsperaDeBloqueoEnPostgres.BloqueadaPorAsync(_arnes.CadenaPropietario, revocacion.ProcessID, insercion,
            "el sellado espera a que la revocación en vuelo termine");

        await transaccion.CommitAsync();

        var fallo = await FluentActions.Awaiting(() => insercion).Should().ThrowAsync<Exception>();
        fallo.Which.Should().Match<Exception>(e => EsViolacionDeComprobacion(e),
            "tras la revocación lee la fila confirmada, con la delegación inactiva y el contador nuevo, y la rechaza");
    }

    [Fact]
    public async Task El_backfill_cierra_la_operacion_sin_origen_con_su_cartera_respeta_la_amparada_y_es_idempotente()
    {
        var amparada = await SembrarAcotadaConCarteraAsync();
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        // Una operación vigente por Cliente empresarial (IX_AsignacionesOperacion_ResponsableRelacionVigente): cada una
        // sobre el suyo.
        var sinOrigen = await SembrarAcotadaConCarteraAsync(await SembrarClienteAsync("Cliente sin origen S.A.", "B10380244"));
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
        // La reactivación no repone la que sobrevivió a la revocación; para la prueba del backfill, una operación con
        // prueba positiva de origen y vigente: la nacida bajo la activación nueva.
        var bajoLaActivacionNueva = await SembrarAcotadaConCarteraAsync(
            await SembrarClienteAsync("Cliente activación nueva S.A.", "B10380251"));
        // La fila heredada de la Gestora CAE, creada tras la reactivación: sin ella el paso 2b cerraría todas sus carteras
        // y el cierre de la de sinOrigen no aislaría el paso 2a.
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae));
            await contexto.SaveChangesAsync();
        }

        await using (var verificacion = ContextoPropietarioDeLaBase())
        {
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == sinOrigen)).OrigenId.Should().BeNull("control");
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == bajoLaActivacionNueva)).RevocacionesDelOrigen
                .Should().Be(1, "control: nació sellada con el contador de la activación vigente");
        }

        await using (var primera = ContextoPropietarioDeLaBase())
            await AsignacionesOperativasBackfillSeeder.SeedAsync(primera, NullLogger.Instance);
        var tras1 = await EstadosAsync(amparada, sinOrigen, bajoLaActivacionNueva);

        tras1[sinOrigen].Should().Be((EstadoAsignacion.Cerrada, EstadoAsignacion.Cerrada),
            "sin prueba positiva de que naciera bajo la activación vigente, se cierra con sus carteras");
        tras1[bajoLaActivacionNueva].Should().Be((EstadoAsignacion.Vigente, EstadoAsignacion.Vigente),
            "el backfill no toca la que guarda su origen");

        await using (var segunda = ContextoPropietarioDeLaBase())
            await AsignacionesOperativasBackfillSeeder.SeedAsync(segunda, NullLogger.Instance);
        (await EstadosAsync(amparada, sinOrigen, bajoLaActivacionNueva)).Should().BeEquivalentTo(tras1, "dos pasadas, el mismo resultado");

        (await CarterasAmparadasDelOperadorAsync()).Should().Contain(bajoLaActivacionNueva)
            .And.NotContain(sinOrigen).And.NotContain(amparada);
    }

    [Fact]
    public async Task Revocar_la_delegacion_revoca_sus_filas_heredadas_y_reactivarla_no_las_repone()
    {
        var fila = new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae);
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(fila);
            await contexto.SaveChangesAsync();
        }

        // Como la ranura anterior: solo escribe Activa.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        await using var verificacion = ContextoPropietarioDeLaBase();
        var guardada = await verificacion.AsignacionesOperadorDelegadoConRevocadas.SingleAsync(a => a.Id == fila.Id);
        guardada.RevocadaEnUtc.Should().NotBeNull("el trigger revoca la fila heredada al contar la revocación");
        (await verificacion.AsignacionesOperadorDelegado.AnyAsync(a => a.Id == fila.Id)).Should().BeFalse(
            "ningún lector, que leen el conjunto sin revocadas, la vuelve a ver");
    }

    [Fact]
    public async Task No_nace_una_fila_heredada_sobre_una_delegacion_revocada()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");

        var insertar = async () =>
        {
            await using var contexto = ContextoPropietarioDeLaBase();
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae));
            await contexto.SaveChangesAsync();
        };

        (await insertar.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(
            e => EsViolacionDeComprobacion(e), "viva tras la revocación, volvería a autorizar al reactivar");
    }

    [Fact]
    public async Task La_migracion_revoca_las_filas_heredadas_previas_de_Operador_CAE_externo()
    {
        var fila = new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae);
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(fila);
            await contexto.SaveChangesAsync();
        }

        await EjecutarComoPropietarioAsync(
            CaeManager.Migrations.PostgreSQL.Migrations.AnadeProcedenciaDeOperacionExterna.RevocaFilasHeredadasPrevias);
        // Dos veces: la segunda no cambia nada.
        await EjecutarComoPropietarioAsync(
            CaeManager.Migrations.PostgreSQL.Migrations.AnadeProcedenciaDeOperacionExterna.RevocaFilasHeredadasPrevias);

        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.AsignacionesOperadorDelegadoConRevocadas.SingleAsync(a => a.Id == fila.Id)).RevocadaEnUtc
            .Should().NotBeNull("sin historia de revocaciones no hay prueba de que no sobreviviera a una");
    }

    // ── Ronda 3 tras Codex r2 (PR #2237) ─────────────────────────────────────

    /// <summary>
    /// Ronda 2 de Codex, alta 2: revocar una fila heredada es definitivo. Un UPDATE que devolviera
    /// <c>RevocadaEnUtc</c> a NULL la devolvería al lector de autorización y, al reactivar la delegación, recuperaría
    /// su efecto; tampoco se recicla una fila para otro rol. Control: una fila viva sí se revoca.
    /// </summary>
    [Fact]
    public async Task Una_fila_heredada_revocada_no_se_des_revoca_ni_cambia_de_rol()
    {
        var fila = new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae);
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(fila);
            await contexto.SaveChangesAsync();
        }

        await EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperadorDelegado" SET "RevocadaEnUtc" = now(), "MotivoRevocacion" = 'Prueba' WHERE "Id" = '{fila.Id}'""");

        foreach (var sql in new[]
                 {
                     $"""UPDATE "AsignacionesOperadorDelegado" SET "RevocadaEnUtc" = NULL WHERE "Id" = '{fila.Id}'""",
                     $"""UPDATE "AsignacionesOperadorDelegado" SET "RevocadaEnUtc" = NULL, "MotivoRevocacion" = NULL WHERE "Id" = '{fila.Id}'""",
                     $"""UPDATE "AsignacionesOperadorDelegado" SET "RevocadaEnUtc" = now() + interval '1 day' WHERE "Id" = '{fila.Id}'""",
                     $"""UPDATE "AsignacionesOperadorDelegado" SET "Rol" = '{Roles.Consulta}' WHERE "Id" = '{fila.Id}'""",
                 })
        {
            var ejecutar = () => EjecutarComoPropietarioAsync(sql);
            (await ejecutar.Should().ThrowAsync<PostgresException>()).Which.SqlState
                .Should().Be(PostgresErrorCodes.CheckViolation, sql);
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        var guardada = await verificacion.AsignacionesOperadorDelegadoConRevocadas.AsNoTracking().SingleAsync(a => a.Id == fila.Id);
        guardada.RevocadaEnUtc.Should().NotBeNull();
        guardada.Rol.Should().Be(Roles.GestorCae);
    }

    /// <summary>
    /// Ronda 2 de Codex, alta 3: con origen explícito, el contador es el que el escritor leyó al decidir. Si entre la
    /// lectura y la inserción la delegación se revocó y se reactivó, o se amplió su caducidad, el contador ya no coincide
    /// y la inserción se rechaza: la decisión anterior no se sella con la activación posterior. Control positivo: con el
    /// contador releído, la misma inserción nace.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_operacion_con_el_contador_leido_antes_de_una_revocacion_o_ampliacion_no_nace(bool ampliando)
    {
        if (ampliando)
            await EjecutarComoPropietarioAsync(
                $"""UPDATE "DelegacionesTenant" SET "ExpiraEnUtc" = now() + interval '1 day' WHERE "Id" = '{_delegacion}'""");

        DelegacionTenant leida;
        await using (var contexto = ContextoPropietarioDeLaBase())
            leida = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);

        if (ampliando)
            await EjecutarComoPropietarioAsync(
                $"""UPDATE "DelegacionesTenant" SET "ExpiraEnUtc" = now() + interval '30 days' WHERE "Id" = '{_delegacion}'""");
        else
        {
            await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
            await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
        }

        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion)).Revocaciones
                .Should().Be(leida.Revocaciones + 1, "control: la activación que se leyó terminó");

        var conElLeido = () => InsertarConOrigenAsync(leida);
        (await conElLeido.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(
            e => EsViolacionDeComprobacion(e), "el contador leído ya no es el de la delegación");

        await InsertarConOrigenAsync(_delegacion);
    }

    /// <summary>
    /// Ronda 2 de Codex, media 7: orden de bloqueo único. Ni los triggers de la procedencia ni la revocación por Soporte
    /// toman un cerrojo consultivo (con él se formaba el ciclo cerrojo consultivo / fila que daba <c>40P01</c>); las
    /// inserciones bloquean la fila de la delegación con <c>FOR SHARE</c> y la revocación por Soporte con
    /// <c>FOR UPDATE</c>. Y la función de sellado no conserva la excepción por profundidad de trigger ni por marca de
    /// sesión (alta 1). Se lee el código desplegado en la base, no la migración: es lo que ejecuta PostgreSQL.
    /// </summary>
    [Fact]
    public async Task La_ruta_de_sellado_y_de_revocacion_no_toma_cerrojos_consultivos()
    {
        await using var conexion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            SELECT p.proname, p.prosrc
              FROM pg_catalog.pg_trigger t
              JOIN pg_catalog.pg_proc p ON p.oid = t.tgfoid
             WHERE NOT t.tgisinternal
               AND t.tgrelid IN ('public."DelegacionesTenant"'::regclass, 'public."AsignacionesOperacion"'::regclass,
                                 'public."AsignacionesOperadorDelegado"'::regclass)
            UNION
            SELECT p.proname, p.prosrc
              FROM pg_catalog.pg_proc p
             WHERE p.proname IN ('app_revocar_operador_cae_externo_por_soporte',
                                 'app_encargo_administracion_exige_amparo_de_origen')
            """, conexion);
        var fuentes = new Dictionary<string, string>();
        await using (var lector = await comando.ExecuteReaderAsync())
            while (await lector.ReadAsync())
                fuentes[lector.GetString(0)] = lector.GetString(1);

        fuentes.Keys.Should().Contain(
            ["app_asignacion_operacion_sella_origen", "app_asignacion_operador_delegado_exige_delegacion_activa",
             "app_asignacion_operador_delegado_revocacion_definitiva", "app_delegacion_tenant_cuenta_revocaciones",
             "app_delegacion_tenant_revoca_filas_heredadas", "app_revocar_operador_cae_externo_por_soporte",
             "app_encargo_administracion_exige_amparo_de_origen"],
            "control: el instrumento ve las funciones de la ruta");
        foreach (var (nombre, fuente) in fuentes)
            fuente.Should().NotContainEquivalentOf("advisory", nombre);
        fuentes["app_asignacion_operacion_sella_origen"].Should().Contain("FOR SHARE")
            .And.NotContain("pg_trigger_depth").And.NotContain("deduccion_origen");
        fuentes["app_asignacion_operador_delegado_exige_delegacion_activa"].Should().Contain("FOR SHARE")
            .And.Contain("RevocacionesDelOrigen", "la fila heredada compara y sella el contador (ronda 3 de Codex, alta 1)");
        fuentes["app_encargo_administracion_exige_amparo_de_origen"].Should().Contain("FOR SHARE",
            "el encargo bloquea la delegación de origen al insertarse (ronda 3 de Codex, media 3)");
        fuentes["app_revocar_operador_cae_externo_por_soporte"].Should().Contain("FOR UPDATE");
    }

    /// <summary>
    /// Ronda 2 de Codex, media 6: una siembra de demostración corre en cada arranque y no es un acto explícito. Tras
    /// revocar y reactivar la delegación (o con una fila heredada revocada, como deja la migración), el par tiene
    /// historia y la siembra no puede crear filas heredadas. Control: el alta recién hecha no tiene historia.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tras_una_revocacion_la_siembra_no_recrea_filas_heredadas(bool soloFilaRevocada)
    {
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion);
            (await HistoriaDelParEnSiembra.TieneHistoriaAsync(contexto, delegacion, CancellationToken.None))
                .Should().BeFalse("control: el alta recién hecha no tiene historia");
            (await HistoriaDelParEnSiembra.PuedeSembrarFilaHeredadaAsync(contexto, delegacion, _gestoraOperador.Id, CancellationToken.None))
                .Should().BeTrue("control: sin historia la siembra sí crea la fila");
        }

        if (soloFilaRevocada)
        {
            var otra = await SembrarUsuarioAsync("consulta-operador", _operador, Roles.Consulta);
            var fila = new AsignacionOperadorDelegado(_delegacion, otra.Id, Roles.Consulta);
            fila.Revocar("Prueba", DateTime.UtcNow);
            await using var contexto = ContextoPropietarioDeLaBase();
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(fila);
            await contexto.SaveChangesAsync();
        }
        else
        {
            await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
            await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        var reactivada = await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion);
        reactivada.Activa.Should().BeTrue("control: la delegación está activa; lo que lo impide es la historia");
        (await HistoriaDelParEnSiembra.TieneHistoriaAsync(verificacion, reactivada, CancellationToken.None)).Should().BeTrue();
        (await HistoriaDelParEnSiembra.PuedeSembrarFilaHeredadaAsync(verificacion, reactivada, _gestoraOperador.Id, CancellationToken.None))
            .Should().BeFalse("lo que fue no vuelve solo, tampoco por la siembra");
    }

    // ── Ronda 4 tras Codex r3 (PR #2237, autorizada por Chris) ───────────────

    /// <summary>
    /// Ronda 3 de Codex, alta 1: la fila heredada que se decide sobre una lectura previa lleva el contador leído, y el
    /// BEFORE INSERT lo compara con el real con la delegación bloqueada. Si entre la lectura y la inserción la delegación
    /// se revocó y se reactivó, o se amplió su caducidad, la fila no nace. Controles: sin contador nace (solo se exige la
    /// delegación activa) y se sella con el real; y la fila sellada ya no cambia de contador.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Una_fila_heredada_con_el_contador_leido_antes_de_una_revocacion_o_ampliacion_no_nace(bool ampliando)
    {
        if (ampliando)
            await EjecutarComoPropietarioAsync(
                $"""UPDATE "DelegacionesTenant" SET "ExpiraEnUtc" = now() + interval '1 day' WHERE "Id" = '{_delegacion}'""");

        DelegacionTenant leida;
        await using (var contexto = ContextoPropietarioDeLaBase())
            leida = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);

        if (ampliando)
            await EjecutarComoPropietarioAsync(
                $"""UPDATE "DelegacionesTenant" SET "ExpiraEnUtc" = now() + interval '30 days' WHERE "Id" = '{_delegacion}'""");
        else
        {
            await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
            await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
        }

        var conElLeido = () => InsertarFilaHeredadaAsync(leida.Revocaciones);
        (await conElLeido.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(
            e => EsViolacionDeComprobacion(e), "el contador leído ya no es el de la delegación");
        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.AsignacionesOperadorDelegadoConRevocadas.AnyAsync(a => a.UsuarioId == _gestoraOperador.Id))
                .Should().BeFalse("no queda ninguna fila");

        var sinContador = await InsertarFilaHeredadaAsync(revocacionesDelOrigen: null);
        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.AsignacionesOperadorDelegadoConRevocadas.AsNoTracking().SingleAsync(a => a.Id == sinContador))
                .RevocacionesDelOrigen.Should().Be(leida.Revocaciones + 1, "control: sin contador nace y se sella con el real");

        var resellar = () => EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperadorDelegado" SET "RevocacionesDelOrigen" = {leida.Revocaciones} WHERE "Id" = '{sinContador}'""");
        (await resellar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
            "el contador sellado de la fila no cambia");
    }

    /// <summary>
    /// Ronda 3 de Codex, alta 1, la carrera: con una revocación y una reactivación en vuelo, la inserción de la fila
    /// heredada espera al cerrojo (<c>FOR SHARE</c>) y, tras el COMMIT, lee la delegación activa con el contador nuevo y
    /// rechaza la fila decidida con el viejo. Aísla la comparación del contador: al terminar, la delegación está activa.
    /// </summary>
    [Fact]
    public async Task Una_revocacion_en_vuelo_no_deja_nacer_una_fila_heredada_con_el_contador_viejo()
    {
        int leido;
        await using (var contexto = ContextoPropietarioDeLaBase())
            leido = (await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion)).Revocaciones;

        await using var revocacion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await revocacion.OpenAsync();
        await using var transaccion = await revocacion.BeginTransactionAsync();
        foreach (var sql in new[]
                 {
                     $"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""",
                     $"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""",
                 })
        {
            await using var comando = new NpgsqlCommand(sql, revocacion, transaccion);
            await comando.ExecuteNonQueryAsync();
        }

        var insercion = Task.Run(() => InsertarFilaHeredadaAsync(leido));
        await EsperaDeBloqueoEnPostgres.BloqueadaPorAsync(_arnes.CadenaPropietario, revocacion.ProcessID, insercion,
            "la inserción espera a que la revocación en vuelo termine");

        await transaccion.CommitAsync();

        var fallo = await FluentActions.Awaiting(() => insercion).Should().ThrowAsync<Exception>();
        fallo.Which.Should().Match<Exception>(e => EsViolacionDeComprobacion(e),
            "tras el COMMIT la delegación está activa, pero con otro contador");
        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion)).Activa
            .Should().BeTrue("control: lo que la rechaza es el contador, no la delegación inactiva");
    }

    /// <summary>
    /// Ronda 3 de Codex, alta 2: el propósito de una DelegacionTenant no cambia. Si cambiara, una de Operador CAE
    /// externo pasaría a Soporte sin contar la revocación ni revocar sus filas heredadas, y al volver conservaría el
    /// amparo. Ningún escritor del código lo cambia (solo lo fija <c>DelegacionTenant.ParaSoporte</c> al crearla).
    /// </summary>
    [Theory]
    [InlineData("""SET "Proposito" = 'Soporte'""")]
    [InlineData("""SET "Activa" = false, "Proposito" = 'Soporte'""")]
    public async Task El_proposito_de_una_delegacion_no_cambia(string cambio)
    {
        var cambiar = () => EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" {cambio} WHERE "Id" = '{_delegacion}'""");

        (await cambiar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        await using var verificacion = ContextoPropietarioDeLaBase();
        var delegacion = await verificacion.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);
        delegacion.Proposito.Should().Be(PropositoDelegacion.OperadorExterno);
        delegacion.Activa.Should().BeTrue("control: el UPDATE entero se rechazó");
    }

    /// <summary>
    /// Ronda 3 de Codex, media 5: una fila heredada viva no prueba que la operación siga abierta. Con la operación del par
    /// cerrada, el par tiene historia y la siembra (<c>CoberturaPantallasSeeder</c>, que ya no mira la fila) no la reabre.
    /// Control: con la misma fila viva y la operación abierta, no hay historia.
    /// </summary>
    [Fact]
    public async Task Con_la_fila_viva_y_la_operacion_cerrada_el_par_tiene_historia()
    {
        await InsertarFilaHeredadaAsync(revocacionesDelOrigen: null);
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            var delegacion = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);
            (await HistoriaDelParEnSiembra.TieneHistoriaAsync(contexto, delegacion, CancellationToken.None))
                .Should().BeFalse("control: fila viva y operación abierta");

            var operaciones = await contexto.AsignacionesOperacion
                .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador)
                .ToListAsync();
            operaciones.Should().NotBeEmpty("control: el alta abrió la operación");
            foreach (var operacion in operaciones)
                operacion.Cerrar(MotivoCierreAsignacion.Transferida, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        var activa = await verificacion.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);
        activa.Activa.Should().BeTrue("control: la delegación sigue activa");
        (await verificacion.AsignacionesOperadorDelegado.AnyAsync(a => a.UsuarioId == _gestoraOperador.Id))
            .Should().BeTrue("control: la fila sigue viva");
        (await HistoriaDelParEnSiembra.TieneHistoriaAsync(verificacion, activa, CancellationToken.None))
            .Should().BeTrue("la operación se cerró: la siembra no la reabre");
    }

    /// <summary>
    /// Ronda 5 de Codex (media, solo demostración): una Asignación de Operación Outbound externa del Tenant entero
    /// suspendida, aunque siga amparada por su DelegacionTenant de origen, es historia del par. Se lee como lo hace la
    /// siembra: <c>cae_app_runtime</c>, sin usuario en la sesión y en el ámbito del Operador CAE externo, así que si
    /// <c>HistoriaDelParEnSiembra</c> dejara de leer las operaciones en el ámbito del Tenant propietario la RLS las
    /// ocultaría y el par saldría sin historia. Control: antes de suspender, sin historia.
    /// </summary>
    [Fact]
    public async Task Bajo_runtime_una_operacion_suspendida_y_amparada_del_par_es_historia_para_la_siembra()
    {
        (await TieneHistoriaComoLaSiembraAsync()).Should().BeFalse("control: la operación del alta, vigente y amparada");

        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            var vigentes = await contexto.AsignacionesOperacion
                .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                            && o.Estado == EstadoAsignacion.Vigente)
                .ToListAsync();
            vigentes.Should().NotBeEmpty("control: el alta abrió la operación");
            foreach (var operacion in vigentes)
                operacion.Suspender();
            await contexto.SaveChangesAsync();
        }

        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion)).Activa
                .Should().BeTrue("control: la delegación de origen sigue activa y la suspendida sigue amparada");

        (await TieneHistoriaComoLaSiembraAsync()).Should().BeTrue(
            "la siembra no abre otra Vigente del Tenant entero durante la suspensión");
    }

    private async Task<bool> TieneHistoriaComoLaSiembraAsync()
    {
        _sesion.UsuarioId = null;
        _sesion.TenantOrigenId = null;
        _sesion.Rol = null;
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(_operador))
        {
            var delegacion = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);
            return await HistoriaDelParEnSiembra.TieneHistoriaAsync(contexto, delegacion, CancellationToken.None);
        }
    }

    private async Task<Guid> InsertarFilaHeredadaAsync(int? revocacionesDelOrigen)
    {
        await using var contexto = ContextoPropietarioDeLaBase();
        var fila = new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae, revocacionesDelOrigen);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(fila);
        await contexto.SaveChangesAsync();
        return fila.Id;
    }

    /// <summary>
    /// La guarda del writer (ronda 2 de Codex, alta 1): el trigger exige la delegación ya guardada y su contador real,
    /// y EF no ordena el UPDATE de la delegación por delante del INSERT de la operación. Con la delegación de origen
    /// modificada sin guardar, <c>AbrirOperacionDelegadaAsync</c> lanza en vez de abrir una nueva. Controles: con la
    /// operación del alta amparada la devuelve sin abrir nada; tras revocar y reactivar, sin cambios pendientes, abre
    /// una nueva sellada con el contador actual.
    /// </summary>
    [Fact]
    public async Task El_writer_no_abre_una_operacion_que_nace_de_una_delegacion_con_cambios_sin_guardar()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(_administradorPropietario, Roles.Administrador);
        var writer = new AsignacionesOperativasWriter(contexto, ambito.ServiceProvider.GetRequiredService<ITenantActual>(), usuarioActual);

        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            (await writer.AbrirOperacionDelegadaAsync(_propietario, _operador, DateTime.UtcNow, null)).OrigenId
                .Should().Be(_delegacion, "control: la operación del alta sigue amparada y se devuelve");
        }

        // Revocar y reactivar deja sin amparo a la del alta: abrir ya tiene que crear una nueva.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion);
            await contexto.Entry(delegacion).ReloadAsync();
            contexto.Entry(delegacion).Property(d => d.ExpiraEnUtc).CurrentValue = DateTime.UtcNow.AddDays(30);
            contexto.Entry(delegacion).State.Should().Be(EntityState.Modified, "control");

            await writer.Invoking(w => w.AbrirOperacionDelegadaAsync(_propietario, _operador, DateTime.UtcNow, null))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*cambios sin guardar*");

            await contexto.Entry(delegacion).ReloadAsync();
            var nueva = await writer.AbrirOperacionDelegadaAsync(_propietario, _operador, DateTime.UtcNow, null);
            nueva.OrigenId.Should().Be(_delegacion, "control: sin cambios pendientes abre una nueva desde la misma delegación");
            nueva.RevocacionesDelOrigen.Should().Be(delegacion.Revocaciones, "control: sellada con el contador actual");
            contexto.Entry(nueva).State.Should().Be(EntityState.Added, "control: es nueva, no la del alta");
        }
    }

    /// <summary>
    /// Cierre y apertura en un solo guardado (revisor-puente del cierre de la ronda 3, media 1): tras revocar y reactivar
    /// por SQL, como la ranura anterior, la operación del Tenant entero del alta sigue Vigente sin amparo y ocupa
    /// <c>IX_AsignacionesOperacion_DelegacionTotalVigente</c>. <c>AbrirOperacionDelegadaAsync</c> la cierra y añade la
    /// nueva en el mismo contexto; un único <c>SaveChanges</c> tiene que emitir el UPDATE de cierre antes del INSERT, o
    /// el índice único lo rechaza con 23505 y la reactivación entera se revierte.
    /// </summary>
    [Fact]
    public async Task Abrir_tras_una_revocacion_por_SQL_cierra_la_del_Tenant_entero_y_guarda_la_nueva_en_un_solo_guardado()
    {
        Guid delAlta;
        await using (var verificacion = ContextoPropietarioDeLaBase())
            delAlta = (await verificacion.AsignacionesOperacion.SingleAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                && o.Servicio == ServicioCae.Outbound && o.Estado != EstadoAsignacion.Cerrada)).Id;

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == delAlta)).Estado
                .Should().Be(EstadoAsignacion.Vigente, "control: la del Tenant entero sobrevivió a la revocación por SQL");

        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(_administradorPropietario, Roles.Administrador);
        var writer = new AsignacionesOperativasWriter(contexto, ambito.ServiceProvider.GetRequiredService<ITenantActual>(), usuarioActual);

        Guid nueva;
        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            nueva = (await writer.AbrirOperacionDelegadaAsync(_propietario, _operador, DateTime.UtcNow, null)).Id;
            nueva.Should().NotBe(delAlta, "control: la del alta ya no está amparada y no se devuelve");
            await contexto.SaveChangesAsync();
        }

        await using (var verificacion = ContextoPropietarioDeLaBase())
        {
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == delAlta)).Estado
                .Should().Be(EstadoAsignacion.Cerrada, "el mismo guardado cerró la del alta");
            var guardada = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == nueva);
            guardada.Estado.Should().NotBe(EstadoAsignacion.Cerrada);
            guardada.OrigenId.Should().Be(_delegacion);
            guardada.RevocacionesDelOrigen.Should().Be(1, "sellada con el contador posterior a la revocación");
        }
    }

    /// <summary>
    /// Reabrir es idempotente aunque el par tenga hoy dos delegaciones de Operador CAE externo activas (la base no lo
    /// impide): con una operación vigente y amparada por su origen, <c>AbrirOperacionDelegadaAsync</c> la devuelve y no
    /// lanza. Sin ella, con dos delegaciones activas, no abre nada (origen ambiguo).
    /// </summary>
    [Fact]
    public async Task Reabrir_devuelve_la_operacion_amparada_aunque_el_par_tenga_dos_delegaciones_activas()
    {
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.DelegacionesTenant.Add(new DelegacionTenant(_operador, _propietario));
            await propietarioDeLaBase.SaveChangesAsync();
            (await propietarioDeLaBase.DelegacionesTenant
                    .CountAsync(d => d.TenantConsultoraId == _operador && d.TenantClienteId == _propietario && d.Activa
                                     && d.Proposito == PropositoDelegacion.OperadorExterno))
                .Should().Be(2, "control: el par tiene dos delegaciones de Operador CAE externo activas");
        }

        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(_administradorPropietario, Roles.Administrador);
        var writer = new AsignacionesOperativasWriter(contexto, ambito.ServiceProvider.GetRequiredService<ITenantActual>(), usuarioActual);

        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            (await writer.AbrirOperacionDelegadaAsync(_propietario, _operador, DateTime.UtcNow, null)).OrigenId
                .Should().Be(_delegacion, "la operación del alta sigue amparada por su origen");
        }

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            await writer.Invoking(w => w.AbrirOperacionDelegadaAsync(_propietario, _operador, DateTime.UtcNow, null))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("*2 delegaciones activas*");
        }
    }

    private async Task InsertarConOrigenAsync(Guid delegacionId)
    {
        await using var contexto = ContextoPropietarioDeLaBase();
        var delegacion = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == delegacionId);
        await InsertarConOrigenAsync(delegacion);
    }

    private async Task InsertarConOrigenAsync(DelegacionTenant delegacion)
    {
        await using var contexto = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var operacion = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_cliente), ahora.AddDays(-1), null, ahora);
        operacion.FijarOrigen(delegacion);
        contexto.AsignacionesOperacion.Add(operacion);
        await contexto.SaveChangesAsync();
    }

    private static bool EsViolacionDeComprobacion(Exception e)
    {
        for (var actual = e; actual is not null; actual = actual.InnerException)
            if (actual is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
                return true;
        return false;
    }

    private async Task<Dictionary<Guid, (EstadoAsignacion Operacion, EstadoAsignacion Cartera)>> EstadosAsync(params Guid[] operaciones)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        var resultado = new Dictionary<Guid, (EstadoAsignacion, EstadoAsignacion)>();
        foreach (var id in operaciones)
        {
            var operacion = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == id);
            var cartera = await verificacion.AsignacionesCartera.SingleAsync(c => c.AsignacionOperacionId == id);
            resultado[id] = (operacion.Estado, cartera.Estado);
        }
        return resultado;
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Una operación Outbound acotada al Cliente empresarial con la cartera de la Gestora CAE, sembrada sin origen
    /// como la escribiría la ranura anterior. Devuelve el Id de la operación.
    /// </summary>
    private async Task<Guid> SembrarAcotadaConCarteraAsync(Guid? cliente = null)
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(cliente ?? _cliente), ahora.AddDays(-1), null, ahora);
        propietarioDeLaBase.AsignacionesOperacion.Add(acotada);
        await propietarioDeLaBase.SaveChangesAsync();
        propietarioDeLaBase.AsignacionesCartera.Add(AsignacionCartera.Externa(
            acotada, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
        await propietarioDeLaBase.SaveChangesAsync();
        return acotada.Id;
    }

    private async Task<Guid> SembrarClienteAsync(string razonSocial, string cif)
    {
        await using var sembrado = ContextoPropietarioDeLaBase(_propietario);
        var cliente = Empresa.CrearComoCliente(razonSocial, cif, false, null, null);
        sembrado.Empresas.Add(cliente);
        await sembrado.SaveChangesAsync();
        return cliente.Id;
    }

    /// <summary>Operaciones cuyas carteras autorizan al Operador CAE externo, leídas como <c>cae_app_runtime</c>.</summary>
    private async Task<List<Guid>> CarterasAmparadasDelOperadorAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        FijarSesion(_gestoraOperador, Roles.GestorCae);
        using (AmbitoTenantExplicito.Establecer(_operador))
            return await TenantsBeneficiariosAutorizados
                .CarterasPorOperacionDeLaOrganizacion(contexto, contexto, _operador, DateTime.UtcNow)
                .Select(v => v.Operacion.Id)
                .ToListAsync();
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        await propietarioDeLaBase.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task<Result<Guid>> AutorizarAsync(ApplicationUser usuario)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, Roles.Administrador);
        var tenant = servicios.GetRequiredService<ITenantActual>();

        var handler = new CrearDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), contexto,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual),
            new AutorizacionDelegacionPorAdministradorDelCliente(servicios.GetRequiredService<UserManager<ApplicationUser>>()),
            usuarioActual, contexto, new TransaccionDeComando(contexto), new AsignacionAutomaticaInerte(), tenant, OutboundTenantTecAbierto.Instancia);

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new CrearDelegacionTenantCommand(_operador, _propietario), CancellationToken.None);
    }

    private CurrentUserServiceFalso FijarSesion(ApplicationUser usuario, string rol)
    {
        _actor.Actual = usuario.Id;
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;
        _sesion.Rol = rol;
        return new CurrentUserServiceFalso(usuario.Id, rol, usuario.TenantId);
    }

    private async Task<ApplicationUser> SembrarUsuarioAsync(string alias, Guid tenant, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{alias}-{Guid.NewGuid():N}@caemanager.local";
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = alias,
            EmailConfirmed = true,
            TenantId = tenant,
        };

        using (AmbitoTenantExplicito.Establecer(tenant))
        {
            (await userManager.CreateAsync(usuario, Contrasena)).Succeeded.Should().BeTrue();
            (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();
        }

        return usuario;
    }

    private static async Task<Guid> SembrarTenantAsync(
        CaeManagerDbContext contexto, string prefijo, PerfilVocabularioTenant perfil, bool operadorCaeExterno = false)
    {
        var tenant = new Tenant($"{prefijo} {Guid.NewGuid():N}", perfil);
        if (operadorCaeExterno)
            tenant.HabilitarComoOperadorCaeExterno();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        return tenant.Id;
    }

    private CaeManagerDbContext ContextoPropietarioDeLaBase(Guid? tenantSellado = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantSellado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorMutable : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }
}
