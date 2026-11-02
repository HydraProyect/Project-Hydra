using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tenants;
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
/// puede hacerlo con la Asignación de Operación de un Operador CAE externo. Decisiones de
/// producto del 2026-10-26: revocan el Gestor CAE, el Coordinador CAE, la Dirección CAE y el
/// Administrador del propio Operador CAE externo, con alcance sobre ese Tenant propietario
/// (Asignación de Cartera viva propia, o un rol que alcanza toda la organización). No pueden el
/// Administrador del Tenant propietario, nadie de otro Operador CAE, una sesión limitada a
/// Consulta ni una sesión sin rol de negocio (Sesión Privilegiada de plataforma). Reactivar es
/// solo del Administrador del Tenant propietario (decisión SEG-7 del 2026-10-31), revocara quien
/// revocara: el Operador CAE externo nunca.
/// La salida a petición del Tenant propietario la hace Soporte TALVEG con Sesión Privilegiada;
/// hoy no existe ese camino para la de un Operador CAE externo (<c>CerrarAccesoSoporteCommand</c>
/// solo cierra las de Soporte) y este comando lo rechaza (ver
/// <see cref="Sin_rol_de_negocio_en_la_sesion_no_se_revoca_nada"/>): queda como seguimiento.
/// </para>
///
/// <para>
/// Retirar a una persona concreta (<c>RevocarAsignacionOperadorDelegadoCommand</c>) no es de ese
/// equipo: en una de Operador CAE externo es solo del Administrador del Tenant propietario, con
/// el mismo predicado que reactivar. La de Soporte TALVEG conserva su criterio: las dos partes.
/// </para>
/// </summary>
public class RevocacionDelegacionTests
{
    private static readonly Tenant TenantDelOperador = OperadorElegible();
    private static readonly Guid OperadorCae = TenantDelOperador.Id;
    private static readonly Guid TenantPropietario = Guid.NewGuid();
    private static readonly Guid GestorCae = Guid.NewGuid();
    private static readonly Guid Administrador = Guid.NewGuid();

    private readonly DirectorioRolesEnOrigen _cuentas = new();
    private readonly CatalogoIncorporacionCarteraFalso _catalogo = new();

    public RevocacionDelegacionTests()
    {
        _cuentas.Asignar(GestorCae, OperadorCae, "GestorCae");
        DarCartera(GestorCae, "GestorCae", TenantPropietario);
    }

    private void DarCartera(Guid usuarioId, string rol, Guid tenantPropietarioId, Guid? operadorTenantId = null) =>
        _catalogo.CarterasVivas.Add((operadorTenantId ?? OperadorCae, new CarteraVivaDeOperacion(
            Guid.NewGuid(), tenantPropietarioId, "Tenant propietario", usuarioId, rol, EsPrincipal: false, VigenciaHasta: null)));

    private static CurrentUserServiceFalso ElGestorCae() => new(GestorCae, "GestorCae", OperadorCae);

    private static CurrentUserServiceFalso ElAdministrador() => new(Administrador, "Administrador", TenantPropietario);

    private static Tenant OperadorElegible()
    {
        var tenant = new Tenant("Operador CAE externo");
        tenant.HabilitarComoOperadorCaeExterno();
        return tenant;
    }

    private static TenantsQueryContextFalso ConOperadorElegible()
    {
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(TenantDelOperador);
        return tenants;
    }

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

