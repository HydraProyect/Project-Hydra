using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Hallazgo N-4 de Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md: la delegación era create-only —
/// los repositorios no exponían borrado ni desactivación, y
/// <c>DelegacionTenant.Desactivar()</c>/<c>Reactivar()</c> eran código muerto.
///
/// <para>
/// Lo que estos tests fijan, además de que revocar y reactivar funcionen, es <b>quién</b>
/// puede hacerlo con la Asignación de Operación de un Operador CAE externo. Decisión de
/// producto del 2026-10-26: «Solo el Gestor CAE del Operador CAE externo revoca y reactiva
/// la Delegación». El Administrador del Tenant propietario no; tampoco un Gestor CAE de otro
/// Operador CAE, ni el Administrador, la Dirección CAE o el Coordinador CAE del propio
/// Operador CAE externo, ni una sesión sin rol de negocio (Sesión Privilegiada de plataforma).
/// </para>
///
/// <para>
/// Retirar a una persona concreta (<c>RevocarAsignacionOperadorDelegadoCommand</c>) no entra
/// en la decisión y conserva su criterio: las dos partes. Tampoco la de Soporte TALVEG.
/// </para>
/// </summary>
public class RevocacionDelegacionTests
{
    private static readonly Guid OperadorCae = Guid.NewGuid();
    private static readonly Guid TenantPropietario = Guid.NewGuid();
    private static readonly Guid GestorCae = Guid.NewGuid();

    private readonly DirectorioRolesEnOrigen _cuentas = new();

    public RevocacionDelegacionTests()
    {
        _cuentas.Asignar(GestorCae, OperadorCae, "GestorCae");
    }

    private static CurrentUserServiceFalso ElGestorCae() => new(GestorCae, "GestorCae", OperadorCae);

    // ── Revocar ───────────────────────────────────────────────────────────

    [Fact]
    public async Task El_Gestor_CAE_del_Operador_CAE_externo_revoca_desde_su_casa_y_el_cierre_se_guarda_en_el_Tenant_propietario()
    {
        // El Gestor CAE vuelve a su casa y revoca. La operación que se cierra es del Tenant
        // propietario, así que el guardado ocurre con ese Tenant activo (RLS), y el ámbito se
        // restaura al salir.
        var (delegacion, repositorio, _) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();
        var guardados = new GuardadosConTenant();
        var handler = Desactivar(repositorio, ElGestorCae(), OperadorCae, writer, guardados);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeFalse();
        writer.OperacionesCerradas.Should().ContainSingle();
        guardados.Tenants.Should().Equal(TenantPropietario);
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
    }

