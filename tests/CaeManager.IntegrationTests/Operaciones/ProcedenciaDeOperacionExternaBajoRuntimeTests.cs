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
/// Procedencia de la Asignación de Operación Outbound externa (hallazgos altos de Codex en la PR #2223; opción A
/// de Chris del 2026-11-14) contra Postgres real: los triggers de la migración
/// <c>AnadeProcedenciaDeOperacionExterna</c> y el predicado <c>AmparoDeDelegacionDeOrigen</c> de los lectores.
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
        operaciones.Should().OnlyContain(o => o.DelegacionTenantOrigenId == _delegacion && o.RevocacionesDelegacionOrigen == 0,
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
            (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada)).DelegacionTenantOrigenId
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
            $"""UPDATE "AsignacionesOperacion" SET "RevocacionesDelegacionOrigen" = 1 WHERE "Id" = '{acotada}'""");
        (await resellar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation,
            "volver a sellar el contador repondría la operación superviviente");

        var reorigen = () => EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "DelegacionTenantOrigenId" = NULL WHERE "Id" = '{acotada}'""");
        (await reorigen.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        // Cerrarla sí se puede: el trigger solo protege el origen.
        await EjecutarComoPropietarioAsync(
            $"""UPDATE "AsignacionesOperacion" SET "Estado" = 'Cerrada' WHERE "Id" = '{acotada}'""");
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Una operación Outbound acotada al Cliente empresarial con la cartera de la Gestora CAE, sembrada sin origen
    /// como la escribiría la ranura anterior. Devuelve el Id de la operación.
    /// </summary>
    private async Task<Guid> SembrarAcotadaConCarteraAsync()
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_cliente), ahora.AddDays(-1), null, ahora);
        propietarioDeLaBase.AsignacionesOperacion.Add(acotada);
        await propietarioDeLaBase.SaveChangesAsync();
        propietarioDeLaBase.AsignacionesCartera.Add(AsignacionCartera.Externa(
            acotada, _gestoraOperador.Id, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
        await propietarioDeLaBase.SaveChangesAsync();
        return acotada.Id;
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
            usuarioActual, contexto, new TransaccionDeComando(contexto), new AsignacionAutomaticaInerte(), tenant);

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