    /// <summary>
    /// Decisión (b) y (c) del 2026-10-26: cada rol del equipo del Operador CAE externo revoca,
    /// el Gestor CAE y el Coordinador CAE con una Asignación de Cartera viva propia sobre ese
    /// Tenant propietario, el Administrador y la Dirección CAE sin ella (alcanzan toda la
    /// organización).
    /// </summary>
    [Theory]
    [InlineData("GestorCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("DireccionCae", false)]
    [InlineData("Administrador", false)]
    public async Task Cada_rol_del_equipo_del_Operador_CAE_externo_con_alcance_revoca(string rol, bool conCartera)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        if (conCartera) DarCartera(persona, rol, TenantPropietario);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue($"{rol} es del equipo del Operador CAE externo");
        delegacion.Activa.Should().BeFalse();
    }

    /// <summary>
    /// Sin Asignación de Cartera viva propia sobre ESE Tenant propietario, el Gestor CAE y el
    /// Coordinador CAE no tienen alcance: ni sin cartera, ni con la cartera de otro Tenant
    /// propietario, ni con una cartera sobre él que sea de otro Operador CAE.
    /// </summary>
    [Theory]
    [InlineData("GestorCae", "ninguna")]
    [InlineData("CoordinadorCae", "ninguna")]
    [InlineData("GestorCae", "otro-tenant")]
    [InlineData("GestorCae", "otro-operador")]
    public async Task El_Gestor_CAE_o_el_Coordinador_CAE_sin_cartera_sobre_ese_Tenant_propietario_no_revoca(
        string rol, string cartera)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        if (cartera == "otro-tenant") DarCartera(persona, rol, Guid.NewGuid());
        if (cartera == "otro-operador") DarCartera(persona, rol, TenantPropietario, operadorTenantId: Guid.NewGuid());
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// El rol que cuenta es el de la sesión en origen, y la cuenta tiene que tenerlo en
    /// Identity: una sesión limitada a Consulta (login local) no revoca aunque la cuenta sea
    /// Gestor CAE —la pantalla no ofrece lo que AutorizacionEscrituraBehavior denegaría—, y un
    /// claim de rol que la cuenta ya no tiene tampoco.
    /// </summary>
    [Theory]
    [InlineData("Consulta", "GestorCae")]
    [InlineData("Administrador", "GestorCae")]
    public async Task El_rol_de_la_sesion_tiene_que_ser_del_equipo_y_coincidir_con_la_cuenta(
        string rolDeSesion, string rolDeCuenta)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rolDeCuenta);
        DarCartera(persona, rolDeCuenta, TenantPropietario);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = Desactivar(
            repositorio, new CurrentUserServiceFalso(persona, rolDeSesion, OperadorCae), OperadorCae,
            unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
    }

