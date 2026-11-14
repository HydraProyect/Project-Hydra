using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
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
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Decisión de Chris del 2026-11-14 (hallazgo de Codex en la revisión de la PR #2215): revocar la
/// delegación de un Operador CAE externo, o retirar a una persona de su equipo, cierra toda la
/// autorización Outbound que deriva de ella sobre el Tenant propietario: las Asignaciones de
/// Operación del Tenant entero y también las acotadas a un Cliente empresarial, con sus
/// Asignaciones de Cartera. Antes solo se cerraba la operación vigente del Tenant entero, y una
/// acotada con su cartera seguía autorizando por Operación
/// (<c>TenantsBeneficiariosAutorizados.CarterasPorOperacionDeLaOrganizacion</c> no mira el ámbito).
///
/// <para>
/// Contra Postgres real con el cableado de producción del <see cref="ArnesDeArranqueRuntime"/>
/// (<c>cae_app_runtime</c>, interceptores, RLS sin debilitar) y con Identity real. Cada persona actúa
/// desde su propio Workspace activo, como en producción. El propietario de la base solo siembra lo
/// que ningún comando de hoy escribe (las operaciones acotadas) y lee el resultado. La vía de
/// Soporte TALVEG se mide en <c>SoporteRevocaOperadorCaeExternoBajoRuntimeTests</c> y el backfill en
/// <c>BackfillNoReabreOperacionesCerradasTests</c>.
/// </para>
/// </summary>
public class RevocacionCierraOperacionesOutboundAcotadasBajoRuntimeTests : IAsyncLifetime
{
    private const string Contrasena = "Arnes#2026Seguro";

    private readonly ActorMutable _actor = new();
    private readonly CurrentUserServiceMutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _propietario;
    private Guid _operador;
    private Guid _clienteUno;
    private Guid _clienteDos;

    private ApplicationUser _administradorPropietario = null!;
    private ApplicationUser _gestoraOperador = null!;
    private ApplicationUser _otroGestorOperador = null!;

    private Guid _delegacion;
    private Guid _operacionTenantEntero;
    private Guid _acotadaVigente;
    private Guid _acotadaProgramada;
    private Guid _inbound;
    private Guid _carteraProgramadaPrincipal;

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
            var uno = Empresa.CrearComoCliente("Cliente acotado uno S.A.", "B10380202", false, null, null);
            var dos = Empresa.CrearComoCliente("Cliente acotado dos S.A.", "B10380228", false, null, null);
            sembrado.Empresas.AddRange(uno, dos);
            await sembrado.SaveChangesAsync();
            (_clienteUno, _clienteDos) = (uno.Id, dos.Id);
        }

        _administradorPropietario = await SembrarUsuarioAsync("admin-propietario", _propietario, Roles.Administrador);
        _gestoraOperador = await SembrarUsuarioAsync("gestora-operador", _operador, Roles.GestorCae);
        _otroGestorOperador = await SembrarUsuarioAsync("otro-gestor-operador", _operador, Roles.GestorCae);

        // La delegación y su operación del Tenant entero, como las deja el alta real.
        var alta = await AutorizarAsync(_administradorPropietario);
        alta.EsExitoso.Should().BeTrue(alta.EsFallido ? alta.Error.Codigo : string.Empty);
        _delegacion = alta.Valor;

        // Lo que ningún comando de hoy escribe y el modelo admite: dos operaciones Outbound acotadas
        // a un Cliente empresarial (una vigente, otra programada que el job activaría al llegar su
        // fecha) y una Inbound del mismo par. Las carteras son del Tenant entero (D-7) bajo cada
        // operación; la de la Gestora CAE bajo la del Tenant entero es la principal.
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var ahora = DateTime.UtcNow;
            var tenantEntero = await propietarioDeLaBase.AsignacionesOperacion.SingleAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                && o.Servicio == ServicioCae.Outbound && o.Estado == EstadoAsignacion.Vigente);
            var acotadaVigente = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(_clienteUno), ahora.AddDays(-1), null, ahora);
            var acotadaProgramada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(_clienteDos), ahora.AddDays(2), null, ahora);
            var inbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            propietarioDeLaBase.AsignacionesOperacion.AddRange(acotadaVigente, acotadaProgramada, inbound);

            var principal = AsignacionCartera.Externa(
                tenantEntero, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora);
            principal.DesignarPrincipal();
            // Programada y principal: la cascada la cierra sin recordar la marca, porque no era la
            // principal vigente de nada (D-9); en duda, falla cerrada.
            var programadaPrincipal = AsignacionCartera.Externa(
                acotadaProgramada, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora.AddDays(3), null, ahora);
            programadaPrincipal.DesignarPrincipal();
            _carteraProgramadaPrincipal = programadaPrincipal.Id;
            propietarioDeLaBase.AsignacionesCartera.AddRange(
                principal,
                AsignacionCartera.Externa(acotadaVigente, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora),
                AsignacionCartera.Externa(acotadaVigente, _otroGestorOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora),
                programadaPrincipal,
                AsignacionCartera.Externa(inbound, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
            propietarioDeLaBase.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                new AsignacionOperadorDelegado(_delegacion, _gestoraOperador.Id, Roles.GestorCae),
                new AsignacionOperadorDelegado(_delegacion, _otroGestorOperador.Id, Roles.GestorCae));
            await propietarioDeLaBase.SaveChangesAsync();

            acotadaProgramada.Estado.Should().Be(EstadoAsignacion.Programada, "control: empieza en el futuro");
            (_operacionTenantEntero, _acotadaVigente, _acotadaProgramada, _inbound) =
                (tenantEntero.Id, acotadaVigente.Id, acotadaProgramada.Id, inbound.Id);
        }
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Revocar_la_delegacion_cierra_tambien_las_operaciones_Outbound_acotadas_y_sus_carteras_bajo_RLS()
    {
        (await EstadoOperacionAsync(_acotadaVigente)).Should().Be(EstadoAsignacion.Vigente, "control: la acotada sigue abierta");
        (await CarterasNoCerradasAsync(_acotadaVigente)).Should().Be(2, "control: con sus dos carteras");
        (await EstadoOperacionAsync(_acotadaProgramada)).Should().Be(EstadoAsignacion.Programada);

        var revocar = await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae);
        revocar.EsExitoso.Should().BeTrue(revocar.EsFallido ? revocar.Error.Codigo : string.Empty);

        foreach (var operacion in new[] { _operacionTenantEntero, _acotadaVigente, _acotadaProgramada })
        {
            (await EstadoOperacionAsync(operacion)).Should().Be(EstadoAsignacion.Cerrada,
                "revocar cierra toda Asignación de Operación Outbound del par, del Tenant entero o acotada");
            (await CarterasNoCerradasAsync(operacion)).Should().Be(0, "y todas sus carteras");
        }

        (await EstadoOperacionAsync(_inbound)).Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo");
        (await CarterasNoCerradasAsync(_inbound)).Should().Be(1);

        await using var verificacion = ContextoPropietarioDeLaBase();
        var programada = await verificacion.AsignacionesCartera.IgnoreQueryFilters()
            .SingleAsync(c => c.Id == _carteraProgramadaPrincipal);
        programada.EraPrincipalAlCerrarsePorCascada.Should().BeFalse(
            "una cartera que no estaba vigente se cierra sin recordar la marca (D-9)");
    }

    [Fact]
    public async Task Retirar_a_la_persona_cierra_sus_carteras_tambien_bajo_las_operaciones_Outbound_acotadas_bajo_RLS()
    {
        (await CarterasNoCerradasAsync(_acotadaVigente, _gestoraOperador.Id)).Should().Be(1, "control: cartera abierta bajo la acotada");

        var asignacion = await AsignacionOperadorAsync(_gestoraOperador.Id);
        var retirar = await RetirarPersonaAsync(_administradorPropietario, Roles.Administrador, asignacion);
        retirar.EsExitoso.Should().BeTrue(retirar.EsFallido ? retirar.Error.Codigo : string.Empty);

        foreach (var operacion in new[] { _operacionTenantEntero, _acotadaVigente, _acotadaProgramada })
            (await CarterasNoCerradasAsync(operacion, _gestoraOperador.Id)).Should().Be(0,
                "retirar a la persona le cierra todas sus carteras Outbound del par, de Tenant entero o acotadas");

        (await CarterasNoCerradasAsync(_acotadaVigente, _otroGestorOperador.Id)).Should().Be(1, "la de otra persona sigue");
        (await EstadoOperacionAsync(_acotadaVigente)).Should().Be(EstadoAsignacion.Vigente, "retirar no cierra la operación");
        (await CarterasNoCerradasAsync(_inbound, _gestoraOperador.Id)).Should().Be(1, "Inbound tiene su propio ciclo");
    }

    /// <summary>
    /// Decisión de Chris del 2026-11-14 (opción A en la PR #2223, supera D-9): reactivar la
    /// DelegacionTenant reabre solo la Asignación de Operación del Tenant entero, ninguna acotada, y no
    /// repone ninguna Asignación de Cartera ni la marca de principal, aunque una acotada se cerrase
    /// después que la del Tenant entero.
    /// </summary>
    [Fact]
    public async Task Reactivar_solo_reabre_la_del_Tenant_entero_y_no_repone_ninguna_cartera_bajo_RLS()
    {
        (await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae)).EsExitoso.Should().BeTrue();

        // Y una acotada que se cerró después que la del Tenant entero, ya en el pasado: por fecha
        // sería la «anterior» si el filtro no exigiera el Tenant entero.
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var ahora = DateTime.UtcNow;
            var posterior = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.DeRelacionCliente(_clienteUno), ahora.AddMinutes(-1), null, ahora);
            posterior.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
            propietarioDeLaBase.AsignacionesOperacion.Add(posterior);
            await propietarioDeLaBase.SaveChangesAsync();
        }

        var reactivar = await ReactivarAsync(_administradorPropietario, Roles.Administrador);
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        await using var verificacion = ContextoPropietarioDeLaBase();
        var abiertas = await verificacion.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound && o.Estado != EstadoAsignacion.Cerrada)
            .ToListAsync();
        abiertas.Should().ContainSingle("reactivar reabre una sola operación, la del Tenant entero")
            .Which.Ambito.EsUniversal.Should().BeTrue();
        (await EstadoOperacionAsync(_acotadaVigente)).Should().Be(EstadoAsignacion.Cerrada, "las acotadas necesitan un acto explícito");
        (await EstadoOperacionAsync(_acotadaProgramada)).Should().Be(EstadoAsignacion.Cerrada);

        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .CountAsync(c => c.AsignacionOperacionId == abiertas[0].Id))
            .Should().Be(0, "reactivar no repone ninguna cartera, tampoco la de la Gestora CAE principal (opción A)");
        (await CarterasNoCerradasAsync(_operacionTenantEntero, _gestoraOperador.Id)).Should().Be(0,
            "ni reabre la que cerró la revocación");
    }

    /// <summary>
    /// Rondas 1 y 2 de Codex en la PR #2223: una cartera Suspendida bajo la operación del Tenant entero
    /// se cierra al revocar, y la reactivación no la devuelve aunque su persona siga en el equipo con el
    /// mismo rol y comparta con su operación una fecha de fin ya pasada. Con la opción A (decisión de
    /// Chris del 2026-11-14) tampoco vuelve la que estaba Vigente: reactivar no repone ninguna cartera.
    /// </summary>
    [Fact]
    public async Task Reactivar_no_devuelve_ni_la_cartera_suspendida_ni_la_vigente_al_revocar_bajo_RLS()
    {
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var tenantEntero = await propietarioDeLaBase.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionTenantEntero);
            var inicio = tenantEntero.VigenciaDesde;
            var ahora = DateTime.UtcNow;
            var finPasado = inicio + (ahora - inicio) / 2;
            finPasado = new DateTime(finPasado.Ticks - finPasado.Ticks % 10, DateTimeKind.Utc);
            var cartera = AsignacionCartera.Externa(
                tenantEntero, _otroGestorOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, inicio, finPasado, inicio);
            cartera.Suspender();
            propietarioDeLaBase.AsignacionesCartera.Add(cartera);
            await propietarioDeLaBase.SaveChangesAsync();

            // La colisión: la operación, todavía Vigente (el job aún no la ha cerrado), con la misma
            // fecha de fin ya pasada que la suspendida.
            await propietarioDeLaBase.AsignacionesOperacion.Where(o => o.Id == _operacionTenantEntero)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.VigenciaHasta, finPasado));
        }

        (await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae)).EsExitoso.Should().BeTrue();

        await using (var cerradas = ContextoPropietarioDeLaBase())
        {
            var operacion = await cerradas.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionTenantEntero);
            var suspendida = await cerradas.AsignacionesCartera.IgnoreQueryFilters()
                .SingleAsync(c => c.AsignacionOperacionId == _operacionTenantEntero && c.UsuarioId == _otroGestorOperador.Id);
            suspendida.VigenciaHasta.Should().Be(operacion.VigenciaHasta, "control: la colisión de fechas sigue ahí");
            suspendida.Estado.Should().Be(EstadoAsignacion.Cerrada);
        }
        var reactivar = await ReactivarAsync(_administradorPropietario, Roles.Administrador);
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        var reabierta = await OperacionReabiertaAsync();
        (await CarterasNoCerradasAsync(reabierta, _otroGestorOperador.Id)).Should().Be(0,
            "la cartera que estaba suspendida al revocar no vuelve, y menos como Vigente");
        (await CarterasNoCerradasAsync(reabierta, _gestoraOperador.Id)).Should().Be(0,
            "y tampoco la que estaba vigente: reactivar no repone ninguna (opción A)");
    }

    /// <summary>
    /// Ronda 2 de Codex en la PR #2223 (hallazgo 2): la revocación cierra también una operación del
    /// Tenant entero Suspendida, pero sus carteras Vigentes no autorizaban nada: se cierran sin marca y
    /// con su estado efectivo Suspendido, y la reactivación no las repone bajo la operación nueva Vigente,
    /// tampoco la marca de principal.
    /// </summary>
    [Fact]
    public async Task Reactivar_no_repone_las_carteras_de_una_operacion_que_estaba_suspendida_al_revocar_bajo_RLS()
    {
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            (await propietarioDeLaBase.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionTenantEntero)).Suspender();
            await propietarioDeLaBase.SaveChangesAsync();
        }

        (await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae)).EsExitoso.Should().BeTrue();

        await using (var cerradas = ContextoPropietarioDeLaBase())
        {
            var principal = await cerradas.AsignacionesCartera.IgnoreQueryFilters()
                .SingleAsync(c => c.AsignacionOperacionId == _operacionTenantEntero && c.UsuarioId == _gestoraOperador.Id);
            principal.Estado.Should().Be(EstadoAsignacion.Cerrada);
            principal.EraPrincipalAlCerrarsePorCascada.Should().BeFalse("bajo una operación suspendida no era la principal vigente de nada");
        }

        var reactivar = await ReactivarAsync(_administradorPropietario, Roles.Administrador);
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        var reabierta = await OperacionReabiertaAsync();
        (await CarterasNoCerradasAsync(reabierta, _gestoraOperador.Id)).Should().Be(0,
            "lo suspendido nunca vuelve como vigente: la cartera de la operación suspendida no se repone");
    }

    /// <summary>
    /// Ronda 2 de Codex en la PR #2223 (hallazgo 3, writer): la cartera de Consulta nace de la fila
    /// heredada solo para quien nunca tuvo cartera en el par. Al reactivar, ni la de quien la tenía
    /// Suspendida al revocar ni la de quien la tenía Vigente vuelven (opción A, decisión de Chris del
    /// 2026-11-14): reactivar no repone carteras, tampoco la de Consulta.
    /// </summary>
    [Fact]
    public async Task Reactivar_no_emite_la_cartera_de_Consulta_ni_a_quien_la_tenia_suspendida_ni_vigente_bajo_RLS()
    {
        var consultaSuspendida = await SembrarUsuarioAsync("consulta-suspendida", _operador, Roles.Consulta);
        var consultaVigente = await SembrarUsuarioAsync("consulta-vigente", _operador, Roles.Consulta);
        await SembrarConsultaAsync(consultaSuspendida.Id, suspendida: true);
        await SembrarConsultaAsync(consultaVigente.Id, suspendida: false);

        (await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae)).EsExitoso.Should().BeTrue();
        var reactivar = await ReactivarAsync(_administradorPropietario, Roles.Administrador);
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        var reabierta = await OperacionReabiertaAsync();
        (await CarterasNoCerradasAsync(reabierta, consultaSuspendida.Id)).Should().Be(0,
            "la fila heredada no devuelve como vigente lo que estaba suspendido");
        (await CarterasNoCerradasAsync(reabierta, consultaVigente.Id)).Should().Be(0,
            "ni la de quien la tenía vigente: ya tuvo cartera en el par y reactivar no la repone");
    }

    /// <summary>
    /// Ronda 2 de Codex en la PR #2223 (hallazgo 3, writer): con la cartera de Consulta Suspendida y viva,
    /// abrir la de Consulta desde la fila heredada no emite otra Vigente a su lado. Antes solo se miraba
    /// si ya había una Vigente.
    /// </summary>
    [Fact]
    public async Task Abrir_la_cartera_de_Consulta_no_emite_una_vigente_junto_a_una_suspendida_viva_bajo_RLS()
    {
        var consulta = await SembrarUsuarioAsync("consulta-viva", _operador, Roles.Consulta);
        var otra = await SembrarUsuarioAsync("consulta-sin-cartera", _operador, Roles.Consulta);
        await SembrarConsultaAsync(consulta.Id, suspendida: true);

        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var servicios = ambito.ServiceProvider;
            var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
            var usuarioActual = FijarSesion(_administradorPropietario, Roles.Administrador);
            var writer = new AsignacionesOperativasWriter(contexto, servicios.GetRequiredService<ITenantActual>(), usuarioActual);
            using (AmbitoTenantExplicito.Establecer(_administradorPropietario.TenantId))
            {
                await writer.AbrirCarteraOperadorAsync(_propietario, _operador, consulta.Id, Roles.Consulta);
                await writer.AbrirCarteraOperadorAsync(_propietario, _operador, otra.Id, Roles.Consulta);
                await contexto.SaveChangesAsync();
            }
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .Where(c => c.UsuarioId == consulta.Id && c.AsignacionOperacionId == _operacionTenantEntero)
                .Select(c => c.Estado).ToListAsync())
            .Should().Equal([EstadoAsignacion.Suspendida], "ninguna Vigente junto a la suspendida");
        (await CarterasNoCerradasAsync(_operacionTenantEntero, otra.Id)).Should().Be(1,
            "control: quien no tenía cartera recibe la de Consulta");
    }

    private async Task SembrarConsultaAsync(Guid usuarioId, bool suspendida)
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var tenantEntero = await propietarioDeLaBase.AsignacionesOperacion.SingleAsync(o => o.Id == _operacionTenantEntero);
        var cartera = AsignacionCartera.Externa(
            tenantEntero, usuarioId, Roles.Consulta, AmbitoAsignacion.Universal, ahora.AddMinutes(-5), null, ahora);
        if (suspendida) cartera.Suspender();
        propietarioDeLaBase.AsignacionesCartera.Add(cartera);
        propietarioDeLaBase.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(_delegacion, usuarioId, Roles.Consulta));
        await propietarioDeLaBase.SaveChangesAsync();
    }

    /// <summary>
    /// Ronda 1 de Codex en la PR #2223 (hallazgo 5): una operación del Tenant entero que se cerró sin
    /// haber empezado lleva su inicio como fin. Con la opción A (decisión de Chris del 2026-11-14) ya no
    /// hay «etapa anterior» que buscar: la reactivación abre una operación nueva del Tenant entero, no
    /// reabre esa ni la que cerró la cascada, y no repone ninguna cartera.
    /// </summary>
    [Fact]
    public async Task Reactivar_abre_una_operacion_nueva_sin_carteras_aunque_otra_del_Tenant_entero_nunca_empezase_bajo_RLS()
    {
        (await RevocarDelegacionAsync(_gestoraOperador, Roles.GestorCae)).EsExitoso.Should().BeTrue();
        Guid nuncaEmpezoId;

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var inicio = DateTime.UtcNow;
            var nuncaEmpezo = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.Universal, inicio, null, inicio.AddSeconds(-1));
            nuncaEmpezo.Estado.Should().Be(EstadoAsignacion.Programada, "control: nace programada");
            nuncaEmpezo.Cerrar(MotivoCierreAsignacion.Revocada, inicio.AddSeconds(-1));
            nuncaEmpezo.VigenciaHasta.Should().Be(inicio, "control: cerrada antes de empezar, su fin es su inicio");
            propietarioDeLaBase.AsignacionesOperacion.Add(nuncaEmpezo);
            await propietarioDeLaBase.SaveChangesAsync();
            nuncaEmpezoId = nuncaEmpezo.Id;
        }

        var reactivar = await ReactivarAsync(_administradorPropietario, Roles.Administrador);
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        var reabierta = await OperacionReabiertaAsync();
        reabierta.Should().NotBe(_operacionTenantEntero).And.NotBe(nuncaEmpezoId, "la operación vigente es nueva");
        (await EstadoOperacionAsync(nuncaEmpezoId)).Should().Be(EstadoAsignacion.Cerrada);
        (await CarterasNoCerradasAsync(reabierta)).Should().Be(0, "reactivar no repone ninguna cartera (opción A)");
    }

    private async Task<Guid> OperacionReabiertaAsync()
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return await verificacion.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound && o.Estado != EstadoAsignacion.Cerrada)
            .Select(o => o.Id)
            .SingleAsync();
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

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
            Autorizacion(servicios), usuarioActual, contexto, new TransaccionDeComando(contexto),
            new AsignacionAutomaticaInerte(), tenant);

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new CrearDelegacionTenantCommand(_operador, _propietario), CancellationToken.None);
    }

    private async Task<Result> RevocarDelegacionAsync(ApplicationUser usuario, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, tenant,
            Directorio(servicios, contexto, tenant, usuarioActual), contexto,
            new CatalogoIncorporacionCartera(contexto, usuarioActual));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacion), CancellationToken.None);
    }

    private async Task<Result> RetirarPersonaAsync(ApplicationUser usuario, string rol, Guid asignacionId)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
            new AsignacionOperadorDelegadoRepository(contexto), new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, tenant, Autorizacion(servicios));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new RevocarAsignacionOperadorDelegadoCommand(asignacionId), CancellationToken.None);
    }

    private async Task<Result> ReactivarAsync(ApplicationUser usuario, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var directorio = Directorio(servicios, contexto, tenant, usuarioActual);
        var handler = new ReactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, contexto,
            new TransaccionDeComando(contexto), tenant, Autorizacion(servicios));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new ReactivarDelegacionTenantCommand(_delegacion), CancellationToken.None);
    }

    private CurrentUserServiceFalso FijarSesion(ApplicationUser usuario, string rol)
    {
        _actor.Actual = usuario.Id;
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;
        _sesion.Rol = rol;
        return new CurrentUserServiceFalso(usuario.Id, rol, usuario.TenantId);
    }

    private static AutorizacionDelegacionPorAdministradorDelCliente Autorizacion(IServiceProvider servicios) =>
        new(servicios.GetRequiredService<UserManager<ApplicationUser>>());

    private static DirectorioUsuariosTenant Directorio(
        IServiceProvider servicios, CaeManagerDbContext contexto, ITenantActual tenant, ICurrentUserService usuario) =>
        new(servicios.GetRequiredService<UserManager<ApplicationUser>>(), contexto, tenant, new PuertaAccesoDatos(), contexto, usuario);

    private async Task<Guid> AsignacionOperadorAsync(Guid usuarioId)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return await verificacion.AsignacionesOperadorDelegadoConRevocadas
            .Where(a => a.DelegacionTenantId == _delegacion && a.UsuarioId == usuarioId)
            .Select(a => a.Id)
            .SingleAsync();
    }

    private async Task<EstadoAsignacion> EstadoOperacionAsync(Guid operacionId)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == operacionId)).Estado;
    }

    private async Task<int> CarterasNoCerradasAsync(Guid operacionId, Guid? usuarioId = null)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return await verificacion.AsignacionesCartera.IgnoreQueryFilters().CountAsync(c =>
            c.AsignacionOperacionId == operacionId
            && (usuarioId == null || c.UsuarioId == usuarioId)
            && c.Estado != EstadoAsignacion.Cerrada);
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

    /// <summary>
    /// Rol propietario de la base: solo siembra y comprueba. Con <paramref name="tenantSellado"/>,
    /// sella el Tenant de las filas que lo llevan (las Empresas). Lo que se prueba corre siempre por
    /// el arnés, como <c>cae_app_runtime</c>.
    /// </summary>
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
