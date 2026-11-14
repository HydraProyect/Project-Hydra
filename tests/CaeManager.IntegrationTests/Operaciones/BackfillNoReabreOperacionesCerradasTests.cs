using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Decisión de Chris del 2026-11-14 (hallazgo de Codex en la revisión de la PR #2215): el backfill de
/// arranque nunca reabre ni recrea una Asignación de Operación Outbound que ya se cerró o expiró, y una
/// delegación inactiva no deja ninguna autorización Outbound derivada, tampoco acotada. Antes, una
/// operación del Tenant entero expirada antes que su delegación (o revocada con la delegación todavía
/// marcada activa) volvía a autorizar en el arranque siguiente: el backfill solo buscaba una del Tenant
/// entero sin cerrar y, al no encontrarla, creaba otra desde la delegación.
///
/// <para>
/// Como en producción, el backfill corre con la conexión de bootstrap (cross-tenant por diseño), no
/// como <c>cae_app_runtime</c>: lo que se mide aquí es qué filas escribe. Que una operación cerrada no
/// autoriza bajo RLS lo fijan las pruebas de <c>TenantsBeneficiariosAutorizados</c>.
/// </para>
/// </summary>
public class BackfillNoReabreOperacionesCerradasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorOperador = Guid.NewGuid();
    private Guid _operador;
    private Guid _propietario;
    private Guid _delegacion;
    private Guid _clienteEmpresarial;

    public async Task InitializeAsync()
    {
        await using var contextoInicial = CrearContexto(Guid.NewGuid());
        await contextoInicial.Database.MigrateAsync();

        var operador = new Tenant("Operador CAE externo", PerfilVocabularioTenant.Consultora);
        var propietario = new Tenant("Tenant propietario", PerfilVocabularioTenant.ClienteDirecto);
        contextoInicial.Tenants.AddRange(operador, propietario);
        await contextoInicial.SaveChangesAsync();
        (_operador, _propietario) = (operador.Id, propietario.Id);

        await using var contexto = CrearContexto(_propietario);
        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorOperador,
            TenantId = _operador,
            UserName = "gestora@operador",
            Email = "gestora@operador"
        });
        await contexto.SaveChangesAsync();

        var delegacion = new DelegacionTenant(_operador, _propietario);
        contexto.DelegacionesTenant.Add(delegacion);
        var cliente = Empresa.CrearComoCliente("Cliente empresarial acotado", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();
        (_delegacion, _clienteEmpresarial) = (delegacion.Id, cliente.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(MotivoCierreAsignacion.Expirada)]
    [InlineData(MotivoCierreAsignacion.Revocada)]
    public async Task Con_la_delegacion_activa_no_recrea_la_operacion_del_Tenant_entero_ya_cerrada(MotivoCierreAsignacion motivo)
    {
        await EjecutarBackfillAsync();
        (await ExternasOutboundAsync()).Should().ContainSingle("control: la primera pasada migra la delegación")
            .Which.Estado.Should().Be(EstadoAsignacion.Vigente);

        await using (var contexto = CrearContexto(_propietario))
        {
            var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario);
            operacion.Cerrar(motivo, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        var externas = await ExternasOutboundAsync();
        externas.Should().ContainSingle("el arranque no recrea una operación que ya se cerró o expiró")
            .Which.Estado.Should().Be(EstadoAsignacion.Cerrada);
        externas[0].MotivoCierre.Should().Be(motivo, "ni la reabre");
    }

    [Fact]
    public async Task Con_la_delegacion_activa_y_solo_una_operacion_acotada_no_abre_la_del_Tenant_entero()
    {
        // Abrirla ampliaría al Tenant entero lo que alguien acotó a un Cliente empresarial.
        await SembrarAcotadaConCarteraAsync();

        await EjecutarBackfillAsync();

        var externas = await ExternasOutboundAsync();
        externas.Should().ContainSingle().Which.Ambito.RelacionClienteId.Should().Be(_clienteEmpresarial);
    }

    [Fact]
    public async Task Con_la_delegacion_inactiva_cierra_tambien_la_operacion_acotada_y_su_cartera()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        Guid inbound;
        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Desactivar();
            var ahora = DateTime.UtcNow;
            var deInbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(deInbound);
            await contexto.SaveChangesAsync();
            inbound = deInbound.Id;
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        var operacion = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada);
        operacion.Estado.Should().Be(EstadoAsignacion.Cerrada, "una delegación inactiva no deja autorización Outbound acotada");
        operacion.MotivoCierre.Should().Be(MotivoCierreAsignacion.MigradaSinFecha);
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .Where(c => c.AsignacionOperacionId == acotada)
                .Select(c => c.Estado)
                .ToListAsync())
            .Should().ContainSingle().Which.Should().Be(EstadoAsignacion.Cerrada, "ni su cartera");
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == inbound)).Estado
            .Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo: el backfill de delegaciones Outbound no la toca");
    }

    [Fact]
    public async Task Una_delegacion_inactiva_no_cierra_lo_de_otra_delegacion_activa_del_mismo_par()
    {
        // Se revocó la primera y se autorizó otra al mismo Operador CAE externo: lo abierto es de la
        // activa. Cerrarlo dejaría la activa sin autorización y, como el backfill ya no recrea, sin
        // vía de vuelta.
        await EjecutarBackfillAsync();
        Guid nueva;
        await using (var contexto = CrearContexto(_propietario))
        {
            var ahora = DateTime.UtcNow;
            (await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Desactivar();
            (await contexto.AsignacionesOperacion.SingleAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario))
                .Cerrar(MotivoCierreAsignacion.Revocada, ahora);
            await contexto.SaveChangesAsync();

            var activa = new DelegacionTenant(_operador, _propietario);
            contexto.DelegacionesTenant.Add(activa);
            var operacion = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.Universal, ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(operacion);
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacion, _gestorOperador, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
            // Como la deja toda vía que emite una cartera externa: con su fila heredada en la activa.
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(activa.Id, _gestorOperador, Roles.GestorCae));
            await contexto.SaveChangesAsync();
            nueva = operacion.Id;
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == nueva)).Estado
            .Should().Be(EstadoAsignacion.Vigente, "la operación de la delegación activa sigue abierta");
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .Where(c => c.AsignacionOperacionId == nueva)
                .Select(c => c.Estado)
                .SingleAsync())
            .Should().Be(EstadoAsignacion.Vigente, "y su cartera");
        (await ExternasOutboundAsync()).Should().HaveCount(2, "ni recrea ni duplica");
    }

    /// <summary>
    /// Ronda 1 de Codex en la PR #2223 (hallazgo 2): una persona retirada antes de esta PR conservó su
    /// cartera bajo una operación acotada (la retirada solo cerraba la del Tenant entero) y su fila
    /// heredada ya no existe. Con la delegación activa, el backfill la cierra; la de quien sí tiene
    /// fila sigue, y la cartera Inbound de la persona retirada no se toca.
    /// </summary>
    [Fact]
    public async Task Con_la_delegacion_activa_cierra_la_cartera_de_una_persona_sin_fila_heredada_tambien_bajo_una_acotada()
    {
        await EjecutarBackfillAsync();
        var conFila = await SembrarUsuarioAsync("con-fila@operador");
        Guid huerfanaAcotada, huerfanaTenantEntero, conservada, deInbound;
        await using (var contexto = CrearContexto(_propietario))
        {
            var ahora = DateTime.UtcNow;
            var tenantEntero = await contexto.AsignacionesOperacion.SingleAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.Servicio == ServicioCae.Outbound);
            var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(_clienteEmpresarial), ahora.AddDays(-1), null, ahora);
            var inbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.AddRange(acotada, inbound);
            var c1 = AsignacionCartera.Externa(acotada, _gestorOperador, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            var c2 = AsignacionCartera.Externa(tenantEntero, _gestorOperador, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            var c3 = AsignacionCartera.Externa(acotada, conFila, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            var c4 = AsignacionCartera.Externa(inbound, _gestorOperador, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            contexto.AsignacionesCartera.AddRange(c1, c2, c3, c4);
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacion, conFila, Roles.GestorCae));
            await contexto.SaveChangesAsync();
            (huerfanaAcotada, huerfanaTenantEntero, conservada, deInbound) = (c1.Id, c2.Id, c3.Id, c4.Id);
        }

        await EjecutarBackfillAsync();

        (await EstadoCarteraAsync(huerfanaAcotada)).Should().Be(EstadoAsignacion.Cerrada,
            "sin fila heredada en una delegación activa y vigente, su cartera bajo la acotada no tiene quien la ampare");
        (await EstadoCarteraAsync(huerfanaTenantEntero)).Should().Be(EstadoAsignacion.Cerrada, "tampoco la del Tenant entero");
        (await EstadoCarteraAsync(conservada)).Should().Be(EstadoAsignacion.Vigente, "control: quien tiene fila conserva la suya");
        (await EstadoCarteraAsync(deInbound)).Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo");
    }

    /// <summary>
    /// Ronda 1 de Codex en la PR #2223 (hallazgo 3): con una delegación inactiva y otra activa del
    /// mismo par, la asignación histórica de la inactiva no recibe una cartera universal bajo la
    /// operación de la activa. Control: la misma asignación en la activa sí la recibe.
    /// </summary>
    [Fact]
    public async Task Una_asignacion_historica_de_la_delegacion_inactiva_no_recibe_cartera_bajo_la_operacion_de_la_activa()
    {
        await EjecutarBackfillAsync();
        var historica = await SembrarUsuarioAsync("historica@operador");
        var actual = await SembrarUsuarioAsync("actual@operador");
        await using (var contexto = CrearContexto(_propietario))
        {
            var ahora = DateTime.UtcNow;
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacion, historica, Roles.Consulta));
            (await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Desactivar();
            (await contexto.AsignacionesOperacion.SingleAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario))
                .Cerrar(MotivoCierreAsignacion.Revocada, ahora);
            await contexto.SaveChangesAsync();

            var activa = new DelegacionTenant(_operador, _propietario);
            contexto.DelegacionesTenant.Add(activa);
            var operacionDeLaActiva = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.Universal, ahora, null, ahora);
            contexto.AsignacionesOperacion.Add(operacionDeLaActiva);
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(activa.Id, actual, Roles.Consulta));
            // Y una cartera de la persona histórica bajo la operación de la activa: su única fila es la
            // de la inactiva, que no la ampara.
            contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
                operacionDeLaActiva, historica, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
            await contexto.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .AnyAsync(c => c.UsuarioId == historica && c.Estado != EstadoAsignacion.Cerrada))
            .Should().BeFalse("una asignación de una delegación inactiva no concede ni sostiene nada bajo la operación de otra");
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .CountAsync(c => c.UsuarioId == actual && c.Estado == EstadoAsignacion.Vigente))
            .Should().Be(1, "control: la de la activa sí recibe su cartera universal de Consulta");
    }

    /// <summary>
    /// Ronda 1 de Codex en la PR #2223 (hallazgo 4): una delegación inactiva no deja abierta la
    /// operación del par por otra que sigue marcada activa pero ya caducó: esa no autoriza nada.
    /// </summary>
    [Fact]
    public async Task Una_delegacion_activa_pero_caducada_no_mantiene_abierta_la_operacion_de_una_inactiva_del_mismo_par()
    {
        await EjecutarBackfillAsync();
        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Desactivar();
            await contexto.SaveChangesAsync();

            var caducada = new DelegacionTenant(_operador, _propietario);
            contexto.DelegacionesTenant.Add(caducada);
            contexto.Entry(caducada).Property(d => d.ExpiraEnUtc).CurrentValue = DateTime.UtcNow.AddDays(-1);
            await contexto.SaveChangesAsync();
            caducada.Activa.Should().BeTrue("control: sigue marcada activa");
        }

        await EjecutarBackfillAsync();

        (await ExternasOutboundAsync()).Should().ContainSingle()
            .Which.Estado.Should().Be(EstadoAsignacion.Cerrada,
                "sin delegación activa y vigente del par, la operación del Tenant entero no queda abierta");
    }

    /// <summary>
    /// Ronda 2 de Codex en la PR #2223 (hallazgo 4): una delegación única, marcada activa pero ya
    /// caducada, no deja autorización Outbound derivada. El job de expiración cierra la operación del
    /// Tenant entero, que lleva la caducidad de la delegación, pero no una acotada sin fecha de fin ni su
    /// cartera. Antes el backfill solo cerraba lo del par con la delegación inactiva.
    /// </summary>
    [Fact]
    public async Task Con_la_unica_delegacion_activa_pero_caducada_cierra_la_operacion_acotada_y_su_cartera()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        Guid inbound;
        DateTime caducidad;
        await using (var contexto = CrearContexto(_propietario))
        {
            var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion);
            caducidad = DateTime.UtcNow.AddMinutes(-1);
            caducidad = new DateTime(caducidad.Ticks - caducidad.Ticks % 10, DateTimeKind.Utc);
            contexto.Entry(delegacion).Property(d => d.ExpiraEnUtc).CurrentValue = caducidad;
            var ahora = DateTime.UtcNow;
            var deInbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(deInbound);
            await contexto.SaveChangesAsync();
            delegacion.Activa.Should().BeTrue("control: sigue marcada activa");
            inbound = deInbound.Id;
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        var operacion = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada);
        operacion.Estado.Should().Be(EstadoAsignacion.Cerrada, "una delegación caducada no deja autorización Outbound acotada");
        operacion.MotivoCierre.Should().Be(MotivoCierreAsignacion.Expirada);
        operacion.VigenciaHasta.Should().Be(caducidad, "se cierra en la fecha en que caducó la delegación");
        var cartera = await verificacion.AsignacionesCartera.IgnoreQueryFilters().SingleAsync(c => c.AsignacionOperacionId == acotada);
        cartera.Estado.Should().Be(EstadoAsignacion.Cerrada, "ni su cartera sin vencimiento");
        (await ExternasOutboundAsync()).Should().ContainSingle("control: no crea la del Tenant entero desde la caducada");
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == inbound)).Estado
            .Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo");
    }

    /// <summary>
    /// Ronda 2 de Codex en la PR #2223 (hallazgo 3, backfill): con la cartera de Consulta Suspendida —viva
    /// o cerrada desde Suspendida—, el arranque no emite otra Vigente desde la fila heredada. Antes la
    /// emitía en cuanto no encontraba una Vigente. Control: la persona sin cartera sí la recibe.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_emite_la_cartera_de_Consulta_si_la_ultima_de_la_persona_quedo_suspendida(bool cerrarla)
    {
        await EjecutarBackfillAsync();
        var suspendida = await SembrarUsuarioAsync("consulta-suspendida@operador");
        var sinCartera = await SembrarUsuarioAsync("consulta-nueva@operador");
        await using (var contexto = CrearContexto(_propietario))
        {
            var ahora = DateTime.UtcNow;
            var tenantEntero = await contexto.AsignacionesOperacion.SingleAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.Servicio == ServicioCae.Outbound);
            var cartera = AsignacionCartera.Externa(
                tenantEntero, suspendida, Roles.Consulta, AmbitoAsignacion.Universal, ahora.AddMinutes(-5), null, ahora);
            cartera.Suspender();
            if (cerrarla) cartera.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
            contexto.AsignacionesCartera.Add(cartera);
            contexto.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                new AsignacionOperadorDelegado(_delegacion, suspendida, Roles.Consulta),
                new AsignacionOperadorDelegado(_delegacion, sinCartera, Roles.Consulta));
            await contexto.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .AnyAsync(c => c.UsuarioId == suspendida && c.Estado == EstadoAsignacion.Vigente))
            .Should().BeFalse("lo suspendido nunca vuelve como vigente, tampoco en el arranque");
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .CountAsync(c => c.UsuarioId == sinCartera && c.Estado == EstadoAsignacion.Vigente))
            .Should().Be(1, "control: quien no tenía cartera recibe la de Consulta");
    }

    /// <summary>
    /// P8, trasladado desde RolesDelegadosSoloDeOperacionTests al retirarse la reposición al reactivar
    /// (opción A en la PR #2223): la fila heredada revocada no emite cartera en el arranque, aunque su
    /// persona nunca haya tenido cartera en el par. La fila revocada es de Consulta, el único rol para el
    /// que el backfill emite cartera automáticamente: con otro rol el test pasaría aunque se leyera la
    /// fila revocada. Control positivo: una fila de Consulta de la misma delegación, igual salvo por la
    /// revocación, sí la emite.
    /// </summary>
    [Fact]
    public async Task No_emite_la_cartera_de_una_fila_heredada_revocada()
    {
        await EjecutarBackfillAsync();
        var revocado = await SembrarUsuarioAsync("consulta-revocada@operador");
        var valida = await SembrarUsuarioAsync("consulta-valida@operador");
        await using (var contexto = CrearContexto(_propietario))
        {
            var revocada = new AsignacionOperadorDelegado(_delegacion, revocado, Roles.Consulta);
            revocada.Revocar("Prueba de revocación", DateTime.UtcNow);
            contexto.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                revocada, new AsignacionOperadorDelegado(_delegacion, valida, Roles.Consulta));
            await contexto.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters().AnyAsync(c => c.UsuarioId == revocado))
            .Should().BeFalse("la fila heredada revocada no emite cartera");
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .CountAsync(c => c.UsuarioId == valida && c.Estado == EstadoAsignacion.Vigente))
            .Should().Be(1, "control positivo: la válida de la misma delegación sí la emite");
    }

    /// <summary>
    /// Ronda 1 de Codex en la PR #2223 (hallazgo 6): la reconciliación de roles de Propiedad cierra
    /// carteras externas Outbound, no Inbound; y también las Programadas, que el job activaría al
    /// llegar su fecha.
    /// </summary>
    [Fact]
    public async Task La_reconciliacion_de_roles_de_Propiedad_cierra_tambien_la_programada_Outbound_y_no_toca_Inbound()
    {
        await EjecutarBackfillAsync();
        Guid deInbound, programadaOutbound;
        await using (var contexto = CrearContexto(_propietario))
        {
            var ahora = DateTime.UtcNow;
            var tenantEntero = await contexto.AsignacionesOperacion.SingleAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.Servicio == ServicioCae.Outbound);
            var inbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(inbound);
            var cartera = AsignacionCartera.Externa(
                inbound, _gestorOperador, Roles.Administrador, AmbitoAsignacion.Universal, ahora, null, ahora);
            var programada = AsignacionCartera.Externa(
                tenantEntero, _gestorOperador, Roles.Administrador, AmbitoAsignacion.Universal, ahora.AddDays(2), null, ahora);
            contexto.AsignacionesCartera.AddRange(cartera, programada);
            // Con fila, para que la cierre la reconciliación de roles y no la de personas sin fila.
            contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(_delegacion, _gestorOperador, Roles.GestorCae));
            await contexto.SaveChangesAsync();
            programada.Estado.Should().Be(EstadoAsignacion.Programada, "control: empieza en el futuro");
            (deInbound, programadaOutbound) = (cartera.Id, programada.Id);
        }

        await EjecutarBackfillAsync();

        (await EstadoCarteraAsync(programadaOutbound)).Should().Be(EstadoAsignacion.Cerrada,
            "un rol de Propiedad no se delega, tampoco en una cartera Outbound que aún no ha empezado");
        (await EstadoCarteraAsync(deInbound)).Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo");
    }

    private async Task<EstadoAsignacion> EstadoCarteraAsync(Guid carteraId)
    {
        await using var contexto = CrearContexto(_propietario);
        return (await contexto.AsignacionesCartera.IgnoreQueryFilters().SingleAsync(c => c.Id == carteraId)).Estado;
    }

    private async Task<Guid> SembrarUsuarioAsync(string correo)
    {
        await using var contexto = CrearContexto(_propietario);
        var usuario = new ApplicationUser { Id = Guid.NewGuid(), TenantId = _operador, UserName = correo, Email = correo };
        contexto.Users.Add(usuario);
        await contexto.SaveChangesAsync();
        return usuario.Id;
    }

    private async Task<Guid> SembrarAcotadaConCarteraAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_clienteEmpresarial), ahora.AddDays(-1), null, ahora);
        contexto.AsignacionesOperacion.Add(acotada);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
            acotada, _gestorOperador, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
        await contexto.SaveChangesAsync();
        return acotada.Id;
    }

    private async Task<List<AsignacionOperacion>> ExternasOutboundAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        return await contexto.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound)
            .ToListAsync();
    }

    private async Task EjecutarBackfillAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
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