    [Fact]
    public async Task El_Gestor_CAE_tambien_revoca_desde_el_Workspace_operativo_derivado_del_Tenant_propietario()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(repositorio, ElGestorCae(), TenantPropietario, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeFalse();
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_no_revoca()
    {
        var administrador = Guid.NewGuid();
        _cuentas.Asignar(administrador, TenantPropietario, "Administrador");
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(administrador, "Administrador", TenantPropietario),
            TenantPropietario, writer, unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        // "No encontrada", no "no autorizado": confirmar la existencia de la fila ya revelaría
        // qué Operadores CAE externos operan sobre qué Tenants propietarios.
        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
        writer.OperacionesCerradas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_otro_Operador_CAE_no_revoca()
    {
        var otroOperador = Guid.NewGuid();
        var otroGestor = Guid.NewGuid();
        _cuentas.Asignar(otroGestor, otroOperador, "GestorCae");
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(otroGestor, "GestorCae", otroOperador), otroOperador,
            unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task Otro_rol_del_propio_Operador_CAE_externo_no_revoca(string rol)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada", $"{rol} no es Gestor CAE");
        delegacion.Activa.Should().BeTrue();
    }

    [Fact]
    public async Task Un_Gestor_CAE_con_la_cuenta_desactivada_no_revoca()
    {
        _cuentas.Desactivar(GestorCae);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_rol_de_negocio_en_la_sesion_no_se_revoca_nada()
    {
        // Sesión Privilegiada de plataforma o rol retirado: Soporte TALVEG no es Gestor CAE.
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(GestorCae, rol: null, tenantOrigenId: OperadorCae), OperadorCae,
            unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_tenant_de_origen_no_se_revoca_nada()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(GestorCae, "GestorCae"), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Desactivar_desde_un_contexto_ajeno_o_sin_Tenant_no_modifica_la_delegacion(bool sinTenant)
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Desactivar(repositorio, ElGestorCae(), sinTenant ? null : Guid.NewGuid(), writer, unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        resultado.Error.Mensaje.Should().Contain("vuelve a tu organización").And.NotContain("Tenant");
        delegacion.Activa.Should().BeTrue();
        writer.OperacionesCerradas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Revocar_dos_veces_no_es_silencioso()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Desactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.YaInactiva");
    }

    /// <summary>
    /// La decisión del 2026-10-26 habla del Operador CAE externo. El acceso de Soporte TALVEG
    /// (plano 3) conserva su criterio: lo revoca cualquiera de las dos partes.
    /// </summary>
    [Fact]
    public async Task Una_delegacion_de_Soporte_conserva_su_criterio_y_la_revoca_el_Tenant_propietario()
    {
        var delegacion = DelegacionTenant.ParaSoporte(OperadorCae, TenantPropietario);
        delegacion.ActivarParaSoporte("Incidencia abierta por el Tenant propietario", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador", TenantPropietario), TenantPropietario);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeFalse();
    }

    // ── Reactivar ─────────────────────────────────────────────────────────

    [Fact]
    public async Task El_Gestor_CAE_reactiva_desde_su_casa_con_la_transaccion_abierta_en_el_Tenant_propietario()
    {
        // La transacción tiene que abrirse YA en el ámbito del Tenant propietario: dentro de
        // ella las lecturas (otro Operador CAE externo vigente, operación, carteras, principal)
        // se hacen con el Tenant firmado al abrirla, y desde la casa del Operador CAE externo
        // RLS no las dejaría ver.
        using var contextoAnterior = AmbitoTenantExplicito.Establecer(Guid.NewGuid());
        var anterior = AmbitoTenantExplicito.TenantIdActual;
        var (delegacion, repositorio, _) = Preparar();
        delegacion.Desactivar();
        var guardados = new GuardadosConTenant();
        var transaccion = new TransaccionQueAnotaElAmbito();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Reactivar(repositorio, ElGestorCae(), OperadorCae, writer, guardados, transaccion: transaccion);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
        writer.OperacionesAbiertas.Should().ContainSingle();
        transaccion.Ambitos.Should().Equal(TenantPropietario);
        guardados.Tenants.Should().Equal(TenantPropietario);
        AmbitoTenantExplicito.TenantIdActual.Should().Be(anterior);
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_no_reactiva()
    {
        var administrador = Guid.NewGuid();
        _cuentas.Asignar(administrador, TenantPropietario, "Administrador");
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(administrador, "Administrador", TenantPropietario),
            TenantPropietario, writer, unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        writer.OperacionesAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_CAE_de_otro_Operador_CAE_no_reactiva()
    {
        var otroOperador = Guid.NewGuid();
        var otroGestor = Guid.NewGuid();
        _cuentas.Asignar(otroGestor, otroOperador, "GestorCae");
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(otroGestor, "GestorCae", otroOperador), otroOperador,
            unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task Otro_rol_del_propio_Operador_CAE_externo_no_reactiva(string rol)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada", $"{rol} no es Gestor CAE");
        delegacion.Activa.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_reactiva_nada()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(repositorio, new CurrentUserServiceFalso(), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.SinUsuario");
        delegacion.Activa.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reactivar_desde_contexto_ajeno_o_sin_Tenant_no_escribe(bool sinTenant)
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Reactivar(repositorio, ElGestorCae(), sinTenant ? null : Guid.NewGuid(), writer, unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado");
        resultado.Error.Mensaje.Should().Be("Para reactivar el acceso, vuelve a tu organización.");
        delegacion.Activa.Should().BeFalse();
        writer.OperacionesAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Sin esto, la diferencia entre "ya estaba activa" y "no encontrada" le contaría a un
    /// tercero si un acceso concreto está revocado ahora mismo.
    /// </summary>
    [Fact]
    public async Task La_autorizacion_de_reactivar_corre_antes_que_el_estado_de_la_delegacion()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();   // nace activa
        var ajeno = Guid.NewGuid();
        _cuentas.Asignar(ajeno, TenantPropietario, "Administrador");

        var sinAutoridad = await Reactivar(
                repositorio, new CurrentUserServiceFalso(ajeno, "Administrador", TenantPropietario), TenantPropietario,
                unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        var conAutoridad = await Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        sinAutoridad.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        conAutoridad.Error.Codigo.Should().Be("DelegacionTenant.YaActiva");
    }

    /// <summary>
    /// Hallazgo de Codex (Alto), ronda 2 sobre el incremento 1b: una <c>DelegacionTenant</c>
    /// heredada con <c>TenantClienteId</c> = Tenant de plataforma no se reactiva, aunque quien
    /// pide sea el Gestor CAE del Operador CAE externo (ADR-011 § 1).
    /// </summary>
    [Fact]
    public async Task No_se_puede_reactivar_una_delegacion_heredada_con_el_tenant_de_plataforma_como_cliente()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        var delegacion = new DelegacionTenant(OperadorCae, plataforma.Id);
        delegacion.Desactivar();
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(plataforma);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork, tenants: tenants);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Hallazgo de Codex (P1) al integrar <c>origin/main</c> en el incremento 1b: si el Tenant
    /// propietario autorizó a otro Operador CAE externo entre revocar y reactivar, se rechaza
    /// antes de escribir.
    /// </summary>
    [Fact]
    public async Task No_se_reactiva_un_operador_externo_si_el_propietario_ya_autorizo_a_otro()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaDelegacionesTenant.AddRange([delegacion, new DelegacionTenant(Guid.NewGuid(), TenantPropietario)]);
        var handler = Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork, tenants: tenants);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.OtroOperadorVigente");
        resultado.Error.Mensaje.Should().NotContain("Tenant");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_delegacion_de_Soporte_no_se_reactiva_por_aqui()
    {
        var delegacion = DelegacionTenant.ParaSoporte(OperadorCae, TenantPropietario);
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── Lo que la pantalla pregunta antes de pintar el botón ──────────────

    /// <summary>
    /// Delegaciones.razor pinta «Revocar acceso» y «Reactivar» solo a quien puede usarlos:
    /// <c>PuedeRevocarQuery</c> y <c>PuedeReactivarQuery</c> tienen que devolver EXACTAMENTE lo
    /// que el comando aplicaría, y además el estado (solo se revoca lo activo y se reactiva lo
    /// revocado).
    /// </summary>
    [Fact]
    public async Task Las_consultas_de_la_pantalla_autorizan_exactamente_a_quien_el_comando_autoriza()
    {
        var administrador = Guid.NewGuid();
        _cuentas.Asignar(administrador, TenantPropietario, "Administrador");
        var activa = new DelegacionTenant(OperadorCae, TenantPropietario);
        var revocada = new DelegacionTenant(OperadorCae, Guid.NewGuid());
        revocada.Desactivar();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaDelegacionesTenant.AddRange([activa, revocada]);

        async Task<(bool RevocaActiva, bool RevocaRevocada, bool ReactivaRevocada, bool ReactivaActiva)> Preguntar(
            CurrentUserServiceFalso quien)
        {
            var repositorio = new DelegacionTenantRepositorioFalso();
            var desactivar = Desactivar(repositorio, quien, OperadorCae, tenants: tenants);
            var reactivar = Reactivar(repositorio, quien, OperadorCae, tenants: tenants);
            return (await desactivar.Handle(new PuedeRevocarQuery(activa.Id), CancellationToken.None),
                await desactivar.Handle(new PuedeRevocarQuery(revocada.Id), CancellationToken.None),
                await reactivar.Handle(new PuedeReactivarQuery(revocada.Id), CancellationToken.None),
                await reactivar.Handle(new PuedeReactivarQuery(activa.Id), CancellationToken.None));
        }

        (await Preguntar(ElGestorCae())).Should().Be((true, false, true, false));
        (await Preguntar(new CurrentUserServiceFalso(administrador, "Administrador", TenantPropietario)))
            .Should().Be((false, false, false, false), "el Administrador del Tenant propietario no revoca ni reactiva");
        (await Preguntar(new CurrentUserServiceFalso()))
            .Should().Be((false, false, false, false), "fallo cerrado: sin usuario no hay a quién autorizar");
    }

    // ── Retirar a una persona: fuera de la decisión, criterio sin cambios ──

    [Fact]
    public async Task Se_puede_retirar_un_operador_sin_revocar_la_delegacion_entera()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: OperadorCae), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().BeEmpty();
        delegacion.Activa.Should().BeTrue("retirar a una persona no corta el acceso de todo el equipo");
    }

    [Fact]
    public async Task Un_tercero_no_puede_retirar_operadores_de_una_delegacion_ajena()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: Guid.NewGuid()), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().ContainSingle();
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private DesactivarDelegacionTenantCommandHandler Desactivar(
        DelegacionTenantRepositorioFalso repositorio, CurrentUserServiceFalso quien, Guid? tenantActivo,
        AsignacionesOperativasWriterFalso? writer = null, IUnitOfWork? unitOfWork = null,
        TenantsQueryContextFalso? tenants = null) =>
        new(repositorio, quien, writer ?? new AsignacionesOperativasWriterFalso(), unitOfWork ?? new UnitOfWorkFalso(),
            new TenantActualFijo(tenantActivo), _cuentas, tenants ?? new TenantsQueryContextFalso());

    private ReactivarDelegacionTenantCommandHandler Reactivar(
        DelegacionTenantRepositorioFalso repositorio, CurrentUserServiceFalso quien, Guid? tenantActivo,
        AsignacionesOperativasWriterFalso? writer = null, IUnitOfWork? unitOfWork = null,
        TenantsQueryContextFalso? tenants = null, ITransaccionDeComando? transaccion = null) =>
        new(repositorio, quien, writer ?? new AsignacionesOperativasWriterFalso(), unitOfWork ?? new UnitOfWorkFalso(),
            tenants ?? new TenantsQueryContextFalso(), transaccion ?? new TransaccionDeComandoFalsa(),
            new CatalogoIncorporacionCarteraFalso(), new DirectorioDestinosCarteraFalso(null), _cuentas,
            new BloqueoCarteraUsuarioFalso(), new TenantActualFijo(tenantActivo));

    private static (DelegacionTenant, DelegacionTenantRepositorioFalso, UnitOfWorkFalso) Preparar()
    {
        var delegacion = new DelegacionTenant(OperadorCae, TenantPropietario);
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);

        return (delegacion, repositorio, new UnitOfWorkFalso());
    }

    private sealed class GuardadosConTenant : IUnitOfWork
    {
        public List<Guid?> Tenants { get; } = [];
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Tenants.Add(AmbitoTenantExplicito.TenantIdActual);
            return Task.FromResult(1);
        }
    }

    private sealed class TransaccionQueAnotaElAmbito : ITransaccionDeComando
    {
        public List<Guid?> Ambitos { get; } = [];

        public Task<Result> EjecutarAsync(
            Func<CancellationToken, Task<Result>> operacion, CancellationToken cancellationToken = default)
        {
            Ambitos.Add(AmbitoTenantExplicito.TenantIdActual);
            return operacion(cancellationToken);
        }
    }
}
