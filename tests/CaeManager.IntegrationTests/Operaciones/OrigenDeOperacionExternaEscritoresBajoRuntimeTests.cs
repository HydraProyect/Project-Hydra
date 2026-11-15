using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Ronda 1 de Codex en la PR #2253 (altas 1, 2 y 3) contra Postgres real.
/// <list type="bullet">
/// <item>Alta 1: la inserción sin origen que espera a una revocación en vuelo de la candidata se rechaza con
/// <c>serialization_failure</c>, para que quien la reintente decida otra vez sobre lo confirmado.</item>
/// <item>Alta 2: <see cref="CatalogoIncorporacionCartera"/> no emite cartera ni fila heredada sobre una operación cuyo
/// origen ya no la ampara, y la fila heredada cuelga de la DelegacionTenant de origen de la operación.</item>
/// <item>Alta 3: la operación que la ranura anterior inserta antes que la DelegacionTenant que la ampara, o antes de
/// reactivarla, en la misma transacción, queda sellada al escribir la delegación; una operación de otra transacción
/// no.</item>
/// </list>
/// </summary>
public partial class OrigenDeOperacionExternaBajoRuntimeTests
{
    private const string CifSegundoCliente = "B12345674";

    // ── Alta 1: deducción con la candidata cambiando ─────────────────────────

