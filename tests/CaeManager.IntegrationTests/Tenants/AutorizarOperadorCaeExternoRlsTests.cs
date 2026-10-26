using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Queries.AutorizarOperadorCaeExterno;
using CaeManager.Domain.Common;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
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

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Incremento 1b del aprovisionamiento (opción 1′): el Administrador de un Tenant
/// propietario YA existente autoriza a un Operador CAE externo con
/// <see cref="CrearDelegacionTenantCommand"/>. Contra Postgres real, con el cableado
/// de producción del <see cref="ArnesDeArranqueRuntime"/> (rol <c>cae_app_runtime</c>,
/// los cuatro interceptores, RLS sin debilitar) y con Identity real.
///
/// <para>
/// Cada persona actúa desde SU propio workspace activo (<c>app.tenant_id</c> = el
/// Tenant de su cuenta), que es como llega en producción. Así, un «no» no puede
/// venir de que RLS le esconda su propia fila de usuario: viene del predicado de
/// <see cref="AutorizacionDelegacionPorAdministradorDelCliente"/>, que es lo que
/// se quiere probar. La persona que ejecuta la delegación con éxito es la misma
/// cuyo Actor real queda en la auditoría: el clic del Administrador es la
/// instrucción documentada (RGPD art. 28, decisión del propietario 2026-09-22).
/// </para>
/// </summary>
public class AutorizarOperadorCaeExternoRlsTests : IAsyncLifetime
{
    private const string Contrasena = "Arnes#2026Seguro";

    private readonly ActorMutable _actor = new();
    // El usuario de la sesión, como lo fija CurrentUserService en producción:
    // sin él, la cuenta del Administrador no sería visible desde otro workspace
    // (RLS de AspNetUsers, P1-M1) y la autorización fallaría antes de llegar a
    // la política que estos tests miden.
    private readonly CurrentUserServiceMutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _propietario;
    private Guid _operador;
    private Guid _otroPropietario;
    private Guid _plataforma;