    [Fact]
    public void Los_roles_del_equipo_son_los_codigos_reales_de_Identity()
    {
        // Application no puede referenciar Infrastructure.Identity.Roles: si un código cambiara
        // allí, la regla dejaría de reconocer a ese rol en silencio.
        new[]
        {
            EquipoDelOperadorCaeExterno.Administrador, EquipoDelOperadorCaeExterno.DireccionCae,
            EquipoDelOperadorCaeExterno.CoordinadorCae, EquipoDelOperadorCaeExterno.GestorCae,
        }.Should().Equal("Administrador", "DireccionCae", "CoordinadorCae", "GestorCae");
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
    public async Task El_Administrador_del_Tenant_propietario_reactiva_con_la_transaccion_abierta_en_su_Tenant()
    {
        // La transacción tiene que abrirse YA en el ámbito del Tenant propietario: dentro de
        // ella las lecturas (otro Operador CAE externo vigente, operación, carteras, principal)
        // se hacen con el Tenant firmado al abrirla.
        using var contextoAnterior = AmbitoTenantExplicito.Establecer(Guid.NewGuid());
        var anterior = AmbitoTenantExplicito.TenantIdActual;
        var (delegacion, repositorio, _) = Preparar();
        delegacion.Desactivar();
        var guardados = new GuardadosConTenant();
        var transaccion = new TransaccionQueAnotaElAmbito();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Reactivar(repositorio, ElAdministrador(), TenantPropietario, writer, guardados, transaccion: transaccion);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
        writer.OperacionesAbiertas.Should().ContainSingle();
        transaccion.Ambitos.Should().Equal(TenantPropietario);
        guardados.Tenants.Should().Equal(TenantPropietario);
        AmbitoTenantExplicito.TenantIdActual.Should().Be(anterior);
    }

    /// <summary>
    /// SEG-7: quien sufre la revocación no la deshace, ni desde su casa ni desde el Workspace
    /// operativo derivado del Tenant propietario, ni siquiera con el rol Administrador. El doble de
    /// autorización dice que sí a propósito: lo que niega aquí es el Tenant de origen.
    /// </summary>
    [Theory]
    [InlineData("GestorCae", true)]
    [InlineData("GestorCae", false)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("CoordinadorCae", false)]
    [InlineData("DireccionCae", true)]
    [InlineData("DireccionCae", false)]
    [InlineData("Administrador", true)]
    [InlineData("Administrador", false)]
    public async Task El_equipo_del_Operador_CAE_externo_no_reactiva_nunca(string rol, bool desdeElTenantPropietario)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        DarCartera(persona, rol, TenantPropietario);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae),
            desdeElTenantPropietario ? TenantPropietario : OperadorCae, writer, unitOfWork,
            autorizacion: new AutorizacionDelegacionFalsa(autoriza: true));

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        writer.OperacionesAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Tras revocar, tampoco reactiva quien revocó: la revocación del Operador CAE externo solo la
    /// deshace el Tenant propietario.
    /// </summary>
    [Fact]
    public async Task Lo_que_revoco_el_Operador_CAE_externo_solo_lo_reactiva_el_Tenant_propietario()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        (await Desactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork)
                .Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None))
            .EsExitoso.Should().BeTrue();

        var delGestor = await Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        delGestor.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();

        var delAdministrador = await Reactivar(repositorio, ElAdministrador(), TenantPropietario, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        delAdministrador.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
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
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task Del_Tenant_propietario_solo_reactiva_el_Administrador(string rol)
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(Guid.NewGuid(), rol, TenantPropietario), TenantPropietario,
            unitOfWork: unitOfWork, autorizacion: new AutorizacionDelegacionFalsa(autoriza: true));

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Una Sesión Privilegiada no tiene rol de negocio. Soporte TALVEG no reactiva, tampoco
    /// simulando al Administrador del Tenant propietario, aunque la revocación la hiciera él a
    /// petición de ese Tenant: <c>AutorizacionEscrituraBehavior</c> lo deniega antes, y este
    /// predicado también, para que la pantalla no lo ofrezca.
    /// </summary>
    [Fact]
    public async Task Una_Sesion_Privilegiada_sin_rol_de_negocio_no_reactiva()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(Administrador, rol: null, tenantOrigenId: TenantPropietario),
            TenantPropietario, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Vigencia de la cuenta: el rol de la sesión no decide si la base ya no lo confirma.</summary>
    [Fact]
    public async Task Una_cuenta_que_ya_no_es_Administrador_en_base_no_reactiva()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, ElAdministrador(), TenantPropietario, unitOfWork: unitOfWork,
            autorizacion: new AutorizacionDelegacionFalsa(autoriza: false));

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_reactiva_nada()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(repositorio, new CurrentUserServiceFalso(), TenantPropietario, unitOfWork: unitOfWork);

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
        var handler = Reactivar(repositorio, ElAdministrador(), sinTenant ? null : Guid.NewGuid(), writer, unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
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

        var sinAutoridad = await Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        var conAutoridad = await Reactivar(repositorio, ElAdministrador(), TenantPropietario, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        sinAutoridad.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        conAutoridad.Error.Codigo.Should().Be("DelegacionTenant.YaActiva");
    }

    /// <summary>
    /// Hallazgo de Codex (Alto), ronda 2 sobre el incremento 1b: una <c>DelegacionTenant</c>
    /// heredada con <c>TenantClienteId</c> = Tenant de plataforma no se reactiva, aunque quien
    /// pide sea el Administrador inicial de TALVEG, que pasaría la autoridad de forma reflexiva
    /// (ADR-011 § 1).
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
        var tenants = ConOperadorElegible();
        tenants.ListaTenants.Add(plataforma);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(Administrador, "Administrador", plataforma.Id), plataforma.Id,
            unitOfWork: unitOfWork, tenants: tenants, autorizacion: AutorizacionDelegacionFalsa.AdministradorDe(plataforma.Id));

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Vigencia de quien recibe el acceso: la fila revocada no prueba que siga siendo un Operador
    /// CAE externo elegible, y sin este corte reactivar saltaría el requisito que exige autorizarlo.
    /// </summary>
    [Fact]
    public async Task No_se_reactiva_a_quien_ya_no_es_un_Operador_CAE_externo_elegible()
    {
        var noElegible = new Tenant("Ya no opera");
        var delegacion = new DelegacionTenant(noElegible.Id, TenantPropietario);
        delegacion.Desactivar();
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(noElegible);
        tenants.ListaDelegacionesTenant.Add(delegacion);
        var unitOfWork = new UnitOfWorkFalso();
        var handler = Reactivar(repositorio, ElAdministrador(), TenantPropietario, unitOfWork: unitOfWork, tenants: tenants);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        var puede = await handler.Handle(new PuedeReactivarQuery(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        puede.Should().BeFalse();
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Hallazgo de Codex (P1) al integrar <c>origin/main</c> en el incremento 1b: si el Tenant
    /// propietario autorizó a otro Operador CAE externo entre revocar y reactivar, se rechaza
    /// antes de escribir. Quien llega aquí es su Administrador: el vínculo es suyo.
    /// </summary>
    [Fact]
    public async Task No_se_reactiva_un_operador_externo_si_el_propietario_ya_autorizo_a_otro()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var tenants = ConOperadorElegible();
        tenants.ListaDelegacionesTenant.AddRange([delegacion, new DelegacionTenant(Guid.NewGuid(), TenantPropietario)]);
        var handler = Reactivar(repositorio, ElAdministrador(), TenantPropietario, unitOfWork: unitOfWork, tenants: tenants);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoReactivable");
        resultado.Error.Mensaje.Should().Be("No se puede reactivar este acceso: tu organización ya tiene otro Operador CAE externo con acceso vigente.");
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
        var handler = Reactivar(repositorio, ElAdministrador(), TenantPropietario, unitOfWork: unitOfWork);

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
        var activa = new DelegacionTenant(OperadorCae, TenantPropietario);
        var revocada = new DelegacionTenant(OperadorCae, TenantPropietario);
        revocada.Desactivar();
        var gestorSinCartera = Guid.NewGuid();
        _cuentas.Asignar(gestorSinCartera, OperadorCae, "GestorCae");
        var direccionDelOperador = Guid.NewGuid();
        _cuentas.Asignar(direccionDelOperador, OperadorCae, "DireccionCae");
        var tenants = ConOperadorElegible();
        tenants.ListaDelegacionesTenant.AddRange([activa, revocada]);

        async Task<(bool RevocaActiva, bool RevocaRevocada, bool ReactivaRevocada, bool ReactivaActiva)> Preguntar(
            CurrentUserServiceFalso quien, Guid? tenantActivo)
        {
            var repositorio = new DelegacionTenantRepositorioFalso();
            var desactivar = Desactivar(repositorio, quien, tenantActivo, tenants: tenants);
            var reactivar = Reactivar(repositorio, quien, tenantActivo, tenants: tenants);
            var individuales = (await desactivar.Handle(new PuedeRevocarQuery(activa.Id), CancellationToken.None),
                await desactivar.Handle(new PuedeRevocarQuery(revocada.Id), CancellationToken.None),
                await reactivar.Handle(new PuedeReactivarQuery(revocada.Id), CancellationToken.None),
                await reactivar.Handle(new PuedeReactivarQuery(activa.Id), CancellationToken.None));
            var lote = await Lote(tenants, quien, tenantActivo)
                .Handle(new PermisosAccesosDelegadosQuery([activa.Id, revocada.Id, Guid.NewGuid()]), CancellationToken.None);
            lote.Should().HaveCount(2, "una fila inexistente no concede permisos");
            (lote[activa.Id].PuedeRevocar, lote[revocada.Id].PuedeRevocar,
                lote[revocada.Id].PuedeReactivar, lote[activa.Id].PuedeReactivar).Should().Be(individuales);
            return individuales;
        }

        (await Preguntar(ElGestorCae(), OperadorCae)).Should().Be((true, false, false, false), "revoca, pero nunca reactiva");
        (await Preguntar(new CurrentUserServiceFalso(direccionDelOperador, "DireccionCae", OperadorCae), OperadorCae))
            .Should().Be((true, false, false, false), "la Dirección CAE alcanza toda la organización para revocar, no para reactivar");
        (await Preguntar(new CurrentUserServiceFalso(gestorSinCartera, "GestorCae", OperadorCae), OperadorCae))
            .Should().Be((false, false, false, false), "sin cartera sobre ese Tenant propietario no tiene alcance");
        (await Preguntar(ElAdministrador(), TenantPropietario))
            .Should().Be((false, false, true, false), "el Administrador del Tenant propietario reactiva lo revocado y no revoca");
        (await Preguntar(ElAdministrador(), Guid.NewGuid()))
            .Should().Be((false, false, false, false), "desde un Workspace operativo derivado de un tercero no se reactiva");
        (await Preguntar(new CurrentUserServiceFalso(Administrador, rol: null, tenantOrigenId: TenantPropietario), TenantPropietario))
            .Should().Be((false, false, false, false), "Soporte TALVEG con Sesión Privilegiada no tiene rol de negocio");
        (await Preguntar(new CurrentUserServiceFalso(), TenantPropietario))
            .Should().Be((false, false, false, false), "fallo cerrado: sin usuario no hay a quién autorizar");
    }

    [Fact]
    public async Task El_lote_distingue_propietario_operador_proposito_y_Tenant_de_plataforma()
    {
        var plataforma = new Tenant("Plataforma", esPlataforma: true);
        var sinCartera = new Tenant("Sin Cartera");
        var noElegible = new Tenant("Ya no opera");
        var propia = new DelegacionTenant(OperadorCae, TenantPropietario);
        var deOtroOperador = new DelegacionTenant(Guid.NewGuid(), TenantPropietario);
        var deSoporte = DelegacionTenant.ParaSoporte(OperadorCae, TenantPropietario);
        var sobrePlataforma = new DelegacionTenant(OperadorCae, plataforma.Id);
        sobrePlataforma.Desactivar();
        var fueraDeCartera = new DelegacionTenant(OperadorCae, sinCartera.Id);
        var tenants = ConOperadorElegible();
        tenants.ListaTenants.AddRange([plataforma, sinCartera, noElegible]);
        tenants.ListaDelegacionesTenant.AddRange([propia, deOtroOperador, deSoporte, sobrePlataforma, fueraDeCartera]);
        DarCartera(GestorCae, "GestorCae", plataforma.Id);
        var quien = ElGestorCae();
        var repositorio = new DelegacionTenantRepositorioFalso();
        var revocar = Desactivar(repositorio, quien, OperadorCae, tenants: tenants);
        var reactivar = Reactivar(repositorio, quien, OperadorCae, tenants: tenants);
        var ids = tenants.ListaDelegacionesTenant.Select(d => d.Id).ToArray();
        var lote = await Lote(tenants, quien, OperadorCae)
            .Handle(new PermisosAccesosDelegadosQuery([.. ids, propia.Id, Guid.NewGuid()]), CancellationToken.None);
        lote.Should().HaveCount(ids.Length);
        foreach (var id in ids)
        {
            lote[id].PuedeRevocar.Should().Be(await revocar.Handle(new PuedeRevocarQuery(id), CancellationToken.None));
            lote[id].PuedeReactivar.Should().Be(await reactivar.Handle(new PuedeReactivarQuery(id), CancellationToken.None));
        }
        lote[propia.Id].Should().Be(new PermisosAccesoDelegadoDto(true, false));
        lote[deOtroOperador.Id].Should().Be(new PermisosAccesoDelegadoDto(false, false));
        lote[deSoporte.Id].Should().Be(new PermisosAccesoDelegadoDto(false, false));
        lote[sobrePlataforma.Id].Should().Be(new PermisosAccesoDelegadoDto(false, false));
        lote[fueraDeCartera.Id].Should().Be(new PermisosAccesoDelegadoDto(false, false));
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
    }

    [Fact]
    public async Task En_el_lote_el_Administrador_solo_reactiva_lo_de_su_Tenant_y_lo_elegible()
    {
        var noElegible = new Tenant("Ya no opera");
        var plataforma = new Tenant("Plataforma", esPlataforma: true);
        var propia = new DelegacionTenant(OperadorCae, TenantPropietario);
        var deNoElegible = new DelegacionTenant(noElegible.Id, TenantPropietario);
        var ajena = new DelegacionTenant(OperadorCae, Guid.NewGuid());
        var deSoporte = DelegacionTenant.ParaSoporte(OperadorCae, TenantPropietario);
        foreach (var delegacion in new[] { propia, deNoElegible, ajena }) delegacion.Desactivar();
        var tenants = ConOperadorElegible();
        tenants.ListaTenants.AddRange([noElegible, plataforma]);
        tenants.ListaDelegacionesTenant.AddRange([propia, deNoElegible, ajena, deSoporte]);
        var quien = ElAdministrador();
        var reactivar = Reactivar(new DelegacionTenantRepositorioFalso(), quien, TenantPropietario, tenants: tenants);

        var lote = await Lote(tenants, quien, TenantPropietario)
            .Handle(new PermisosAccesosDelegadosQuery(tenants.ListaDelegacionesTenant.Select(d => d.Id).ToArray()), CancellationToken.None);

        foreach (var (id, permisos) in lote)
            permisos.PuedeReactivar.Should().Be(await reactivar.Handle(new PuedeReactivarQuery(id), CancellationToken.None));
        lote[propia.Id].PuedeReactivar.Should().BeTrue();
        lote[deNoElegible.Id].PuedeReactivar.Should().BeFalse();
        lote[ajena.Id].PuedeReactivar.Should().BeFalse();
        lote[deSoporte.Id].PuedeReactivar.Should().BeFalse();
    }

    // ── Retirar a una persona, sin revocar la delegación entera ──

    [Fact]
    public async Task Desde_el_Tenant_de_origen_no_se_retira_una_persona_de_un_Operador_CAE_externo()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var writer = new AsignacionesOperativasWriterFalso();
        var handler = RetirarPersona(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: OperadorCae), writer, unitOfWork,
            OperadorCae);

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.RequiereTenantPropietario");
        resultado.Error.Mensaje.Should().Be("Pide a la organización cuyos datos gestiona que retire a esta persona.");
        asignacionRepositorio.Asignaciones.Should().ContainSingle();
        writer.CarterasCerradas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
        delegacion.Activa.Should().BeTrue();
    }

    /// <summary>
    /// La exigencia del Tenant propietario es solo de la del Operador CAE externo, que tiene
    /// cartera que cerrar: una de Soporte TALVEG se sigue retirando desde el otro extremo.
    /// </summary>
    [Fact]
    public async Task Desde_el_otro_extremo_se_retira_una_persona_de_una_delegacion_de_Soporte()
    {
        var delegacion = DelegacionTenant.ParaSoporte(OperadorCae, TenantPropietario);
        var delegacionRepositorio = new DelegacionTenantRepositorioFalso();
        delegacionRepositorio.Agregar(delegacion);
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "Consulta");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        // Sin rol ni autoridad de Administrador: la de Soporte no pasa por ese predicado.
        var handler = RetirarPersona(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: OperadorCae), new AsignacionesOperativasWriterFalso(),
            new UnitOfWorkFalso(), OperadorCae, new AutorizacionDelegacionFalsa(autoriza: false));

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_tercero_no_puede_retirar_operadores_de_una_delegacion_ajena()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var handler = RetirarPersona(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: Guid.NewGuid()), new AsignacionesOperativasWriterFalso(), unitOfWork,
            TenantPropietario, new AutorizacionDelegacionFalsa(autoriza: true));

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().ContainSingle();
    }

    /// <summary>
    /// El Tenant de origen autoriza y abre la lectura de la fila, pero borrarla exige que el Tenant
    /// activo sea un extremo: sin esta guarda, RLS no dejaría alcanzar ninguna fila y el guardado
    /// terminaría en un DbUpdateConcurrencyException.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Desde_el_workspace_de_un_tercero_no_se_retira_un_operador_y_se_pide_volver_a_la_organizacion(bool sinTenantActivo)
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var handler = RetirarPersona(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: OperadorCae), new AsignacionesOperativasWriterFalso(), unitOfWork,
            sinTenantActivo ? null : Guid.NewGuid());

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        resultado.Error.Mensaje.Should().Be("Para retirar a esta persona, vuelve a tu organización.");
        asignacionRepositorio.Asignaciones.Should().ContainSingle();
    }

    [Fact]
    public async Task El_Administrador_del_Tenant_propietario_retira_a_una_persona_y_cierra_su_cartera()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);
        var writer = new AsignacionesOperativasWriterFalso();

        var resultado = await RetirarPersona(
                asignacionRepositorio, delegacionRepositorio, ElAdministrador(), writer, unitOfWork,
                TenantPropietario, new AutorizacionDelegacionFalsa(autoriza: true))
            .Handle(new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().BeEmpty();
        writer.CarterasCerradas.Should().ContainSingle();
        unitOfWork.VecesGuardado.Should().Be(1);
        delegacion.Activa.Should().BeTrue("retirar a una persona no corta el acceso de todo el equipo");
    }

    /// <summary>
    /// Cada caso falla por una sola condición del predicado: el rol de la sesión (Gestor CAE,
    /// Dirección CAE, Coordinador CAE y Consulta del Tenant propietario, o una Sesión Privilegiada sin
    /// rol de negocio), la cuenta leída en base (Administrador degradado o desactivado) o el Tenant de
    /// origen (una persona del Operador CAE externo dentro del Workspace operativo derivado, con el
    /// Tenant activo correcto).
    /// </summary>
    [Theory]
    [InlineData("GestorCae", true, true)]
    [InlineData("DireccionCae", true, true)]
    [InlineData("CoordinadorCae", true, true)]
    [InlineData("Consulta", true, true)]
    [InlineData(null, true, true)]
    [InlineData("Administrador", false, true)]
    [InlineData("Administrador", true, false)]
    public async Task Sin_ser_Administrador_del_Tenant_propietario_no_se_retira_a_una_persona(
        string? rol, bool cuentaEsAdministrador, bool origenEsElPropietario)
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);
        var writer = new AsignacionesOperativasWriterFalso();
        var quien = new CurrentUserServiceFalso(
            Guid.NewGuid(), rol, origenEsElPropietario ? TenantPropietario : OperadorCae);

        var resultado = await RetirarPersona(
                asignacionRepositorio, delegacionRepositorio, quien, writer, unitOfWork,
                TenantPropietario, new AutorizacionDelegacionFalsa(autoriza: cuentaEsAdministrador))
            .Handle(new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        resultado.Error.Mensaje.Should().Be(
            "Solo un Administrador de la organización propietaria de los datos puede retirar a esta persona.");
        asignacionRepositorio.Asignaciones.Should().ContainSingle();
        writer.CarterasCerradas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// La ruta de Soporte TALVEG sigue siendo de las dos partes y no mira el rol: un Gestor CAE del
    /// Tenant propietario retira a la persona de Soporte igual que antes. El extremo consultora lo
    /// prueba <see cref="Desde_el_otro_extremo_se_retira_una_persona_de_una_delegacion_de_Soporte"/>.
    /// </summary>
    [Fact]
    public async Task La_retirada_de_Soporte_desde_el_Tenant_propietario_no_exige_Administrador()
    {
        var delegacion = DelegacionTenant.ParaSoporte(OperadorCae, TenantPropietario);
        var delegacionRepositorio = new DelegacionTenantRepositorioFalso();
        delegacionRepositorio.Agregar(delegacion);
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "Consulta");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);
        var writer = new AsignacionesOperativasWriterFalso();

        var resultado = await RetirarPersona(
                asignacionRepositorio, delegacionRepositorio,
                new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae", TenantPropietario), writer,
                new UnitOfWorkFalso(), TenantPropietario, new AutorizacionDelegacionFalsa(autoriza: false))
            .Handle(new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().BeEmpty();
        writer.CarterasCerradas.Should().BeEmpty("el plano 3 no tiene cartera que cerrar");
    }

    // ── Arnés ─────────────────────────────────────────────────────────────

    private static RevocarAsignacionOperadorDelegadoCommandHandler RetirarPersona(
        AsignacionOperadorDelegadoRepositorioFalso asignaciones, DelegacionTenantRepositorioFalso delegaciones,
        CurrentUserServiceFalso quien, AsignacionesOperativasWriterFalso writer, IUnitOfWork unitOfWork,
        Guid? tenantActivo, IAutorizacionDelegacionTenant? autorizacion = null) =>
        new(asignaciones, delegaciones, quien, writer, unitOfWork, new TenantActualFijo(tenantActivo),
            autorizacion ?? new AutorizacionDelegacionFalsa(autoriza: false));

    private DesactivarDelegacionTenantCommandHandler Desactivar(
        DelegacionTenantRepositorioFalso repositorio, CurrentUserServiceFalso quien, Guid? tenantActivo,
        AsignacionesOperativasWriterFalso? writer = null, IUnitOfWork? unitOfWork = null,
        TenantsQueryContextFalso? tenants = null) =>
        new(repositorio, quien, writer ?? new AsignacionesOperativasWriterFalso(), unitOfWork ?? new UnitOfWorkFalso(),
            new TenantActualFijo(tenantActivo), _cuentas, tenants ?? new TenantsQueryContextFalso(), _catalogo);

    private ReactivarDelegacionTenantCommandHandler Reactivar(
        DelegacionTenantRepositorioFalso repositorio, CurrentUserServiceFalso quien, Guid? tenantActivo,
        AsignacionesOperativasWriterFalso? writer = null, IUnitOfWork? unitOfWork = null,
        TenantsQueryContextFalso? tenants = null, ITransaccionDeComando? transaccion = null,
        IAutorizacionDelegacionTenant? autorizacion = null) =>
        new(repositorio, quien, writer ?? new AsignacionesOperativasWriterFalso(), unitOfWork ?? new UnitOfWorkFalso(),
            tenants ?? ConOperadorElegible(), transaccion ?? new TransaccionDeComandoFalsa(),
            _catalogo, new DirectorioDestinosCarteraFalso(null), _cuentas,
            new BloqueoCarteraUsuarioFalso(), new TenantActualFijo(tenantActivo),
            autorizacion ?? AutorizacionDelegacionFalsa.AdministradorDe(TenantPropietario));

    private PermisosAccesosDelegadosQueryHandler Lote(
        TenantsQueryContextFalso tenants, CurrentUserServiceFalso quien, Guid? tenantActivo) =>
        new(tenants, quien, _cuentas, _catalogo, new TenantActualFijo(tenantActivo),
            AutorizacionDelegacionFalsa.AdministradorDe(TenantPropietario));

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