    /// <summary>
    /// La carrera real, con dos conexiones. La revocación se escribe y no se confirma; la inserción sin origen lee la
    /// candidata activa, se queda esperando en el <c>FOR SHARE</c> y, cuando la revocación se confirma, encuentra la
    /// fila revocada. Sin el rechazo, nacería sin origen en silencio (ninguna activa bajo el bloqueo) aunque la
    /// decisión de la ranura anterior se tomó con la delegación activa.
    /// </summary>
    [Fact]
    public async Task La_insercion_sin_origen_que_espera_a_la_revocacion_en_vuelo_de_la_candidata_se_rechaza_para_reintentar()
    {
        await using var revocacion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await revocacion.OpenAsync();
        await using var transaccion = await revocacion.BeginTransactionAsync();
        await using (var revocar = new NpgsqlCommand(
                         """UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = @id""", revocacion, transaccion))
        {
            revocar.Parameters.AddWithValue("id", _delegacion);
            (await revocar.ExecuteNonQueryAsync()).Should().Be(1, "control: la revocación toca la candidata");
        }

        var insercion = Task.Run(() => InsertarAcotadaSinOrigenAsync(_cliente));
        await EsperarBloqueadaPorAsync(revocacion.ProcessID, insercion);
        await transaccion.CommitAsync();

        var guardado = async () => await insercion;
        (await guardado.Should().ThrowAsync<DbUpdateException>()).Which.Should().Match<DbUpdateException>(
            e => TieneEstado(e, PostgresErrorCodes.SerializationFailure),
            "la candidata que leyó sin bloquear ya no es la que encontró bloqueada");

        // El reintento decide sobre lo confirmado: ninguna delegación activa, así que nace sin origen.
        var reintento = await InsertarAcotadaSinOrigenAsync(_cliente);
        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == reintento)).OrigenId.Should().BeNull();
        (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones.Should().Be(1);
    }

    // ── Alta 2: el catálogo de incorporación ─────────────────────────────────

    /// <summary>
    /// Sin el amparo en <c>ObtenerOperacionVigenteAsync</c>, la operación sigue vigente y su DelegacionTenant de
    /// origen vuelve a estar activa, así que <c>VinculoVivoConElOperadorAsync</c> la encontraría y la incorporación
    /// emitiría cartera y fila heredada sobre una operación que ya no autoriza.
    /// </summary>
    [Fact]
    public async Task La_incorporacion_a_una_operacion_cuyo_origen_se_revoco_y_reactivo_se_anula()
    {
        var delTenantEntero = await OperacionDelTenantEnteroAsync();
        var control = await IncorporarAsync(delTenantEntero);
        control.Resultado.Cartera.Should().NotBeNull("control: con su origen vigente, la incorporación emite la cartera");
        control.OrigenDeLaFilaHeredada.Should().Be(_delegacion);

        await RevocarYReactivarPorLaRanuraAnteriorAsync();
        await AfirmarVigentesAsync(delTenantEntero);

        var anulada = await IncorporarAsync(delTenantEntero);
        anulada.Resultado.Cartera.Should().BeNull();
        anulada.Resultado.MotivoAnulacion.Should().Be(MotivoAnulacionSolicitudCartera.OperacionNoVigente);
        anulada.OrigenDeLaFilaHeredada.Should().BeNull("sin cartera tampoco hay fila heredada");
    }

    /// <summary>
    /// Con dos DelegacionesTenant activas del par, la fila heredada cuelga de la de origen de la operación y no de la
    /// más antigua, que es la que elegiría la consulta sin el filtro de origen.
    /// </summary>
    [Fact]
    public async Task La_fila_heredada_de_la_incorporacion_cuelga_del_origen_de_la_operacion()
    {
        DelegacionTenant segunda;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.DelegacionesTenant.Add(new DelegacionTenant(_operador, _propietario));
            await propietarioDeLaBase.SaveChangesAsync();
            segunda = await propietarioDeLaBase.DelegacionesTenant.AsNoTracking()
                .SingleAsync(d => d.TenantClienteId == _propietario && d.TenantConsultoraId == _operador && d.Id != _delegacion);
        }

        (await InsertarAcotadaConOrigenAsync(segunda)).Should().BeFalse("control: el origen explícito de la segunda es válido");
        await using (var verificacion = ContextoPropietarioDeLaBase())
        {
            var acotada = await verificacion.AsignacionesOperacion
                .SingleAsync(o => o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                                  && o.AmbitoRelacionClienteId == _cliente);
            acotada.OrigenId.Should().Be(segunda.Id);

            var incorporada = await IncorporarAsync(acotada.Id);
            incorporada.Resultado.Cartera.Should().NotBeNull();
            incorporada.OrigenDeLaFilaHeredada.Should().Be(segunda.Id,
                "la fila heredada es de la DelegacionTenant que ampara la operación, no de la más antigua del par");
        }
    }

    // ── Alta 3: sellado al escribir la delegación ────────────────────────────

    /// <summary>
    /// El orden de guardado de la ranura anterior: la operación del par nuevo se inserta antes que la DelegacionTenant
    /// que la ampara, en la misma transacción. Bajo <c>cae_app_runtime</c> y por <see cref="TransaccionDeComando"/>, como
    /// los comandos de producción: la escritura de la delegación reescribe la operación bajo la RLS de quien escribe.
    /// </summary>
    [Fact]
    public async Task La_operacion_insertada_antes_que_su_origen_en_la_misma_transaccion_queda_sellada()
    {
        Guid otroOperador;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
            otroOperador = await SembrarTenantAsync(
                propietarioDeLaBase, "Operador CAE nuevo", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);

        Guid operacionId = Guid.Empty, origenId = Guid.Empty;
        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            FijarSesion(_administradorPropietario, Roles.Administrador);
            using (AmbitoTenantExplicito.Establecer(_propietario))
            {
                var resultado = await new TransaccionDeComando(contexto).EjecutarAsync(async ct =>
                {
                    var ahora = DateTime.UtcNow;
                    var operacion = AsignacionOperacion.Externa(_propietario, otroOperador, ServicioCae.Outbound,
                        AmbitoAsignacion.DeRelacionCliente(_cliente), ahora.AddDays(-1), null, ahora);
                    contexto.AsignacionesOperacion.Add(operacion);
                    await contexto.SaveChangesAsync(ct);

                    var origen = new DelegacionTenant(otroOperador, _propietario);
                    contexto.DelegacionesTenant.Add(origen);
                    await contexto.SaveChangesAsync(ct);

                    operacionId = operacion.Id;
                    origenId = origen.Id;
                    return Result.Exito();
                });
                resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);
            }
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        var guardada = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == operacionId);
        guardada.OrigenId.Should().Be(origenId, "la DelegacionTenant escrita en su transacción es la única activa del par");
        guardada.RevocacionesDelOrigen.Should().Be(0);
    }

    /// <summary>
    /// La reactivación de la ranura anterior escrita después de insertar la operación, en la misma transacción. La
    /// operación del mismo par que se insertó antes, fuera de esa transacción, mientras la delegación estaba revocada,
    /// no se sella: reactivar no repone el amparo de lo que nació sin él.
    /// </summary>
    [Fact]
    public async Task La_operacion_insertada_antes_de_reactivar_su_origen_en_la_misma_transaccion_queda_sellada_y_la_anterior_no()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        var anterior = await SembrarAcotadaConCarteraAsync();
        var segundoCliente = await SembrarClienteAsync("Cliente reactivado S.A.", CifSegundoCliente);

        Guid nueva;
        await using (var contexto = ContextoPropietarioDeLaBase())
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync();
            var ahora = DateTime.UtcNow;
            var operacion = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(segundoCliente), ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            await contexto.SaveChangesAsync();
            await contexto.Database.ExecuteSqlAsync(
                $"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = {_delegacion}""");
            await transaccion.CommitAsync();
            nueva = operacion.Id;
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        var sellada = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == nueva);
        sellada.OrigenId.Should().Be(_delegacion);
        sellada.RevocacionesDelOrigen.Should().Be(1, "nace con el contador que la delegación tiene al reactivarse");
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == anterior)).OrigenId
            .Should().BeNull("nació en otra transacción, con la delegación revocada");
        (await CarterasAutorizadasDelOperadorAsync()).Should().Contain(anterior,
            "control: sin origen sigue como antes de la migración, con la delegación del par activa");
    }

    // ── Montaje de los escritores ────────────────────────────────────────────

    /// <summary>Una acotada sin origen, como la inserta la ranura anterior. Devuelve el Id de la operación.</summary>
    private async Task<Guid> InsertarAcotadaSinOrigenAsync(Guid clienteEmpresarial)
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(clienteEmpresarial), ahora.AddDays(-1), null, ahora);
        propietarioDeLaBase.AsignacionesOperacion.Add(acotada);
        await propietarioDeLaBase.SaveChangesAsync();
        return acotada.Id;
    }

    /// <summary>
    /// Espera a que alguna sesión quede bloqueada por <paramref name="pidBloqueante"/>. Falla si la inserción termina
    /// antes (no llegó a esperar al bloqueo) o si en 30 s nadie espera.
    /// </summary>
    private async Task EsperarBloqueadaPorAsync(int pidBloqueante, Task insercion)
    {
        await using var observador = new NpgsqlConnection(_arnes.CadenaPropietario);
        await observador.OpenAsync();
        await using var consulta = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @pid = ANY(pg_blocking_pids(pid)))", observador);
        consulta.Parameters.AddWithValue("pid", pidBloqueante);

        var limite = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < limite)
        {
            insercion.IsCompleted.Should().BeFalse("la inserción tenía que esperar al bloqueo de la revocación en vuelo");
            if ((bool)(await consulta.ExecuteScalarAsync())!)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("La inserción no llegó a esperar al bloqueo de la revocación en vuelo.");
    }

    /// <summary>
    /// <see cref="CatalogoIncorporacionCartera.IncorporarAsync(Guid, Guid, Guid, Guid, CancellationToken)"/> en el
    /// contexto del Operador CAE externo, como <c>cae_app_runtime</c>, sin guardar. Devuelve también la DelegacionTenant
    /// de la fila heredada que añadió al contexto.
    /// </summary>
    private async Task<(ResultadoIncorporacionCartera Resultado, Guid? OrigenDeLaFilaHeredada)> IncorporarAsync(Guid operacionId)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var gestora = FijarSesion(_gestoraOperador, Roles.GestorCae);
        using (AmbitoTenantExplicito.Establecer(_operador))
        {
            var resultado = await new CatalogoIncorporacionCartera(contexto, gestora)
                .IncorporarAsync(_propietario, _operador, operacionId, _gestoraOperador.Id, CancellationToken.None);
            var filaHeredada = contexto.ChangeTracker.Entries<AsignacionOperadorDelegado>()
                .Select(e => (Guid?)e.Entity.DelegacionTenantId)
                .SingleOrDefault();
            return (resultado, filaHeredada);
        }
    }

    private async Task<Guid> SembrarClienteAsync(string nombre, string cif)
    {
        await using var sembrado = ContextoPropietarioDeLaBase(_propietario);
        var empresa = Empresa.CrearComoCliente(nombre, cif, false, null, null);
        sembrado.Empresas.Add(empresa);
        await sembrado.SaveChangesAsync();
        return empresa.Id;
    }

    private static bool TieneEstado(Exception e, string sqlState)
    {
        for (var actual = e; actual is not null; actual = actual.InnerException)
            if (actual is PostgresException postgres && postgres.SqlState == sqlState)
                return true;
        return false;
    }
}