    private ApplicationUser _administradorPropietario = null!;
    private ApplicationUser _gestorPropietario = null!;
    private ApplicationUser _administradorOtroTenant = null!;
    private ApplicationUser _actorPlataforma = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, actorAuditoriaPersonalizado: _actor, currentUserServicePersonalizado: _sesion);

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            _propietario = await SembrarTenantAsync(propietarioDeLaBase, "Refrielectric", PerfilVocabularioTenant.ClienteDirecto);
            _operador = await SembrarTenantAsync(propietarioDeLaBase, "ArcoSPA", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);
            _otroPropietario = await SembrarTenantAsync(propietarioDeLaBase, "Laboratorios Dexter", PerfilVocabularioTenant.ClienteDirecto);
            _plataforma = (await propietarioDeLaBase.Tenants.SingleAsync(t => t.EsPlataforma)).Id;
        }

        _administradorPropietario = await SembrarUsuarioAsync("admin-refrielectric", _propietario, Roles.Administrador);
        _gestorPropietario = await SembrarUsuarioAsync("gestor-refrielectric", _propietario, Roles.GestorCae);
        _administradorOtroTenant = await SembrarUsuarioAsync("admin-dexter", _otroPropietario, Roles.Administrador);
        // El Actor de Plataforma TALVEG con TODO lo que podría confundirse con
        // autoridad: rol Administrador en su Tenant y concesión AdminPlataforma
        // global y vigente. Ninguna de las dos le deja ejecutar la delegación.
        _actorPlataforma = await SembrarUsuarioAsync("actor-plataforma", _plataforma, Roles.Administrador);
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
                _actorPlataforma.Id, vigenciaDesde: DateTime.UtcNow.AddMinutes(-5), vigenciaHasta: null));
            await propietarioDeLaBase.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    // ── Autorización real (Identity + base), persona por persona ───────────

    [Fact]
    public async Task El_alta_autorizada_guarda_la_operacion_en_el_Tenant_propietario_nuevo_bajo_RLS()
    {
        using var scope = _arnes.Servicios.CreateScope();
        var servicios = scope.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuario = new CurrentUserServiceFalso(_actorPlataforma.Id, tenantOrigenId: _plataforma);
        _actor.Actual = _actorPlataforma.Id;
        _sesion.UsuarioId = _actorPlataforma.Id;
        _sesion.TenantOrigenId = _plataforma;
        using var tenantOrigen = AmbitoTenantExplicito.Establecer(_plataforma);
        var handler = new CaeManager.Application.Tenants.Commands.CrearClienteDelegante.CrearClienteDeleganteCommandHandler(
            new TenantRepository(contexto), new DelegacionTenantRepository(contexto),
            new AsignacionOperadorDelegadoRepository(contexto), new ParametroSistemaRepository(contexto),
            new CaeManager.Infrastructure.Plataforma.AutorizacionAdminPlataformaPorConcesion(contexto), usuario,
            new AsignacionesOperativasWriter(contexto, servicios.GetRequiredService<ITenantActual>(), usuario), contexto, new TransaccionDeComando(contexto));

        var resultado = await handler.Handle(
            new CaeManager.Application.Tenants.Commands.CrearClienteDelegante.CrearClienteDeleganteCommand("Tenant propietario nuevo"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        AmbitoTenantExplicito.TenantIdActual.Should().Be(_plataforma);
        using var propietario = AmbitoTenantExplicito.Establecer(resultado.Valor);
        var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => !o.EsRaiz && o.PropietarioTenantId == resultado.Valor);
        operacion.OperadorTenantId.Should().Be(_plataforma);
        (await contexto.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == resultado.Valor && d.TenantConsultoraId == _plataforma))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Solo_el_administrador_del_tenant_propietario_puede_autorizar_al_operador()
    {
        (await PuedeAutorizarAsync(_administradorPropietario)).Should().BeTrue();
        (await PuedeAutorizarAsync(_gestorPropietario)).Should().BeFalse(
            "un Gestor CAE del Tenant propietario está en el Tenant correcto sin autoridad para conceder acceso");
        (await PuedeAutorizarAsync(_administradorOtroTenant)).Should().BeFalse(
            "el rol Administrador de otro Tenant no da autoridad sobre este");
        (await PuedeAutorizarAsync(_actorPlataforma)).Should().BeFalse(
            "TALVEG nunca inicia ni ejecuta la delegación (ADR-004 § 11.1)");
    }

    [Fact]
    public async Task El_actor_de_plataforma_tampoco_puede_desde_dentro_del_workspace_del_propietario()
    {
        // Workspace activo = Tenant propietario (p. ej. una ventana de soporte):
        // una coordenada de contexto no es autoridad.
        (await PuedeAutorizarAsync(_actorPlataforma, workspaceActivo: _propietario)).Should().BeFalse();
    }

    [Fact]
    public async Task Un_administrador_desactivado_no_puede_autorizar()
    {
        // Hallazgo de la revisión puente del incremento 1b: sin esta guarda,
        // una cuenta desactivada por /usuarios seguía teniendo rol Administrador
        // y tenant correcto, y superaba la autorización mientras su cookie o
        // token de sesión ya emitidos siguieran vivos.
        using (var ambito = _arnes.Servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            // En el Tenant de la cuenta: AspNetUsers tiene RLS (P1-M1).
            using var tenantDeLaCuenta = AmbitoTenantExplicito.Establecer(_propietario);
            var usuario = (await userManager.FindByIdAsync(_administradorPropietario.Id.ToString()))!;
            usuario.Desactivar();
            (await userManager.UpdateAsync(usuario)).Succeeded.Should().BeTrue();
        }

        (await PuedeAutorizarAsync(_administradorPropietario)).Should().BeFalse();
    }

    // ── El comando completo bajo RLS ─────────────────────────────────────────

    [Fact]
    public async Task El_administrador_autoriza_y_queda_la_delegacion_la_operacion_y_su_actor_real()
    {
        var resultado = await EjecutarAsync(_administradorPropietario, _operador, _propietario);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : string.Empty);

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var vinculo = await propietarioDeLaBase.DelegacionesTenant.SingleAsync(d => d.Id == resultado.Valor);
        vinculo.TenantConsultoraId.Should().Be(_operador);
        vinculo.TenantClienteId.Should().Be(_propietario);
        vinculo.Proposito.Should().Be(PropositoDelegacion.OperadorExterno);

        (await propietarioDeLaBase.AsignacionesOperacion.CountAsync(o =>
                !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                && o.Estado == EstadoAsignacion.Vigente))
            .Should().Be(1);

        // Nadie queda como Gestor CAE: eso lo decide después el Operador con su cartera.
        (await propietarioDeLaBase.AsignacionesOperadorDelegado.AnyAsync(a => a.DelegacionTenantId == vinculo.Id))
            .Should().BeFalse();

        (await propietarioDeLaBase.RegistrosAuditoria.IgnoreQueryFilters()
                .AnyAsync(r => r.EntidadTipo == nameof(DelegacionTenant) && r.EntidadId == vinculo.Id
                               && r.ActorRealUsuarioId == _administradorPropietario.Id))
            .Should().BeTrue("el consentimiento es el clic del Administrador, y su Actor real queda registrado");
    }

    [Fact]
    public async Task Ni_el_gestor_ni_otro_tenant_ni_la_plataforma_escriben_nada()
    {
        (await EjecutarAsync(_gestorPropietario, _operador, _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");
        (await EjecutarAsync(_administradorOtroTenant, _operador, _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");
        (await EjecutarAsync(_actorPlataforma, _operador, _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");
        (await EjecutarAsync(_actorPlataforma, _operador, _propietario, workspaceActivo: _propietario)).Error.Codigo
            .Should().Be("DelegacionTenant.NoAutorizado");

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        (await propietarioDeLaBase.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == _propietario)).Should().BeFalse();
        (await propietarioDeLaBase.AsignacionesOperacion.AnyAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Desde_el_workspace_de_otro_tenant_se_rechaza_antes_de_escribir()
    {
        var resultado = await EjecutarAsync(
            _administradorPropietario, _operador, _propietario, workspaceActivo: _otroPropietario);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        resultado.Error.Mensaje.Should().Contain("entra en la organización que delega",
            "es la guarda de contexto la que rechaza, no la falta de autoridad");

        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        (await propietarioDeLaBase.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == _propietario))
            .Should().BeFalse("el contexto ajeno se rechaza antes de crear el vínculo");
    }

    // ── Revocar y reactivar: solo el Gestor CAE del Operador CAE externo ───
    // Decisión de producto del 2026-10-26: «Solo el Gestor CAE del Operador CAE externo revoca y
    // reactiva la Delegación». Cada persona actúa desde su propio Workspace activo, como en
    // producción, y la regla se lee en Identity real (GestorCaeDelOperadorCaeExterno).

    [Fact]
    public async Task El_Gestor_CAE_del_Operador_CAE_externo_revoca_y_reactiva_desde_su_casa_y_la_operacion_y_su_cartera_se_reabren_bajo_RLS()
    {
        // Flujo de FlujoAltaYRevocacionDelegacionTests, ahora bajo cae_app_runtime: el
        // Gestor CAE del Operador CAE externo revoca con su propio Tenant activo. La
        // Asignación de Operación es del Tenant propietario y el WITH CHECK exige ese
        // Tenant activo para cerrarla; el comando guarda en su ámbito tras decidir la
        // autoridad por el Tenant de origen.
        var alta = await EjecutarAsync(_administradorPropietario, _operador, _propietario);
        alta.EsExitoso.Should().BeTrue(alta.EsFallido ? alta.Error.Codigo : string.Empty);
        var delegacionId = alta.Valor;
        var gestorOperador = await SembrarUsuarioAsync("gestor-operador", _operador, Roles.GestorCae);
        await SembrarCarteraAsync(delegacionId, gestorOperador.Id);
        (await EstadoAsync(delegacionId, gestorOperador.Id)).Should().Be((true, 1, 1, 1), "control: acceso vigente con su cartera");

        var revocar = await RevocarAsync(gestorOperador, Roles.GestorCae, delegacionId);
        revocar.Puede.Should().BeTrue("la pantalla le pinta «Revocar acceso»");
        revocar.Resultado.EsExitoso.Should().BeTrue(revocar.Resultado.EsFallido ? revocar.Resultado.Error.Codigo : string.Empty);
        (await EstadoAsync(delegacionId, gestorOperador.Id)).Should().Be((false, 0, 1, 0),
            "la operación delegada se cierra, no se borra, y arrastra su cartera");

        var reactivar = await ReactivarAsync(gestorOperador, Roles.GestorCae, delegacionId);
        reactivar.Puede.Should().BeTrue("la pantalla le pinta «Reactivar»");
        reactivar.Resultado.EsExitoso.Should().BeTrue(reactivar.Resultado.EsFallido ? reactivar.Resultado.Error.Codigo : string.Empty);
        (await EstadoAsync(delegacionId, gestorOperador.Id)).Should().Be((true, 1, 2, 1),
            "reactivar abre una operación nueva y le repone la cartera que la revocación cerró");
    }

    [Theory]
    [InlineData("administrador-del-tenant-propietario")]
    [InlineData("gestor-cae-de-otro-operador-cae-externo")]
    [InlineData("administrador-del-operador-cae-externo")]
    public async Task Quien_no_es_Gestor_CAE_del_Operador_CAE_externo_no_revoca_ni_reactiva_y_no_escribe_bajo_RLS(string quien)
    {
        var alta = await EjecutarAsync(_administradorPropietario, _operador, _propietario);
        alta.EsExitoso.Should().BeTrue(alta.EsFallido ? alta.Error.Codigo : string.Empty);
        var delegacionId = alta.Valor;
        var gestorOperador = await SembrarUsuarioAsync("gestor-operador", _operador, Roles.GestorCae);
        await SembrarCarteraAsync(delegacionId, gestorOperador.Id);

        var (intruso, rol) = quien switch
        {
            // El Administrador del Tenant propietario, desde su casa: autorizó el acceso, pero
            // revocarlo y reactivarlo ya no es suyo.
            "administrador-del-tenant-propietario" => (_administradorPropietario, Roles.Administrador),
            // Un Gestor CAE de verdad, pero de otro Operador CAE externo.
            "gestor-cae-de-otro-operador-cae-externo" => (await SembrarUsuarioAsync(
                "gestor-otro-operador", await SembrarOtroOperadorCaeExternoAsync(), Roles.GestorCae), Roles.GestorCae),
            // Del propio Operador CAE externo, pero sin rol GestorCae.
            "administrador-del-operador-cae-externo" => (await SembrarUsuarioAsync(
                "admin-operador", _operador, Roles.Administrador), Roles.Administrador),
            _ => throw new ArgumentOutOfRangeException(nameof(quien)),
        };

        var revocar = await RevocarAsync(intruso, rol, delegacionId);
        revocar.Puede.Should().BeFalse("la pantalla no le pinta «Revocar acceso»");
        revocar.Resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        (await EstadoAsync(delegacionId, gestorOperador.Id)).Should().Be((true, 1, 1, 1), "no se escribe nada");

        // Control: el Gestor CAE del Operador CAE externo sí revoca.
        (await RevocarAsync(gestorOperador, Roles.GestorCae, delegacionId)).Resultado.EsExitoso.Should().BeTrue();

        var reactivar = await ReactivarAsync(intruso, rol, delegacionId);
        reactivar.Puede.Should().BeFalse("la pantalla no le pinta «Reactivar»");
        reactivar.Resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        (await EstadoAsync(delegacionId, gestorOperador.Id)).Should().Be((false, 0, 1, 0), "no se escribe nada");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reactivar_bajo_RLS_restaura_la_operacion_o_rechaza_el_contexto_ajeno(bool contextoAjeno)
    {
        var alta = await EjecutarAsync(_administradorPropietario, _operador, _propietario);
        alta.EsExitoso.Should().BeTrue();
        var gestorOperador = await SembrarUsuarioAsync("gestor-operador", _operador, Roles.GestorCae);
        (await RevocarAsync(gestorOperador, Roles.GestorCae, alta.Valor)).Resultado.EsExitoso.Should().BeTrue();

        // El Gestor CAE del Operador CAE externo, con autoridad, pero desde el Workspace operativo
        // derivado de un tercero: se rechaza con un error propio antes de escribir.
        var reactivar = await ReactivarAsync(
            gestorOperador, Roles.GestorCae, alta.Valor, contextoAjeno ? _otroPropietario : null);
        if (contextoAjeno)
            reactivar.Resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        else
            reactivar.Resultado.EsExitoso.Should().BeTrue(reactivar.Resultado.EsFallido ? reactivar.Resultado.Error.Codigo : string.Empty);

        var estado = await EstadoAsync(alta.Valor, gestorOperador.Id);
        estado.Activa.Should().Be(!contextoAjeno);
        estado.OperacionesVigentes.Should().Be(contextoAjeno ? 0 : 1);
        estado.Operaciones.Should().Be(contextoAjeno ? 1 : 2);
    }

    [Fact]
    public async Task El_segundo_guardado_fallido_no_deja_un_Tenant_huerfano_bajo_RLS()
    {
        using var scope = _arnes.Servicios.CreateScope();
        var servicios = scope.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuario = new CurrentUserServiceFalso(_actorPlataforma.Id, tenantOrigenId: _plataforma);
        _actor.Actual = _actorPlataforma.Id;
        _sesion.UsuarioId = _actorPlataforma.Id;
        _sesion.TenantOrigenId = _plataforma;
        using var origen = AmbitoTenantExplicito.Establecer(_plataforma);
        var guardados = new FalloEnSegundoGuardado(contexto);
        var handler = new CaeManager.Application.Tenants.Commands.CrearClienteDelegante.CrearClienteDeleganteCommandHandler(
            new TenantRepository(contexto), new DelegacionTenantRepository(contexto),
            new AsignacionOperadorDelegadoRepository(contexto), new ParametroSistemaRepository(contexto),
            new CaeManager.Infrastructure.Plataforma.AutorizacionAdminPlataformaPorConcesion(contexto), usuario,
            new AsignacionesOperativasWriter(contexto, servicios.GetRequiredService<ITenantActual>(), usuario),
            guardados, new TransaccionDeComando(contexto));
        var nombre = $"Alta fallida {Guid.NewGuid():N}";

        Func<Task> ejecutar = async () => await handler.Handle(new(nombre), CancellationToken.None);
        await ejecutar.Should().ThrowAsync<InvalidOperationException>().WithMessage("Segundo guardado fallido");

        guardados.Intentos.Should().Be(2);
        guardados.PrimerGuardado.Should().BeGreaterThan(0);
        contexto.ChangeTracker.Entries().Should().BeEmpty();
        AmbitoTenantExplicito.TenantIdActual.Should().Be(_plataforma);
        await using var verificacion = ContextoPropietarioDeLaBase();
        (await verificacion.Tenants.AnyAsync(t => t.Nombre == nombre)).Should().BeFalse();
    }

    private sealed class FalloEnSegundoGuardado(CaeManagerDbContext contexto) : IUnitOfWork
    {
        public int Intentos { get; private set; }
        public int PrimerGuardado { get; private set; }
        public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (++Intentos == 2)
                throw new InvalidOperationException("Segundo guardado fallido");
            return PrimerGuardado = await contexto.SaveChangesAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task Un_segundo_operador_con_la_operacion_completa_se_rechaza_con_mensaje_y_no_con_excepcion()
    {
        Guid segundoOperador;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
            segundoOperador = await SembrarTenantAsync(propietarioDeLaBase, "Prevención Norte", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);

        (await EjecutarAsync(_administradorPropietario, _operador, _propietario)).EsExitoso.Should().BeTrue();

        var segundo = await EjecutarAsync(_administradorPropietario, segundoOperador, _propietario);

        segundo.Error.Codigo.Should().Be("DelegacionTenant.OtroOperadorVigente");
    }

    [Fact]
    public async Task La_busqueda_bajo_RLS_resuelve_el_operador_y_nada_mas()
    {
        string nombreOperador;
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
            nombreOperador = await propietarioDeLaBase.Tenants.Where(t => t.Id == _operador).Select(t => t.Nombre).SingleAsync();

        // El nombre exacto, pero con otras mayúsculas y espacios alrededor: el buscador
        // no distingue mayúsculas; un fragmento no encuentra nada.
        var comoAdministrador = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(null, $"  {nombreOperador.ToUpperInvariant()} "));
        var porFragmento = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(null, "ArcoSPA"));
        var comoGestor = await BuscarAsync(_gestorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(_operador, null));
        var laPlataforma = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(_plataforma, null));
        var otroPropietario = await BuscarAsync(_administradorPropietario, new BuscarOperadorCaeExternoAutorizableQuery(_otroPropietario, null));

        comoAdministrador.Should().NotBeNull();
        comoAdministrador!.TenantId.Should().Be(_operador);
        porFragmento.Should().BeNull("solo el nombre exacto: nunca se enumeran Operadores CAE externos por prefijo");
        comoGestor.Should().BeNull("sin autoridad la consulta no revela ni que el Operador existe");
        laPlataforma.Should().BeNull();
        otroPropietario.Should().BeNull();
    }

    [Fact]
    public async Task El_rol_revocado_desde_otro_circuito_corta_la_autorizacion_en_un_circuito_ya_cargado()
    {
        // Revisión Codex (lote 1 del selector de Tenant, ronda 1): FindByIdAsync + IsInRoleAsync
        // reutilizaban la instancia y la afiliación rastreadas por el contexto del circuito, así que
        // una segunda decisión seguía autorizando con un rol ya revocado (C6/I7).
        var administrador = await SembrarUsuarioAsync("admin-revocable", _propietario, Roles.Administrador);

        using var ambito = _arnes.Servicios.CreateScope();
        var predicado = new AdministradorDelTenantPropietarioEnBase(
            ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());

        using (AmbitoTenantExplicito.Establecer(_propietario))
            (await predicado.EsAdministradorEnBaseAsync(administrador.Id, _propietario)).Should().BeTrue();

        using (var otroCircuito = _arnes.Servicios.CreateScope())
        using (AmbitoTenantExplicito.Establecer(_propietario))
        {
            var otroUserManager = otroCircuito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var usuario = await otroUserManager.FindByIdAsync(administrador.Id.ToString());
            (await otroUserManager.RemoveFromRoleAsync(usuario!, Roles.Administrador)).Succeeded.Should().BeTrue();
        }

        using (AmbitoTenantExplicito.Establecer(_propietario))
            (await predicado.EsAdministradorEnBaseAsync(administrador.Id, _propietario)).Should().BeFalse(
                "el rol efectivo se lee de la base en cada decisión, no del seguimiento del circuito");
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    private async Task<bool> PuedeAutorizarAsync(ApplicationUser usuario, Guid? workspaceActivo = null)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;

        using (AmbitoTenantExplicito.Establecer(workspaceActivo ?? usuario.TenantId))
            return await new AutorizacionDelegacionPorAdministradorDelCliente(userManager)
                .PuedeGestionarDelegacionesAsync(usuario.Id, _propietario);
    }

    private async Task<Result<Guid>> EjecutarAsync(
        ApplicationUser usuario, Guid operador, Guid propietario, Guid? workspaceActivo = null)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = new CurrentUserServiceFalso(usuario.Id, tenantOrigenId: usuario.TenantId);
        _actor.Actual = usuario.Id;
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;

        var handler = new CrearDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto),
            contexto,
            new AsignacionesOperativasWriter(contexto, servicios.GetRequiredService<ITenantActual>(), usuarioActual),
            new AutorizacionDelegacionPorAdministradorDelCliente(servicios.GetRequiredService<UserManager<ApplicationUser>>()),
            usuarioActual,
            contexto, new TransaccionDeComando(contexto), new AsignacionAutomaticaInerte(), servicios.GetRequiredService<ITenantActual>());

        using (AmbitoTenantExplicito.Establecer(workspaceActivo ?? usuario.TenantId))
            return await handler.Handle(new CrearDelegacionTenantCommand(operador, propietario), CancellationToken.None);
    }

    private async Task<OperadorCaeExternoAutorizableDto?> BuscarAsync(
        ApplicationUser usuario, BuscarOperadorCaeExternoAutorizableQuery consulta)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;

        var handler = new AutorizarOperadorCaeExternoQueriesHandler(
            servicios.GetRequiredService<CaeManagerDbContext>(),
            new AutorizacionDelegacionPorAdministradorDelCliente(servicios.GetRequiredService<UserManager<ApplicationUser>>()),
            new CurrentUserServiceFalso(usuario.Id, tenantOrigenId: usuario.TenantId));

        using (AmbitoTenantExplicito.Establecer(usuario.TenantId))
            return await handler.Handle(consulta, CancellationToken.None);
    }

    /// <summary>
    /// Revoca como lo haría la pantalla: primero pregunta si puede (lo que pinta «Revocar
    /// acceso») y después ejecuta el comando, los dos con la misma sesión y el mismo
    /// Workspace activo (por defecto, la casa de quien revoca).
    /// </summary>
    private async Task<(bool Puede, Result Resultado)> RevocarAsync(
        ApplicationUser usuario, string rol, Guid delegacionId, Guid? workspaceActivo = null)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = FijarSesion(usuario, rol);
        var tenant = servicios.GetRequiredService<ITenantActual>();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            new DelegacionTenantRepository(contexto), usuarioActual,
            new AsignacionesOperativasWriter(contexto, tenant, usuarioActual), contexto, tenant,
            Directorio(servicios, contexto, tenant, usuarioActual), contexto);

        using (AmbitoTenantExplicito.Establecer(workspaceActivo ?? usuario.TenantId))
        {
            var puede = await handler.Handle(new PuedeRevocarQuery(delegacionId), CancellationToken.None);
            var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacionId), CancellationToken.None);
            AmbitoTenantExplicito.TenantIdActual.Should().Be(workspaceActivo ?? usuario.TenantId, "el ámbito del propietario se restaura al salir");
            return (puede, resultado);
        }
    }

    /// <summary>Como <see cref="RevocarAsync"/>, con «Reactivar».</summary>
    private async Task<(bool Puede, Result Resultado)> ReactivarAsync(
        ApplicationUser usuario, string rol, Guid delegacionId, Guid? workspaceActivo = null)
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
            new TransaccionDeComando(contexto), new CatalogoIncorporacionCartera(contexto, usuarioActual),
            directorio, directorio, new BloqueoCarteraUsuario(contexto), tenant);

        using (AmbitoTenantExplicito.Establecer(workspaceActivo ?? usuario.TenantId))
        {
            var puede = await handler.Handle(new PuedeReactivarQuery(delegacionId), CancellationToken.None);
            var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacionId), CancellationToken.None);
            AmbitoTenantExplicito.TenantIdActual.Should().Be(workspaceActivo ?? usuario.TenantId, "el ámbito del propietario se restaura al salir");
            return (puede, resultado);
        }
    }

    private CurrentUserServiceFalso FijarSesion(ApplicationUser usuario, string rol)
    {
        _actor.Actual = usuario.Id;
        _sesion.UsuarioId = usuario.Id;
        _sesion.TenantOrigenId = usuario.TenantId;
        _sesion.Rol = rol;
        return new CurrentUserServiceFalso(usuario.Id, rol, usuario.TenantId);
    }

    private static DirectorioUsuariosTenant Directorio(
        IServiceProvider servicios, CaeManagerDbContext contexto, ITenantActual tenant, ICurrentUserService usuario) =>
        new(servicios.GetRequiredService<UserManager<ApplicationUser>>(), contexto, tenant, new PuertaAccesoDatos(), contexto, usuario);

    /// <summary>
    /// La Asignación de Cartera de un Gestor CAE sobre la operación vigente, con su fila de
    /// operador delegado: lo que la revocación cierra en cascada y la reactivación repone.
    /// </summary>
    private async Task SembrarCarteraAsync(Guid delegacionId, Guid gestorId)
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        var operacion = await propietarioDeLaBase.AsignacionesOperacion.SingleAsync(o =>
            !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
            && o.Estado == EstadoAsignacion.Vigente);
        var ahora = DateTime.UtcNow;
        propietarioDeLaBase.AsignacionesCartera.Add(AsignacionCartera.Externa(
            operacion, gestorId, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
        propietarioDeLaBase.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacionId, gestorId, Roles.GestorCae));
        await propietarioDeLaBase.SaveChangesAsync();
    }

    private async Task<Guid> SembrarOtroOperadorCaeExternoAsync()
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        return await SembrarTenantAsync(propietarioDeLaBase, "Otro Operador CAE", PerfilVocabularioTenant.Consultora, operadorCaeExterno: true);
    }

    /// <summary>
    /// Lo que la revocación y la reactivación escriben, leído como propietario de la base: si la
    /// delegación está activa, las operaciones delegadas del par (vigentes y en total) y las
    /// carteras vigentes del Gestor CAE sobre el Tenant propietario.
    /// </summary>
    private async Task<(bool Activa, int OperacionesVigentes, int Operaciones, int CarterasVigentes)> EstadoAsync(
        Guid delegacionId, Guid gestorId)
    {
        await using var verificacion = ContextoPropietarioDeLaBase();
        var operaciones = verificacion.AsignacionesOperacion.Where(o =>
            !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador);
        return (
            (await verificacion.DelegacionesTenant.SingleAsync(d => d.Id == delegacionId)).Activa,
            await operaciones.CountAsync(o => o.Estado == EstadoAsignacion.Vigente),
            await operaciones.CountAsync(),
            await verificacion.AsignacionesCartera.IgnoreQueryFilters().CountAsync(c =>
                c.PropietarioTenantId == _propietario && c.UsuarioId == gestorId && c.Estado == EstadoAsignacion.Vigente));
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
    /// Rol propietario de la base, sin interceptores: solo siembra y comprueba. Lo
    /// que se prueba corre siempre por el arnés, como <c>cae_app_runtime</c>.
    /// </summary>
    private CaeManagerDbContext ContextoPropietarioDeLaBase()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new SinTenant());
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class ActorMutable : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }
}
