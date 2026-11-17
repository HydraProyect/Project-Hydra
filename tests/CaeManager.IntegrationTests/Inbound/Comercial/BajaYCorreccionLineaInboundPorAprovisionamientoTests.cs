using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.CorregirLineaInbound;
using CaeManager.Application.Inbound.Comercial.Commands.DarDeBajaLineaInbound;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.Comercial;

/// <summary>
/// Opción B del 2026-11-15: la baja (<see cref="DarDeBajaLineaInboundCommand"/>) y la corrección
/// (<see cref="CorregirLineaInboundCommand"/>) de la línea Inbound contra PostgreSQL real, con la misma composición que
/// <c>ContratarLineaInboundPorAprovisionamientoTests</c> más la <see cref="TransaccionDeComando"/> de producción.
///
/// <para>
/// Lo que solo esta composición prueba: que el handler finaliza como <c>cae_app_aprovisionamiento</c> con los privilegios
/// de columna de <c>FinalizaLineaInboundPorAprovisionamiento</c>, que la auditoría escrita a mano atribuye la baja al Actor
/// de Plataforma por su Sesión Privilegiada, y que la corrección finaliza y contrata en una transacción sin que el índice
/// único parcial choque ni el gate DN-7 vea un instante sin línea vigente. Y la otra mitad, con SQL directo bajo cada rol:
/// la única transición que el rol elevado puede escribir es Vigente→Finalizado de una fila Inbound de su Tenant, con el fin
/// entre now() - 5 minutos y now(), nunca en el futuro (issue #2259), sin tocar ni leer lo contratado; el runtime y el soporte no actualizan nada.
/// </para>
/// </summary>
public class BajaYCorreccionLineaInboundPorAprovisionamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorPlataforma = Guid.NewGuid();
    private readonly Tenant _plataforma = new("Organización de plataforma de prueba", esPlataforma: true);
    private readonly Tenant _tce = new("Tenant TCE de prueba");
    private readonly Tenant _otro = new("Otro Tenant de prueba");

    private Guid _sesionDeAprovisionamiento;
    private Guid _sesionDeLectura;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        await using var contexto = CrearContexto(_tce.Id, new Seleccion(_tce.Id, null));

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_plataforma, _tce, _otro);

        var aprovisionamiento = ConcesionPrivilegio.SobreTenants(
            _actorPlataforma, CapacidadPrivilegio.Aprovisionamiento, [_tce.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        var lectura = ConcesionPrivilegio.SobreTenants(
            _actorPlataforma, CapacidadPrivilegio.SoporteLectura, [_tce.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        contexto.ConcesionesPrivilegio.AddRange(aprovisionamiento, lectura);

        var sesionDeAprovisionamiento = SesionPrivilegiada.Abrir(
            aprovisionamiento, _tce.Id, "Baja o corrección de la línea Inbound", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        var sesionDeLectura = SesionPrivilegiada.Abrir(
            lectura, _tce.Id, "Consulta de soporte", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        contexto.SesionesPrivilegiadas.AddRange(sesionDeAprovisionamiento, sesionDeLectura);

        await contexto.SaveChangesAsync();
        _sesionDeAprovisionamiento = sesionDeAprovisionamiento.Id;
        _sesionDeLectura = sesionDeLectura.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_baja_por_el_pipeline_finaliza_la_vigente_una_vez_y_deja_su_auditoria()
    {
        var alta = await ContratarAsync("cus_PagadorTce");

        var antes = DateTime.UtcNow;
        var baja = await EnviarAsync(_sesionDeAprovisionamiento, rol: null, new DarDeBajaLineaInboundCommand());
        baja.EsExitoso.Should().BeTrue(baja.EsFallido ? $"código: {baja.Error.Codigo}" : string.Empty);
        baja.Valor.Should().Be(alta);

        var fila = (await FilasComoPropietarioAsync()).Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        fila.VigenteHastaUtc.Should().NotBeNull().And.BeOnOrAfter(antes.AddSeconds(-1)).And.BeOnOrBefore(DateTime.UtcNow.AddSeconds(1));
        fila.PagadorStripeCustomerId.Should().Be("cus_PagadorTce", "la baja no toca lo contratado");

        // La auditoría escrita a mano por el repositorio, con la conexión elevada: sellada en el Tenant beneficiario y
        // atribuida al Actor de Plataforma por la vía de su Sesión Privilegiada, como la del alta.
        var auditoria = await AuditoriaComoPropietarioAsync(alta);
        auditoria.Select(r => r.Accion).Should().BeEquivalentTo(["Creado", "Modificado"]);
        var modificacion = auditoria.Single(r => r.Accion == "Modificado");
        modificacion.TenantId.Should().Be(_tce.Id);
        modificacion.UsuarioId.Should().Be(_actorPlataforma);
        modificacion.ActorRealUsuarioId.Should().Be(_actorPlataforma);
        modificacion.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        modificacion.ViaAccesoId.Should().Be(_sesionDeAprovisionamiento);
        // La misma forma que el «Modificado» del interceptor: solo las dos propiedades cambiadas, el enum como número.
        using (var datosAntes = System.Text.Json.JsonDocument.Parse(modificacion.DatosAntes!))
        using (var datosDespues = System.Text.Json.JsonDocument.Parse(modificacion.DatosDespues!))
        {
            datosAntes.RootElement.GetProperty(nameof(ProductoContratado.Estado)).GetInt32()
                .Should().Be((int)EstadoProductoContratado.Vigente);
            datosAntes.RootElement.GetProperty(nameof(ProductoContratado.VigenteHastaUtc)).ValueKind
                .Should().Be(System.Text.Json.JsonValueKind.Null);
            datosDespues.RootElement.GetProperty(nameof(ProductoContratado.Estado)).GetInt32()
                .Should().Be((int)EstadoProductoContratado.Finalizado);
            datosDespues.RootElement.GetProperty(nameof(ProductoContratado.VigenteHastaUtc)).GetDateTime().ToUniversalTime()
                .Should().Be(fila.VigenteHastaUtc!.Value, "el fin auditado es el escrito: el now() de la transacción");
            datosAntes.RootElement.EnumerateObject().Should().HaveCount(2);
            datosDespues.RootElement.EnumerateObject().Should().HaveCount(2);
        }

        var segunda = await EnviarAsync(_sesionDeAprovisionamiento, rol: null, new DarDeBajaLineaInboundCommand());
        segunda.Error.Should().Be(ErroresDarDeBajaLineaInbound.SinLineaVigente, "Finalizado es final");
        (await AuditoriaComoPropietarioAsync(alta)).Should().HaveCount(2, "la baja rechazada no deja registro");
    }

    [Fact]
    public async Task La_correccion_sustituye_la_vigente_en_una_transaccion_sin_hueco_en_el_gate_DN7()
    {
        var anterior = await ContratarAsync("cus_PagadorErroneo");

        // Hallazgo 2 de Codex (ronda 2): el reloj de la aplicación va 4 minutos por delante del de PostgreSQL. Desde la
        // issue #2259 las políticas no admiten ningún instante futuro, así que si la corrección tomara el instante de la
        // aplicación se rechazaría; y aunque lo admitieran, la corregida empezaría después del now() del gate y la
        // comprobación tras el COMMIT fallaría. La corrección toma el now() que devuelve la finalización.
        _relojDeLaAplicacion = new RelojDesfasado(TimeSpan.FromMinutes(4));

        // Determinista, sin depender de tiempos: el propietario bloquea las inserciones en RegistrosAuditoria. La
        // corrección finaliza (UPDATE) y se queda esperando al guardar la auditoría de la baja, con la transacción abierta.
        // En ese instante, otra conexión tiene que seguir viendo la línea Inbound vigente: el UPDATE no es visible hasta
        // el COMMIT, que llega con la nueva ya insertada.
        await using var retenedor = new NpgsqlConnection(_cadenaConexion);
        await retenedor.OpenAsync();
        await using var bloqueo = await retenedor.BeginTransactionAsync();
        await using (var cerrar = new NpgsqlCommand("""LOCK TABLE "RegistrosAuditoria" IN SHARE MODE;""", retenedor, bloqueo))
            await cerrar.ExecuteNonQueryAsync();

        var correccion = Task.Run(() => EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new CorregirLineaInboundCommand("cus_PagadorCorregido", TramoTrabajadoresActivos.De81A150, ModalidadSuscripcionAlta.Mensual)));

        await using (var observador = new NpgsqlConnection(_cadenaConexion))
        {
            await observador.OpenAsync();
            await EsperarBloqueadaAsync(observador, correccion, "%RegistrosAuditoria%");

            // La fila sustituida ya está tomada por el UPDATE de la corrección, sin confirmar...
            (await SqlStateComoPropietarioAsync(observador,
                    $"""SELECT 1 FROM "ProductosContratados" WHERE "Id" = '{anterior}' FOR UPDATE NOWAIT;"""))
                .Should().Be(PostgresErrorCodes.LockNotAvailable, "la corrección ya finalizó dentro de su transacción");
            // ...y aun así el gate DN-7 sigue viendo la línea Inbound vigente.
            (await GateInboundVeVigenteAsync(observador)).Should().BeTrue("la baja sin confirmar no abre hueco en el gate");
        }

        await bloqueo.RollbackAsync();
        var resultado = await correccion;
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? $"código: {resultado.Error.Codigo}" : string.Empty);

        await using (var observador = new NpgsqlConnection(_cadenaConexion))
        {
            await observador.OpenAsync();
            (await GateInboundVeVigenteAsync(observador)).Should().BeTrue("tras el COMMIT, la corregida es la vigente");
        }

        var filas = await FilasComoPropietarioAsync();
        filas.Should().HaveCount(2, "la corrección no sobrescribe: deja el histórico en filas (DECISION_LOG 2026-11-05 (a))");
        var sustituida = filas.Single(f => f.Id == anterior);
        var corregida = filas.Single(f => f.Id == resultado.Valor);
        sustituida.Estado.Should().Be(EstadoProductoContratado.Finalizado);
        sustituida.PagadorStripeCustomerId.Should().Be("cus_PagadorErroneo");
        corregida.Estado.Should().Be(EstadoProductoContratado.Vigente);
        corregida.PagadorStripeCustomerId.Should().Be("cus_PagadorCorregido");
        corregida.Tramo.Should().Be(TramoTrabajadoresActivos.De81A150);
        corregida.Modalidad.Should().Be(ModalidadSuscripcionAlta.Mensual);
        corregida.TenantId.Should().Be(_tce.Id);
        sustituida.VigenteHastaUtc.Should().Be(corregida.VigenteDesdeUtc, "sin hueco ni solape entre las dos vigencias");
        corregida.VigenteDesdeUtc.Should().BeBefore(DateTime.UtcNow.AddMinutes(1),
            "el inicio es el now() de la transacción de PostgreSQL, no el reloj de la aplicación adelantado 4 minutos");

        (await AuditoriaComoPropietarioAsync(anterior)).Select(r => r.Accion).Should().BeEquivalentTo(["Creado", "Modificado"]);
        (await AuditoriaComoPropietarioAsync(corregida.Id)).Should().ContainSingle().Which.Accion.Should().Be("Creado");
    }

    [Fact]
    public async Task Con_el_reloj_de_la_aplicacion_adelantado_el_alta_empieza_en_el_now_de_la_base_y_el_gate_la_ve_en_el_acto()
    {
        // Issue #2259: la política de INSERT ya no admite un inicio futuro, así que el alta no puede tomar el reloj de la
        // aplicación. Con él 4 minutos por delante del de PostgreSQL, el alta de antes se rechazaría (42501), y con la
        // política de antes la fila empezaría en el futuro: el gate DN-7 no la vería hasta pasados esos minutos, y dentro
        // de una misma consulta podría dar dos valores distintos.
        _relojDeLaAplicacion = new RelojDesfasado(TimeSpan.FromMinutes(4));

        var alta = await ContratarAsync("cus_PagadorTce");

        await using var observador = new NpgsqlConnection(_cadenaConexion);
        await observador.OpenAsync();
        (await GateInboundVeVigenteAsync(observador)).Should().BeTrue("el inicio ya ha llegado en cuanto se ve el COMMIT");
        await using var consultarAhora = new NpgsqlCommand("SELECT now();", observador);
        var ahoraDeLaBase = (DateTime)(await consultarAhora.ExecuteScalarAsync())!;

        var fila = (await FilasComoPropietarioAsync()).Should().ContainSingle().Subject;
        fila.Id.Should().Be(alta);
        fila.VigenteDesdeUtc.Should().BeOnOrBefore(ahoraDeLaBase.ToUniversalTime(),
            "el inicio es el now() de la transacción que insertó, anterior a cualquier lectura que vea la fila");
        fila.VigenteDesdeUtc.Should().BeBefore(DateTime.UtcNow.AddMinutes(1),
            "control: no es el reloj de la aplicación adelantado 4 minutos");
    }

    [Fact]
    public async Task Si_la_corregida_no_se_inserta_tampoco_se_confirma_la_baja()
    {
        var anterior = await ContratarAsync("cus_PagadorTce");

        // Sin línea que sustituir la corrección no empieza; con línea, un fallo después de finalizar lo deshace todo. Se
        // provoca con la propia base: un disparador del propietario que rechaza el INSERT de la corregida. Distingue si la
        // sustituida ya estaba finalizada dentro de la transacción (hallazgo 2 de Codex, ronda 1): así el test exige que
        // el rechazo llegó después del UPDATE, no que la corrección fallara antes por cualquier otro motivo.
        await EjecutarComoPropietarioAsync(
            $$"""
            CREATE FUNCTION public.prueba_rechaza_insert() RETURNS trigger LANGUAGE plpgsql SECURITY DEFINER AS
            $f$ BEGIN
                IF EXISTS (SELECT 1 FROM "ProductosContratados"
                           WHERE "Id" = '{{anterior}}' AND "Estado" = 'Finalizado') THEN
                    RAISE EXCEPTION 'rechazo tras finalizar' USING ERRCODE = 'P0001';
                END IF;
                RAISE EXCEPTION 'insert sin finalizar antes' USING ERRCODE = 'P0001';
            END $f$;
            CREATE TRIGGER prueba_rechaza_insert BEFORE INSERT ON "ProductosContratados"
                FOR EACH ROW EXECUTE FUNCTION public.prueba_rechaza_insert();
            """);

        // LoggingBehavior relanza lo inesperado: la excepción del disparador llega al llamador, envuelta o no.
        var intento = () => EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new CorregirLineaInboundCommand("cus_PagadorCorregido", TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual));
        var excepcion = (await intento.Should().ThrowAsync<Exception>()).Which;
        var postgres = excepcion as PostgresException ?? excepcion.InnerException as PostgresException;
        postgres.Should().NotBeNull($"el rechazo tiene que venir del disparador; llegó {excepcion.GetType().Name}: {excepcion.Message}");
        postgres!.MessageText.Should().Be("rechazo tras finalizar", "el INSERT de la corregida llega con la sustituida ya finalizada");

        var fila = (await FilasComoPropietarioAsync()).Should().ContainSingle().Subject;
        fila.Id.Should().Be(anterior);
        fila.Estado.Should().Be(EstadoProductoContratado.Vigente, "la baja de la sustituida se deshizo con la transacción");
        fila.VigenteHastaUtc.Should().BeNull();
        (await AuditoriaComoPropietarioAsync(anterior)).Should().ContainSingle("la auditoría de la baja también se deshizo");
    }

    [Fact]
    public async Task Con_la_vigencia_aun_sin_empezar_la_baja_se_rechaza_sin_cambiar_nada()
    {
        await EjecutarComoPropietarioAsync(InsertInbound(_tce.Id, vigenteDesde: "now() + interval '1 day'"));

        var baja = await EnviarAsync(_sesionDeAprovisionamiento, rol: null, new DarDeBajaLineaInboundCommand());

        baja.Error.Should().Be(ErroresDarDeBajaLineaInbound.VigenciaAunNoIniciada,
            "CK_ProductosContratados_EstadoYVigencia rechaza un fin anterior al inicio");
        (await FilasComoPropietarioAsync()).Should().ContainSingle().Which.Estado.Should().Be(EstadoProductoContratado.Vigente);
    }

    [Fact]
    public async Task Con_cambios_pendientes_de_otro_acto_la_baja_no_escribe_nada()
    {
        // Hallazgo 2 del revisor-puente: la guarda del repositorio, como la del alta. El registro de auditoría de la baja
        // se guarda con SaveChanges bajo el rol elevado, que arrastraría lo pendiente de otro acto.
        var alta = await ContratarAsync("cus_PagadorTce");
        await using var contexto = CrearContexto(_tce.Id, new Seleccion(_tce.Id, null));
        var ajeno = new RegistroAuditoria(
            "PruebaAjena", Guid.NewGuid(), "Creado", null, null,
            usuarioId: _actorPlataforma, tipoActor: TipoActorAuditoria.Persona, actorRealUsuarioId: _actorPlataforma,
            viaAcceso: TipoViaAccesoAuditoria.Normal, viaAccesoId: null);
        contexto.RegistrosAuditoria.Add(ajeno);
        var repositorio = new ProductoContratadoRepository(contexto, actorAuditoria: null!);

        var finalizar = () => repositorio.FinalizarInboundVigenteAsync();

        await finalizar.Should().ThrowAsync<InvalidOperationException>("la baja se escribe sola");
        contexto.Entry(ajeno).State.Should().Be(EntityState.Added, "lo pendiente de otro acto se queda sin escribir");
        var fila = (await FilasComoPropietarioAsync()).Should().ContainSingle().Subject;
        fila.Estado.Should().Be(EstadoProductoContratado.Vigente);
        fila.VigenteHastaUtc.Should().BeNull();
        (await AuditoriaComoPropietarioAsync(alta)).Should().ContainSingle("solo el alta");
        (await AuditoriaComoPropietarioAsync(ajeno.EntidadId)).Should().BeEmpty();
    }

    public static TheoryData<string, string> ParesConcurrentes() => new()
    {
        { "baja", "baja" },
        { "correccion", "correccion" },
        { "correccion", "baja" },
        { "baja", "correccion" },
    };

    [Theory]
    [MemberData(nameof(ParesConcurrentes))]
    public async Task Dos_bajas_o_correcciones_a_la_vez_sobre_la_misma_fila_solo_una_finaliza(string primero, string segundo)
    {
        // Hallazgo 2 de Codex, ronda 3, en la #2256. Determinista: el propietario bloquea las inserciones en
        // RegistrosAuditoria. La primera finaliza (UPDATE) y espera con la fila tomada al guardar su auditoría; la segunda
        // lee la misma fila aún vigente y espera el bloqueo de fila en su UPDATE. Al soltar, la primera confirma y la
        // segunda reevalúa el WHERE, no toca nada y deshace su transacción entera.
        var anterior = await ContratarAsync("cus_PagadorTce");

        await using var retenedor = new NpgsqlConnection(_cadenaConexion);
        await retenedor.OpenAsync();
        await using var bloqueo = await retenedor.BeginTransactionAsync();
        await using (var cerrar = new NpgsqlCommand("""LOCK TABLE "RegistrosAuditoria" IN SHARE MODE;""", retenedor, bloqueo))
            await cerrar.ExecuteNonQueryAsync();

        var primera = Task.Run(() => EnviarAsync(_sesionDeAprovisionamiento, rol: null, Comando(primero)));
        Task<Result<Guid>> segunda;
        await using (var observador = new NpgsqlConnection(_cadenaConexion))
        {
            await observador.OpenAsync();
            await EsperarBloqueadaAsync(observador, primera, "%RegistrosAuditoria%");
            segunda = Task.Run(() => EnviarAsync(_sesionDeAprovisionamiento, rol: null, Comando(segundo)));
            await EsperarBloqueadaAsync(observador, segunda, "%UPDATE%ProductosContratados%");
        }

        await bloqueo.RollbackAsync();
        var resultadoPrimera = await primera;
        var resultadoSegunda = await segunda;

        resultadoPrimera.EsExitoso.Should().BeTrue(resultadoPrimera.EsFallido ? $"código: {resultadoPrimera.Error.Codigo}" : string.Empty);
        resultadoSegunda.Error.Should().Be(segundo == "baja"
                ? ErroresDarDeBajaLineaInbound.SinLineaVigente
                : ErroresCorregirLineaInbound.SinLineaVigente,
            "el UPDATE de la segunda reevalúa el estado de partida tras el COMMIT de la primera y no encuentra fila");

        var filas = await FilasComoPropietarioAsync();
        filas.Should().HaveCount(primero == "correccion" ? 2 : 1, "la segunda no contrata nada");
        filas.Single(f => f.Id == anterior).Estado.Should().Be(EstadoProductoContratado.Finalizado);
        filas.Count(f => f.Estado == EstadoProductoContratado.Vigente).Should().Be(primero == "correccion" ? 1 : 0);
        (await AuditoriaComoPropietarioAsync(anterior)).Select(r => r.Accion).Should().BeEquivalentTo(
            ["Creado", "Modificado"], "una sola finalización auditada");
    }

    public static TheoryData<string> Comandos() => new() { "baja", "correccion" };

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Una_sesion_de_soporte_de_solo_lectura_no_da_de_baja_ni_corrige(string comando)
    {
        await ContratarAsync("cus_PagadorTce");

        var resultado = await EnviarAsync(_sesionDeLectura, rol: null, Comando(comando));

        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
        (await FilasComoPropietarioAsync()).Should().ContainSingle().Which.Estado.Should().Be(EstadoProductoContratado.Vigente);
    }

    [Theory]
    [MemberData(nameof(Comandos))]
    public async Task Un_Administrador_del_propio_Tenant_sin_Sesion_Privilegiada_no_da_de_baja_ni_corrige(string comando)
    {
        await ContratarAsync("cus_PagadorTce");

        var resultado = await EnviarAsync(sesion: null, rol: "Administrador", Comando(comando));

        resultado.Error.Should().Be(comando == "baja"
            ? ErroresDarDeBajaLineaInbound.NoAutorizado
            : ErroresCorregirLineaInbound.NoAutorizado);
        (await FilasComoPropietarioAsync()).Should().ContainSingle().Which.Estado.Should().Be(EstadoProductoContratado.Vigente);
    }

    [Fact]
    public async Task El_indice_unico_parcial_solo_admite_la_corregida_si_la_sustituida_se_finaliza_antes()
    {
        await EjecutarComoPropietarioAsync(InsertInbound(_tce.Id, id: FilaSustituida));

        // Insertar antes de finalizar choca con la vigente: por eso la corrección finaliza primero.
        var alReves = await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, InsertInbound(_tce.Id) + Finalizar(FilaSustituida));
        alReves.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        alReves.Restriccion.Should().Be(ProductoContratadoConfiguration.IndiceInboundVigentePorTenant);

        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, Finalizar(FilaSustituida) + InsertInbound(_tce.Id)))
            .SqlState.Should().BeNull("finalizar y después insertar, en la misma transacción, entra");
    }

    [Fact]
    public async Task Con_SQL_directo_el_rol_elevado_solo_finaliza_la_Inbound_vigente_de_su_Tenant_y_dentro_del_margen()
    {
        var finalizada = Guid.NewGuid();
        var otraLinea = Guid.NewGuid();
        var deOtroTenant = Guid.NewGuid();
        var eliminada = Guid.NewGuid();
        // Inicio de ayer: el CHECK de coherencia (fin no anterior al inicio) no interfiere con el margen que se prueba.
        await EjecutarComoPropietarioAsync(
            InsertInbound(_tce.Id, id: FilaSustituida, vigenteDesde: "now() - interval '1 day'")
            + InsertInbound(_tce.Id, id: finalizada, finalizado: true, vigenteDesde: "now() - interval '1 day'")
            + InsertInbound(_tce.Id, id: otraLinea, linea: "OutboundGestionPropia", vigenteDesde: "now() - interval '1 day'")
            + InsertInbound(_otro.Id, id: deOtroTenant, vigenteDesde: "now() - interval '1 day'")
            + InsertInbound(_tce.Id, id: eliminada, eliminado: true, vigenteDesde: "now() - interval '1 day'"));

        // Control positivo: sin él, los rechazos de abajo pasarían igual con un rol que no pudiera actualizar nada.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, Finalizar(FilaSustituida)))
            .Should().Be(((string?)null, 1, (string?)null));
        foreach (var desfase in new[] { "now() - interval '4 minutes'", "now()" })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, Finalizar(FilaSustituida, desfase)))
                .Should().Be(((string?)null, 1, (string?)null), $"dentro del margen: {desfase}");

        // Filas que el USING no deja ver para actualizar: el UPDATE no falla, no toca nada.
        foreach (var (sql, motivo) in new[]
                 {
                     ("""UPDATE "ProductosContratados" SET "Estado" = 'Vigente', "VigenteHastaUtc" = NULL WHERE "Id" = '""" + finalizada + "';",
                      "Finalizado es final: no se reabre"),
                     (Finalizar(finalizada), "una fila ya finalizada"),
                     (Finalizar(otraLinea), "otra línea del Producto Contratado"),
                     (Finalizar(deOtroTenant), "una fila de otro Tenant"),
                     (Finalizar(eliminada), "una fila eliminada"),
                 })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, sql))
                .Should().Be(((string?)null, 0, (string?)null), motivo);

        // Lo que el WITH CHECK rechaza de la fila que sí puede actualizar.
        foreach (var (sql, motivo) in new[]
                 {
                     (Finalizar(FilaSustituida, "now() - interval '6 minutes'"), "6 minutos antes, fuera del margen"),
                     (Finalizar(FilaSustituida, "now() + interval '6 minutes'"), "6 minutos después, fuera del margen"),
                     (Finalizar(FilaSustituida, "now() - interval '1 hour'"), "una baja con fecha atrasada"),
                     ("""UPDATE "ProductosContratados" SET "VigenteHastaUtc" = now() WHERE "Id" = '""" + FilaSustituida + "';",
                      "un fin sin pasar a Finalizado"),
                 })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, sql)).SqlState
                .Should().BeOneOf([PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.CheckViolation], motivo);
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, Finalizar(FilaSustituida, "now() + interval '6 minutes'")))
            .SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "el margen lo pone la política, no el CHECK");
        // Issue #2259: el fin nunca queda en el futuro, ni dentro del antiguo margen del reloj de la aplicación. Lo rechaza
        // la política (42501), no el CHECK: el inicio de la fila es de ayer.
        foreach (var desfase in new[] { "now() + interval '4 minutes'", "now() + interval '1 millisecond'" })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, Finalizar(FilaSustituida, desfase)))
                .SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"un fin futuro: {desfase}");

        // Columnas sin privilegio: lo contratado, la papelera, el Tenant y la línea no se cambian, y lo contratado no se lee.
        foreach (var sql in new[]
                 {
                     $"""UPDATE "ProductosContratados" SET "Tramo" = 'De11A40' WHERE "Id" = '{FilaSustituida}';""",
                     $"""UPDATE "ProductosContratados" SET "PagadorStripeCustomerId" = 'cus_Otro' WHERE "Id" = '{FilaSustituida}';""",
                     $"""UPDATE "ProductosContratados" SET "Modalidad" = 'Mensual' WHERE "Id" = '{FilaSustituida}';""",
                     $"""UPDATE "ProductosContratados" SET "VigenteDesdeUtc" = now() WHERE "Id" = '{FilaSustituida}';""",
                     $"""UPDATE "ProductosContratados" SET "EstaEliminado" = true WHERE "Id" = '{FilaSustituida}';""",
                     $"""UPDATE "ProductosContratados" SET "TenantId" = '{_otro.Id}' WHERE "Id" = '{FilaSustituida}';""",
                     $"""UPDATE "ProductosContratados" SET "Linea" = 'OutboundGestionPropia' WHERE "Id" = '{FilaSustituida}';""",
                     """SELECT "PagadorStripeCustomerId" FROM "ProductosContratados";""",
                     """SELECT "Tramo", "Modalidad" FROM "ProductosContratados";""",
                     """SELECT "VigenteDesdeUtc", "VigenteHastaUtc" FROM "ProductosContratados";""",
                     """DELETE FROM "ProductosContratados";""",
                 })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, sql)).SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);

        // De las columnas que lee, solo las filas Inbound de su Tenant, eliminadas incluidas (las descarta el filtro de EF).
        (await EscalarComoAsync("cae_app_aprovisionamiento", _tce.Id, """SELECT count("Id") FROM "ProductosContratados";"""))
            .Should().Be(3L, "la sustituida, la finalizada y la eliminada; ni la de otra línea ni la de otro Tenant");

        // El runtime del propio Tenant y el soporte no actualizan nada: un Tenant no se cierra ni se reabre su línea.
        foreach (var rol in new[] { "cae_app_runtime", "cae_app_soporte" })
            (await SqlStateComoAsync(rol, _tce.Id, Finalizar(FilaSustituida))).SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, rol);
    }

    private static readonly Guid FilaSustituida = Guid.Parse("0b5c1c1e-0000-4000-8000-000000000001");

    private static string Finalizar(Guid id, string fin = "now()") =>
        $"""UPDATE "ProductosContratados" SET "Estado" = 'Finalizado', "VigenteHastaUtc" = {fin} WHERE "Id" = '{id}';""";

    private static string InsertInbound(
        Guid tenantId, Guid? id = null, bool finalizado = false, string linea = "Inbound", bool eliminado = false,
        string vigenteDesde = "now()") =>
        $"""
        INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
            "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
        VALUES ('{id ?? Guid.NewGuid()}', '{linea}', 'cus_X', 'De1A10', 'Anual', {vigenteDesde},
            {(finalizado ? "now()" : "NULL")}, '{(finalizado ? "Finalizado" : "Vigente")}', '{tenantId}', '{Guid.NewGuid()}', now(),
            {(eliminado ? "true" : "false")});
        """;

    private static IRequest<Result<Guid>> Comando(string comando) => comando == "baja"
        ? new DarDeBajaLineaInboundCommand()
        : new CorregirLineaInboundCommand("cus_PagadorCorregido", TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Anual);

    private async Task<Guid> ContratarAsync(string pagador)
    {
        var alta = await EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new ContratarLineaInboundCommand(pagador, TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));
        alta.EsExitoso.Should().BeTrue(alta.EsFallido ? $"control del alta: {alta.Error.Codigo}" : string.Empty);
        return alta.Valor;
    }

    /// <summary>El reloj de la aplicación que reciben los handlers; un test lo desfasa frente al de PostgreSQL.</summary>
    private TimeProvider _relojDeLaAplicacion = TimeProvider.System;

    private sealed class RelojDesfasado(TimeSpan desfase) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + desfase;
    }

    private async Task<Result<Guid>> EnviarAsync(Guid? sesion, string? rol, IRequest<Result<Guid>> comando)
    {
        await using var proveedor = ConstruirProveedor(sesion, rol);
        return await proveedor.GetRequiredService<IMediator>().Send(comando);
    }

    /// <summary>
    /// El predicado del gate DN-7 de <c>app_inbound_tenant_contratista_del_contrato</c>, para el Tenant TCE. La función real,
    /// con una transacción o una consulta interna lectoras anteriores a la corrección, la prueba <c>LecturaInboundBajoRuntimeTests</c>.
    /// </summary>
    private async Task<bool> GateInboundVeVigenteAsync(NpgsqlConnection conexion)
    {
        await using var comando = new NpgsqlCommand(
            """
            SELECT EXISTS (SELECT 1 FROM public."ProductosContratados" pc
                            WHERE pc."TenantId" = @tenant AND pc."Linea" = 'Inbound'
                              AND pc."Estado" = 'Vigente' AND NOT pc."EstaEliminado"
                              AND pc."VigenteDesdeUtc" <= pg_catalog.clock_timestamp());
            """, conexion);
        comando.Parameters.AddWithValue("tenant", _tce.Id);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task EsperarBloqueadaAsync(NpgsqlConnection observador, Task<Result<Guid>> tarea, string patronConsulta)
    {
        await using var esperando = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock' AND query LIKE @patron",
            observador);
        esperando.Parameters.AddWithValue("patron", patronConsulta);
        var limite = DateTime.UtcNow.AddSeconds(30);
        while ((long)(await esperando.ExecuteScalarAsync())! < 1)
        {
            if (tarea.IsCompleted)
            {
                var resultado = await tarea;
                throw new Xunit.Sdk.XunitException(
                    $"la corrección terminó sin esperar el bloqueo: {(resultado.EsExitoso ? "éxito" : resultado.Error.Codigo)}");
            }

            DateTime.UtcNow.Should().BeBefore(limite, "la corrección debe quedar esperando el bloqueo de la auditoría");
            await Task.Delay(25);
        }
    }

    private static async Task<string?> SqlStateComoPropietarioAsync(NpgsqlConnection conexion, string sql)
    {
        await using var transaccion = await conexion.BeginTransactionAsync();
        try
        {
            await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
            await comando.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex.SqlState;
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    /// <summary>Las filas como superusuario de pruebas: la comprobación no depende de los privilegios que se prueban.</summary>
    private async Task<List<ProductoContratado>> FilasComoPropietarioAsync()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _tce.Id });
        return await contexto.ProductosContratados.IgnoreQueryFilters().Where(p => p.TenantId == _tce.Id).ToListAsync();
    }

    private async Task<List<RegistroAuditoria>> AuditoriaComoPropietarioAsync(Guid entidadId)
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _tce.Id });
        return await contexto.RegistrosAuditoria.IgnoreQueryFilters().Where(r => r.EntidadId == entidadId).ToListAsync();
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Ejecuta <paramref name="sql"/> con <paramref name="rol"/> y el contexto firmado de <paramref name="tenantContexto"/>,
    /// dentro de una transacción que se deshace. Devuelve el SQLSTATE y la restricción del rechazo o, si entró,
    /// <c>null</c> y las filas afectadas por la última sentencia.
    /// </summary>
    private async Task<(string? SqlState, int Filas, string? Restriccion)> SqlStateComoAsync(string rol, Guid tenantContexto, string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var transaccion = await PrepararComoAsync(conexion, rol, tenantContexto);
        try
        {
            await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
            return (null, await comando.ExecuteNonQueryAsync(), null);
        }
        catch (PostgresException ex)
        {
            return (ex.SqlState, -1, ex.ConstraintName);
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private async Task<object?> EscalarComoAsync(string rol, Guid tenantContexto, string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var transaccion = await PrepararComoAsync(conexion, rol, tenantContexto);
        try
        {
            await using var comando = new NpgsqlCommand(sql, conexion, transaccion);
            return await comando.ExecuteScalarAsync();
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private static async Task<NpgsqlTransaction> PrepararComoAsync(NpgsqlConnection conexion, string rol, Guid tenantContexto)
    {
        var transaccion = await conexion.BeginTransactionAsync();
        var contextoFirmado = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: tenantContexto);
        await using (var preparar = new NpgsqlCommand(
                         $"SELECT set_config('app.contexto', @contexto, true); SET LOCAL ROLE {rol};", conexion, transaccion))
        {
            preparar.Parameters.AddWithValue("contexto", contextoFirmado);
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var comprobarRol = new NpgsqlCommand("SELECT current_user::text;", conexion, transaccion))
            ((string)(await comprobarRol.ExecuteScalarAsync())!).Should().Be(rol);

        return transaccion;
    }

    private ServiceProvider ConstruirProveedor(Guid? sesionSeleccionada, string? rol)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tce.Id };
        var currentUser = sesionSeleccionada is null
            ? new CurrentUserServiceFalso(_actorPlataforma, rol: rol, tenantOrigenId: _tce.Id)
            : new CurrentUserServiceFalso(_actorPlataforma, rol: rol, tenantOrigenId: _plataforma.Id);
        var seleccion = new Seleccion(_tce.Id, sesionSeleccionada);
        var actorAuditoria = new ActorAuditoriaDesdeSesion(currentUser, seleccion, currentUser);
        var dbContext = CrearContexto(_tce.Id, seleccion, currentUser, actorAuditoria);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(currentUser);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        servicios.AddSingleton(dbContext);
        servicios.AddSingleton(_relojDeLaAplicacion);

        servicios.AddSingleton<IPlataformaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ISesionPrivilegiadaActual>(sp => new SesionPrivilegiadaActual(
            sp.GetRequiredService<IPlataformaQueryContext>(), seleccion, currentUser));
        servicios.AddSingleton<IElevacionEscrituraPrivilegiada>(sp =>
            new ElevacionEscrituraPrivilegiada(sp.GetRequiredService<CaeManagerDbContext>()));

        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEncargosAdministracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IProductoContratadoRepository, ProductoContratadoRepository>();
        servicios.AddSingleton<IActorAuditoria>(actorAuditoria);
        // La transacción de producción: la corrección finaliza y contrata dentro de ella.
        servicios.AddSingleton<ITransaccionDeComando>(sp => new TransaccionDeComando(sp.GetRequiredService<CaeManagerDbContext>()));

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto(
        Guid tenantId, IClienteActivoSeleccionado seleccion, ICurrentUserService? currentUserService = null,
        IActorAuditoria? actorAuditoria = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var rls = new TenantRlsConnectionInterceptor(
            tenantActual, seleccion,
            currentUserService ?? new CurrentUserServiceFalso(_actorPlataforma, rol: null, tenantOrigenId: tenantId),
            BaseDatosPostgresDePruebas.FirmanteContextoRls);
        var constructor = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (actorAuditoria is null)
            constructor.AddInterceptors(new TenantSelladoInterceptor(tenantActual), rls);
        else
            constructor.AddInterceptors(
                new AuditoriaInterceptor(actorAuditoria), rls, new TenantSelladoInterceptor(tenantActual),
                new ConcurrenciaOptimistaInterceptor());

        return new CaeManagerDbContext(constructor.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class Seleccion(Guid tenantSeleccionado, Guid? sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
