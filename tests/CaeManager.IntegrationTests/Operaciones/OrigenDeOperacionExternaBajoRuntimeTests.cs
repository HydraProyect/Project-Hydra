using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
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
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Fase de expansión de la procedencia de la Asignación de Operación Outbound externa (decisión de Chris del
/// 2026-11-15 sobre la PR #2237, opción c) contra Postgres real: los triggers de la migración
/// <c>AnadeOrigenDeOperacionExterna</c> y el filtro <c>AmparoDeOrigen.SinOrigenOAmparadasPorSuOrigen</c> de los
/// lectores.
/// <list type="bullet">
/// <item>La inserción sella el origen: el alta real lo fija y la inserción que no lo trae (la ranura anterior) lo
/// deduce si el par tiene una sola DelegacionTenant de Operador CAE externo activa.</item>
/// <item>Con origen, la operación solo autoriza mientras esa DelegacionTenant siga activa y sin revocaciones desde que
/// nació: revocada, revocada y reactivada, o sustituida por otra del par, deja de autorizar.</item>
/// <item>Sin origen, la operación autoriza como antes de la migración: lo decide el resto del predicado de cada
/// lector (en <c>CarterasPorOperacionDeLaOrganizacion</c>, una delegación activa del par).</item>
/// </list>
/// <para>
/// La delegación nace por el alta real (<see cref="CrearDelegacionTenantCommandHandler"/>) bajo el cableado de
/// producción del <see cref="ArnesDeArranqueRuntime"/>. Las operaciones acotadas, que ningún comando de hoy escribe,
/// las siembra el propietario de la base sin fijar el origen, como la ranura anterior. Las revocaciones «de la ranura
/// anterior» se escriben con SQL que solo toca <c>Activa</c>, sin cerrar operaciones. El lector se ejerce como
/// <c>cae_app_runtime</c>.
/// </para>
/// </summary>
public partial class OrigenDeOperacionExternaBajoRuntimeTests : IAsyncLifetime
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
            var cliente = Empresa.CrearComoCliente("Cliente origen S.A.", "B10380236", false, null, null);
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
    public async Task La_insercion_sella_el_origen_el_alta_real_lo_fija_y_la_insercion_sin_origen_lo_deduce()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();

        await using var verificacion = ContextoPropietarioDeLaBase();
        var operaciones = await verificacion.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound)
            .ToListAsync();
        operaciones.Should().HaveCount(2, "control: la del Tenant entero del alta y la acotada sembrada");
        operaciones.Should().OnlyContain(o => o.OrigenId == _delegacion && o.RevocacionesDelOrigen == 0,
            "el writer fija el origen en el alta y el trigger lo deduce en la inserción que no lo trae");
        // Rama «interna → sin origen» del trigger. El Tenant sembrado no tiene raíz (sin datos de prueba nadie le
        // aprovisiona el producto): se inserta aquí, como propietario de la base, una raíz (propietario = operador) que
        // trae explícitos el origen y el contador de la delegación del alta. El trigger tiene que borrarlos. Sin esa rama,
        // la inserción caería en la de origen explícito, que la rechaza porque la delegación no es del par (P, P): el
        // SaveChanges fallaría y el test con él.
        await using (var sembrado = ContextoPropietarioDeLaBase(_propietario))
        {
            var ahora = DateTime.UtcNow;
            var raiz = AsignacionOperacion.Raiz(_propietario, ServicioCae.Outbound, ahora, ahora);
            sembrado.AsignacionesOperacion.Add(raiz);
            sembrado.Entry(raiz).Property(o => o.OrigenId).CurrentValue = _delegacion;
            sembrado.Entry(raiz).Property(o => o.RevocacionesDelOrigen).CurrentValue = 0;
            await sembrado.SaveChangesAsync();
        }
        var raizGuardada = await verificacion.AsignacionesOperacion.AsNoTracking()
            .SingleAsync(o => o.EsRaiz && o.PropietarioTenantId == _propietario);
        raizGuardada.OrigenId.Should().BeNull("la raíz es interna: no nace de ninguna DelegacionTenant");
        raizGuardada.RevocacionesDelOrigen.Should().BeNull("sin origen tampoco hay contador que sellar");
        (await CarterasAutorizadasDelOperadorAsync()).Should().Contain(acotada,
            "con su DelegacionTenant de origen vigente, la operación autoriza");
    }

    /// <summary>
    /// La deducción del trigger bajo la identidad real que inserta en producción. La ranura anterior no conoce
    /// <c>OrigenId</c> y su INSERT no lo nombra; este modelo lo envía a NULL, y para el trigger es lo mismo
    /// (<c>NEW."OrigenId" IS NULL</c> en los dos casos). Como el trigger no es SECURITY DEFINER, lo que se prueba es que
    /// <c>cae_app_runtime</c> en el Tenant propietario ve la DelegacionTenant del par bajo su RLS y puede bloquearla
    /// con <c>FOR SHARE</c>; sin eso la operación nacería sin origen.
    /// </summary>
    [Fact]
    public async Task Bajo_cae_app_runtime_la_insercion_sin_origen_de_la_ranura_anterior_lo_deduce()
    {
        Guid acotada;
        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            FijarSesion(_administradorPropietario, Roles.Administrador);
            using (AmbitoTenantExplicito.Establecer(_propietario))
            {
                var ahora = DateTime.UtcNow;
                var operacion = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
                    AmbitoAsignacion.DeRelacionCliente(_cliente), ahora.AddDays(-1), null, ahora);
                operacion.OrigenId.Should().BeNull("control: la inserción no trae origen, como la de la ranura anterior");
                contexto.AsignacionesOperacion.Add(operacion);
                await contexto.SaveChangesAsync();
                acotada = operacion.Id;
            }
        }

        await using var verificacion = ContextoPropietarioDeLaBase();
        var guardada = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada);
        guardada.OrigenId.Should().Be(_delegacion, "el par tiene una sola DelegacionTenant de Operador CAE externo activa");
        guardada.RevocacionesDelOrigen.Should().Be(0);
    }

    [Fact]
    public async Task Con_origen_y_su_DelegacionTenant_revocada_no_autoriza_ni_al_reactivarla()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        (await CarterasAutorizadasDelOperadorAsync()).Should().Contain(acotada, "control");

        // La ranura anterior solo escribe Activa y no cierra la acotada: el trigger cuenta la revocación igualmente.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        (await CarterasAutorizadasDelOperadorAsync()).Should().NotContain(acotada, "su DelegacionTenant de origen está revocada");

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
        await using (var verificacion = ContextoPropietarioDeLaBase())
        {
            (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones.Should().Be(1);
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).Estado
                .Should().Be(EstadoAsignacion.Vigente, "control: la operación sobrevivió a la revocación");
        }

        (await CarterasAutorizadasDelOperadorAsync()).Should().NotContain(acotada,
            "la delegación de origen vuelve a estar activa, pero se revocó después de nacer la operación");
    }

    [Fact]
    public async Task Con_origen_otra_DelegacionTenant_vigente_del_par_no_la_valida()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.DelegacionesTenant.Add(new DelegacionTenant(_operador, _propietario));
            await propietarioDeLaBase.SaveChangesAsync();
        }

        (await CarterasAutorizadasDelOperadorAsync()).Should().NotContain(acotada,
            "la operación nació de la delegación revocada; la nueva del par no la sostiene");
    }

    [Fact]
    public async Task Sin_origen_la_operacion_autoriza_como_antes_de_la_migracion()
    {
        // Sin delegación activa al insertar, el trigger no tiene de dónde deducir el origen.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        var acotada = await SembrarAcotadaConCarteraAsync();

        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).OrigenId
                .Should().BeNull("control: nació sin delegación vigente de la que deducir el origen");

        (await CarterasAutorizadasDelOperadorAsync()).Should().NotContain(acotada,
            "como antes: sin ninguna delegación activa del par no autoriza");

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        (await CarterasAutorizadasDelOperadorAsync()).Should().Contain(acotada,
            "fase de expansión: sin origen, una delegación activa del par la autoriza, como antes de la migración");
    }

    [Fact]
    public async Task La_via_del_Tenant_entero_deja_de_autorizar_la_operacion_con_origen_revocado_y_reactivado()
    {
        var delTenantEntero = await OperacionDelTenantEnteroAsync();
        (await OperacionesDelTenantEnteroAsync()).Should().Contain(delTenantEntero, "control");

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");

        (await OperacionesDelTenantEnteroAsync()).Should().NotContain(delTenantEntero,
            "la vía sin cartera también exige el amparo de su origen a la operación que lo tiene");
    }

    [Fact]
    public async Task Revocar_y_reactivar_por_los_comandos_reales_abre_una_operacion_nueva_con_el_contador_nuevo()
    {
        var anterior = await OperacionDelTenantEnteroAsync();

        var revocar = await RevocarAsync();
        revocar.EsExitoso.Should().BeTrue(revocar.EsFallido ? revocar.Error.Codigo : string.Empty);
        var reactivar = await ReactivarAsync();
        reactivar.EsExitoso.Should().BeTrue(reactivar.EsFallido ? reactivar.Error.Codigo : string.Empty);

        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones.Should().Be(1);
        var nueva = await verificacion.AsignacionesOperacion.SingleAsync(o =>
            !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
            && o.Servicio == ServicioCae.Outbound && o.Estado != EstadoAsignacion.Cerrada);
        nueva.Id.Should().NotBe(anterior, "control: la revocación cerró la anterior y la reactivación abrió otra");
        nueva.OrigenId.Should().Be(_delegacion);
        nueva.RevocacionesDelOrigen.Should().Be(1,
            "la reactivación se guarda antes de abrir la operación, que nace con el contador ya incrementado");
        (await OperacionesDelTenantEnteroAsync()).Should().Contain(nueva.Id, "la operación nueva está amparada");
    }

    [Fact]
    public async Task Un_origen_explicito_con_el_contador_desfasado_o_una_delegacion_inactiva_se_rechaza()
    {
        await using var contexto = ContextoPropietarioDeLaBase();
        var leida = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);
        leida.Revocaciones.Should().Be(0, "control");

        // Entre la lectura y la inserción, la ranura anterior revoca y reactiva: el contador ya no es el leído.
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = true WHERE "Id" = '{_delegacion}'""");
        (await InsertarAcotadaConOrigenAsync(leida)).Should().BeTrue("una decisión anterior a la revocación no se sella");

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        var inactiva = await contexto.DelegacionesTenant.AsNoTracking().SingleAsync(d => d.Id == _delegacion);
        (await InsertarAcotadaConOrigenAsync(inactiva)).Should().BeTrue("una delegación inactiva no es origen");
    }

    [Fact]
    public async Task Un_origen_explicito_de_otro_par_se_rechaza()
    {
        Guid otraDelegacion;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var otroOperador = await SembrarTenantAsync(
                propietarioDeLaBase, "Otro Operador CAE", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);
            var otra = new DelegacionTenant(otroOperador, _propietario);
            propietarioDeLaBase.DelegacionesTenant.Add(otra);
            await propietarioDeLaBase.SaveChangesAsync();
            otraDelegacion = otra.Id;
        }

        // FijarOrigen ya lo rechaza en el dominio; la base es la defensa ante una inserción que no pase por él.
        await using var contexto = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_cliente), ahora.AddDays(-1), null, ahora);
        contexto.AsignacionesOperacion.Add(acotada);
        contexto.Entry(acotada).Property(o => o.OrigenId).CurrentValue = otraDelegacion;
        contexto.Entry(acotada).Property(o => o.RevocacionesDelOrigen).CurrentValue = 0;

        var guardado = async () => await contexto.SaveChangesAsync();
        (await guardado.Should().ThrowAsync<DbUpdateException>()).Which.Should()
            .Match<DbUpdateException>(e => EsViolacionDeComprobacion(e), "la delegación de origen no es del par de la operación");
    }

    [Fact]
    public async Task Con_dos_delegaciones_activas_del_par_el_trigger_no_deduce_el_origen()
    {
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.DelegacionesTenant.Add(new DelegacionTenant(_operador, _propietario));
            await propietarioDeLaBase.SaveChangesAsync();
        }

        var acotada = await SembrarAcotadaConCarteraAsync();

        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).OrigenId
            .Should().BeNull("con dos delegaciones activas del par el origen es ambiguo y no se deduce");
    }

    [Fact]
    public async Task Una_delegacion_caducada_no_es_origen_y_quitar_la_caducidad_cuenta_como_revocacion()
    {
        await EjecutarComoPropietarioAsync(
            $"""UPDATE "DelegacionesTenant" SET "ExpiraEnUtc" = now() - interval '1 day' WHERE "Id" = '{_delegacion}'""");
        var acotada = await SembrarAcotadaConCarteraAsync();

        await using (var verificacion = ContextoPropietarioDeLaBase())
        {
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).OrigenId
                .Should().BeNull("una delegación caducada no es origen deducible");
            (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones
                .Should().Be(0, "acortar la caducidad no amplía nada: no cuenta");
        }

        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "ExpiraEnUtc" = NULL WHERE "Id" = '{_delegacion}'""");
        await using (var verificacion = ContextoPropietarioDeLaBase())
            (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones
                .Should().Be(1, "quitar la caducidad de una delegación de Operador CAE externo activa cuenta como revocación");
    }

    [Fact]
    public async Task El_contador_de_revocaciones_no_se_rebaja()
    {
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Activa" = false WHERE "Id" = '{_delegacion}'""");
        await EjecutarComoPropietarioAsync($"""UPDATE "DelegacionesTenant" SET "Revocaciones" = 0, "Activa" = true WHERE "Id" = '{_delegacion}'""");

        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Revocaciones
            .Should().Be(1, "el trigger lo calcula desde la fila anterior: ningún UPDATE lo rebaja");
    }

    // ── Medida ───────────────────────────────────────────────────────────────

    /// <summary>Operaciones cuyas carteras autorizan al Operador CAE externo, leídas como <c>cae_app_runtime</c>.</summary>
    private async Task<List<Guid>> CarterasAutorizadasDelOperadorAsync()
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

    /// <summary>La vía sin cartera, leída como <c>cae_app_runtime</c> en el Tenant propietario.</summary>
    private async Task<List<Guid>> OperacionesDelTenantEnteroAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        FijarSesion(_administradorPropietario, Roles.Administrador);
        using (AmbitoTenantExplicito.Establecer(_propietario))
            return await TenantsBeneficiariosAutorizados.OperacionesDelTenantEntero(contexto, contexto, DateTime.UtcNow)
                .Where(o => o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador)
                .Select(o => o.Id)
                .ToListAsync();
    }

    private async Task<Guid> OperacionDelTenantEnteroAsync()
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        return await verificacion.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound && o.AmbitoRelacionClienteId == null
                        && o.Estado == EstadoAsignacion.Vigente)
            .Select(o => o.Id)
            .SingleAsync();
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Una operación Outbound acotada al Cliente empresarial con la cartera de la Gestora CAE, sembrada sin origen
    /// como la escribiría la ranura anterior. Devuelve el Id de la operación.
    /// </summary>
    private async Task<Guid> SembrarAcotadaConCarteraAsync(Guid? clienteEmpresarial = null, Guid? usuario = null)
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(clienteEmpresarial ?? _cliente), ahora.AddDays(-1), null, ahora);
        propietarioDeLaBase.AsignacionesOperacion.Add(acotada);
        await propietarioDeLaBase.SaveChangesAsync();
        propietarioDeLaBase.AsignacionesCartera.Add(AsignacionCartera.Externa(
            acotada, usuario ?? _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
        await propietarioDeLaBase.SaveChangesAsync();
        return acotada.Id;
    }

    /// <summary><c>true</c> si la base rechaza, con <c>check_violation</c>, una acotada con ese origen explícito.</summary>
    private async Task<bool> InsertarAcotadaConOrigenAsync(DelegacionTenant origen)
    {
        await using var contexto = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_cliente), ahora.AddDays(-1), null, ahora);
        acotada.FijarOrigen(origen);
        contexto.AsignacionesOperacion.Add(acotada);
        try
        {
            await contexto.SaveChangesAsync();
            return false;
        }
        catch (DbUpdateException e) when (EsViolacionDeComprobacion(e))
        {
            return true;
        }
    }

    private static bool EsViolacionDeComprobacion(Exception e)
    {
        for (var actual = e; actual is not null; actual = actual.InnerException)
            if (actual is PostgresException { SqlState: PostgresErrorCodes.CheckViolation })
                return true;
        return false;
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
            Autorizacion(servicios), usuarioActual, contexto, new TransaccionDeComando(contexto),
            new AsignacionAutomaticaInerte(), tenant);

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(new CrearDelegacionTenantCommand(_operador, _propietario), CancellationToken.None);
    }

    /// <summary>
    /// Revoca por el comando real. La DelegacionTenant de un Operador CAE externo la revoca su equipo, desde su propia
    /// casa (<c>EquipoDelOperadorCaeExterno</c>), no el Administrador del Tenant propietario; un Administrador del
    /// Operador CAE externo no necesita cartera para hacerlo.
    /// </summary>
    private async Task<Result> RevocarAsync()
    {
        var administradorOperador = await SembrarUsuarioAsync("admin-operador", _operador, Roles.Administrador);
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(administradorOperador, Roles.Administrador);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, tenant,
            Directorio(servicios, contexto, tenant, usuarioActual), contexto,
            new CatalogoIncorporacionCartera(contexto, usuarioActual));

        using (AmbitoTenantExplicito.Establecer(_operador))
            return await handler.Handle(new DesactivarDelegacionTenantCommand(_delegacion), CancellationToken.None);
    }

    private async Task<Result> ReactivarAsync()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(_administradorPropietario, Roles.Administrador);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new ReactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, contexto,
            new TransaccionDeComando(contexto), tenant, Autorizacion(servicios));

        using (AmbitoTenantExplicito.Establecer(_propietario))
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

    /// <param name="conContrasena">Sin contraseña (ni login externo), la cuenta queda pendiente de activación.</param>
    private async Task<ApplicationUser> SembrarUsuarioAsync(string alias, Guid tenant, string rol, bool conContrasena = true)
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
            var alta = conContrasena ? await userManager.CreateAsync(usuario, Contrasena) : await userManager.CreateAsync(usuario);
            alta.Succeeded.Should().BeTrue();
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
