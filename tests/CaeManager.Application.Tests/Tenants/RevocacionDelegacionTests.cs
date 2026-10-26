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
/// producto del 2026-10-26: revocan y reactivan el Gestor CAE, el Coordinador CAE, la Dirección
/// CAE y el Administrador del propio Operador CAE externo, con alcance sobre ese Tenant
/// propietario (Asignación de Cartera viva propia, o un rol que alcanza toda la organización).
/// No pueden el Administrador del Tenant propietario, nadie de otro Operador CAE, una sesión
/// limitada a Consulta ni una sesión sin rol de negocio (Sesión Privilegiada de plataforma).
/// La salida a petición del Tenant propietario la hace Soporte TALVEG con Sesión Privilegiada;
/// hoy no existe ese camino para la de un Operador CAE externo (<c>CerrarAccesoSoporteCommand</c>
/// solo cierra las de Soporte) y este comando lo rechaza (ver
/// <see cref="Sin_rol_de_negocio_en_la_sesion_no_se_revoca_nada"/>): queda como seguimiento.
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
    [InlineData("GestorCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("DireccionCae", false)]
    [InlineData("Administrador", false)]
    public async Task Cada_rol_del_equipo_del_Operador_CAE_externo_con_alcance_reactiva(string rol, bool conCartera)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        if (conCartera) DarCartera(persona, rol, TenantPropietario);
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue($"{rol} es del equipo del Operador CAE externo");
        delegacion.Activa.Should().BeTrue();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    public async Task El_Gestor_CAE_o_el_Coordinador_CAE_sin_cartera_sobre_ese_Tenant_propietario_no_reactiva(string rol)
    {
        var persona = Guid.NewGuid();
        _cuentas.Asignar(persona, OperadorCae, rol);
        DarCartera(persona, rol, Guid.NewGuid());
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = Reactivar(
            repositorio, new CurrentUserServiceFalso(persona, rol, OperadorCae), OperadorCae, unitOfWork: unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Consecuencia aceptada de la decisión (c) del 2026-10-26: revocar cierra las carteras en
    /// cascada, así que después el Gestor CAE que revocó ya no tiene alcance y no reactiva. Sí
    /// el Administrador del Operador CAE externo (alcanza toda la organización); si no hay
    /// ninguno, la reactivación queda en manos de Soporte TALVEG.
    /// </summary>
    [Fact]
    public async Task Tras_revocar_y_cerrarse_las_carteras_el_Gestor_CAE_no_reactiva_y_el_Administrador_del_Operador_si()
    {
        var administradorDelOperador = Guid.NewGuid();
        _cuentas.Asignar(administradorDelOperador, OperadorCae, "Administrador");
        var (delegacion, repositorio, unitOfWork) = Preparar();
        (await Desactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork)
                .Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        _catalogo.CarterasVivas.Clear();   // el cierre en cascada de la operación

        var delGestor = await Reactivar(repositorio, ElGestorCae(), OperadorCae, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        delGestor.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeFalse();

        var delAdministrador = await Reactivar(
                repositorio, new CurrentUserServiceFalso(administradorDelOperador, "Administrador", OperadorCae),
                OperadorCae, unitOfWork: unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);
        delAdministrador.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
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

        // Mensaje genérico (decisión del 2026-10-26): quien reactiva es el Operador CAE externo,
        // y ni el código ni el texto le dicen que el Tenant propietario trabaja con otro.
        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoReactivable");
        resultado.Error.Mensaje.Should().Be("No se puede reactivar esta Delegación; contacta con el Tenant propietario.");
        resultado.Error.Mensaje.Should().NotContainAny("otro", "Operador");
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
        var revocada = new DelegacionTenant(OperadorCae, TenantPropietario);
        revocada.Desactivar();
        var gestorSinCartera = Guid.NewGuid();
        _cuentas.Asignar(gestorSinCartera, OperadorCae, "GestorCae");
        var direccionDelOperador = Guid.NewGuid();
        _cuentas.Asignar(direccionDelOperador, OperadorCae, "DireccionCae");
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
        (await Preguntar(new CurrentUserServiceFalso(direccionDelOperador, "DireccionCae", OperadorCae)))
            .Should().Be((true, false, true, false), "la Dirección CAE alcanza toda la organización");
        (await Preguntar(new CurrentUserServiceFalso(gestorSinCartera, "GestorCae", OperadorCae)))
            .Should().Be((false, false, false, false), "sin cartera sobre ese Tenant propietario no tiene alcance");
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
            new TenantActualFijo(tenantActivo), _cuentas, tenants ?? new TenantsQueryContextFalso(), _catalogo);

    private ReactivarDelegacionTenantCommandHandler Reactivar(
        DelegacionTenantRepositorioFalso repositorio, CurrentUserServiceFalso quien, Guid? tenantActivo,
        AsignacionesOperativasWriterFalso? writer = null, IUnitOfWork? unitOfWork = null,
        TenantsQueryContextFalso? tenants = null, ITransaccionDeComando? transaccion = null) =>
        new(repositorio, quien, writer ?? new AsignacionesOperativasWriterFalso(), unitOfWork ?? new UnitOfWorkFalso(),
            tenants ?? new TenantsQueryContextFalso(), transaccion ?? new TransaccionDeComandoFalsa(),
            _catalogo, new DirectorioDestinosCarteraFalso(null), _cuentas,
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
