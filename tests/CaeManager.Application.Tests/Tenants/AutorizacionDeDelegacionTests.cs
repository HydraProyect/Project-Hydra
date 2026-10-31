using CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.Operaciones.IncorporacionCartera;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Los dos comandos que crean autoridad de plano 2 —vincular dos tenants, y
/// autorizar a una persona concreta dentro de esa vinculación— no comprobaban
/// quién los invocaba. No eran explotables porque ninguna pantalla los
/// despachaba, pero eso es una propiedad del cableado de hoy, no una frontera:
/// están registrados en MediatR y un solo <c>Send</c> los habría activado, con
/// el único filtro de la lista blanca de escritura — que incluye a
/// <c>GestorCae</c>.
///
/// <b>No aparecieron en el inventario de <c>EsPlataforma</c> precisamente porque
/// no lo usaban.</b> Un inventario que pregunta "¿dónde se comprueba X?" no
/// puede encontrar una operación que carece de X. La pregunta correcta es "¿qué
/// operaciones pueden cambiar este estado, y dónde se autoriza cada una?".
///
/// La autoridad la fija ADR-004 § 12.2 y no es de Hydra: <b>solo un
/// <c>Administrador</c> del tenant del Cliente Delegante</b>. Y § 11.1 lo cierra
/// por el otro lado — Hydra nunca inicia una delegación.
///
/// <para>
/// Revocar la Asignación de Operación de un Operador CAE externo no sigue esta regla: desde las
/// decisiones de producto del 2026-10-26 lo decide el equipo del propio Operador CAE externo con
/// alcance sobre el Tenant propietario (<see cref="CaeManager.Application.Tenants.EquipoDelOperadorCaeExterno"/>).
/// Reactivarla es solo del Administrador del Tenant propietario (decisión SEG-7 del 2026-10-31). Sus
/// tests están en <see cref="RevocacionDelegacionTests"/>.
/// </para>
/// </summary>
public class AutorizacionDeDelegacionTests
{
    private static readonly Guid Consultora = Guid.NewGuid();
    private static readonly Guid ClienteDelegante = Guid.NewGuid();
    private static readonly Guid Usuario = Guid.NewGuid();

    [Fact]
    public async Task Sin_autoridad_del_cliente_delegante_no_se_asigna_ningun_operador()
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            writer,
            new AutorizacionDelegacionFalsa(autoriza: false), new CurrentUserServiceFalso(Usuario), unitOfWork,
            new TenantsQueryContextFalso());

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");

        // Y no deja rastro por ninguna de las dos vías de escritura.
        asignaciones.Asignaciones.Should().BeEmpty();
        writer.CarterasAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_autoridad_se_comprueba_contra_el_cliente_delegante_no_contra_la_consultora()
    {
        // El contrato de § 12.2 en una línea: quien concede el acceso a unos
        // datos es su dueño. Preguntar por la Consultora dejaría que la parte
        // que RECIBE el acceso se lo concediera a sí misma.
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();
        var autorizacion = new AutorizacionDelegacionFalsa(autoriza: true);

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            new AsignacionesOperativasWriterFalso(),
            autorizacion, new CurrentUserServiceFalso(Usuario), unitOfWork,
            new TenantsQueryContextFalso());

        await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        autorizacion.UltimoTenantConsultado.Should().Be(ClienteDelegante,
            "la autoridad es del dueño de los datos, no de quien va a operarlos");
        autorizacion.UltimoTenantConsultado.Should().NotBe(Consultora);
    }

    [Fact]
    public async Task La_autorizacion_se_comprueba_antes_que_el_estado_de_la_delegacion()
    {
        // Una delegación desactivada y un actor sin autoridad: el error tiene
        // que ser el de autorización. Si contestara "esta delegación está
        // desactivada", estaría confirmando que la delegación existe a alguien
        // que no tiene por qué saberlo.
        var delegacion = new DelegacionTenant(Consultora, ClienteDelegante);
        delegacion.Desactivar();

        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        var asignaciones = new AsignacionOperadorDelegadoRepositorioFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            new AsignacionesOperativasWriterFalso(),
            new AutorizacionDelegacionFalsa(autoriza: false), new CurrentUserServiceFalso(Usuario), unitOfWork,
            new TenantsQueryContextFalso());

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado",
            "el estado interno de la delegación no se revela a quien no está autorizado sobre ella");
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_asigna_nada()
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            new AsignacionesOperativasWriterFalso(),
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(usuarioId: null), unitOfWork,
            new TenantsQueryContextFalso());

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.SinUsuario");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// Cuarto sitio del mismo hallazgo de Codex (ver ReactivarDelegacionTenant y
    /// AutorizarOperadorCaeExternoQueries): <c>PuedeGestionarDelegacionesAsync</c> no
    /// excluye por sí sola el Tenant de plataforma como <c>TenantClienteId</c>. Con una
    /// <c>DelegacionTenant</c> heredada que tuviera a TALVEG como Cliente Delegante, su
    /// Administrador inicial podría autorizar operadores sobre "datos" del propio TALVEG.
    /// <c>AutorizacionDelegacionFalsa</c> dice que sí a propósito: lo que corta aquí es
    /// la comprobación de <c>EsPlataforma</c>, no la autoridad.
    /// </summary>
    [Fact]
    public async Task No_se_puede_asignar_un_operador_sobre_una_delegacion_heredada_con_el_tenant_de_plataforma_como_cliente()
    {
        var plataforma = new Tenant("TALVEG");
        plataforma.MarcarComoPlataforma();
        var delegacion = new DelegacionTenant(Consultora, plataforma.Id);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        var asignaciones = new AsignacionOperadorDelegadoRepositorioFalso();
        var tenants = new TenantsQueryContextFalso();
        tenants.ListaTenants.Add(plataforma);
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            new AsignacionesOperativasWriterFalso(),
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(Usuario), unitOfWork,
            tenants);

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("TALVEG nunca es Tenant propietario de un Operador CAE externo (ADR-011 § 1)");
        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        asignaciones.Asignaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    // ── CrearDelegacionTenantCommand: quién puede vincular dos tenants ─────

    /// <summary>
    /// El contexto de tenants va a <c>null</c> a propósito: si el handler lo
    /// tocara antes de autorizar, este test reventaría con una
    /// <c>NullReferenceException</c> en vez de devolver el error esperado.
    /// Prueba las dos cosas a la vez — que deniega, y que deniega <b>antes</b> de
    /// mirar si esos tenants existen.
    /// </summary>
    [Fact]
    public async Task Sin_autoridad_del_cliente_delegante_no_se_crea_ninguna_delegacion()
    {
        var delegaciones = new DelegacionTenantRepositorioFalso();
        var writer = new AsignacionesOperativasWriterFalso();
        var unitOfWork = new UnitOfWorkFalso();

        var handler = new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommandHandler(
            delegaciones, tenantsContext: null!, writer,
            new AutorizacionDelegacionFalsa(autoriza: false), new CurrentUserServiceFalso(Usuario), unitOfWork, new TransaccionDeComandoFalsa(), new AsignacionAutomaticaInerte(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(ClienteDelegante));

        var resultado = await handler.Handle(
            new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommand(
                Consultora, ClienteDelegante), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado",
            "quien no tiene autoridad no debe poder distinguir, por el mensaje, qué identificadores de tenant " +
            "corresponden a organizaciones reales");

        delegaciones.Delegaciones.Should().BeEmpty();
        writer.OperacionesAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Al_vincular_la_autoridad_se_pide_sobre_el_cliente_delegante()
    {
        // Congela QUÉ SUJETO es dueño de la autoridad. Si alguien cambiara
        // TenantClienteId por TenantConsultoraId, el resultado seguiría siendo
        // "denegado" y un test que solo mirase el código de error pasaría igual.
        var autorizacion = new AutorizacionDelegacionFalsa(autoriza: false);

        var handler = new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommandHandler(
            new DelegacionTenantRepositorioFalso(), tenantsContext: null!,
            new AsignacionesOperativasWriterFalso(),
            autorizacion, new CurrentUserServiceFalso(Usuario), new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new AsignacionAutomaticaInerte(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(Consultora));

        await handler.Handle(
            new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommand(
                Consultora, ClienteDelegante), CancellationToken.None);

        autorizacion.UltimoTenantConsultado.Should().Be(ClienteDelegante,
            "quien concede el acceso a unos datos es su dueño (ADR-004 § 12.2)");
        autorizacion.UltimoTenantConsultado.Should().NotBe(Consultora,
            "la Consultora es quien RECIBE el acceso: preguntarle a ella la dejaría concedérselo a sí misma");
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_vincula_nada()
    {
        var handler = new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommandHandler(
            new DelegacionTenantRepositorioFalso(), tenantsContext: null!,
            new AsignacionesOperativasWriterFalso(),
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(usuarioId: null),
            new UnitOfWorkFalso(), new TransaccionDeComandoFalsa(), new AsignacionAutomaticaInerte(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(ClienteDelegante));

        var resultado = await handler.Handle(
            new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommand(
                Consultora, ClienteDelegante), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.SinUsuario");
    }

    /// <summary>
    /// La autoridad sobre la Delegación se conserva en su servicio específico.
    /// CrearDelegacionTenant y ReactivarDelegacionTenant reciben ITenantActual solo para
    /// rechazar un contexto de escritura ajeno, después de autorizar contra el Tenant
    /// propietario de la petición.
    /// Los tests de comportamiento comprueban ambos predicados por separado.
    /// </summary>
    [Fact]
    public void Los_handlers_conservan_las_dependencias_de_autorizacion_de_delegacion()
    {
        var handlers = new[]
        {
            typeof(Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommandHandler),
            typeof(CrearAsignacionOperadorDelegadoCommandHandler),
            typeof(ReactivarDelegacionTenantCommandHandler),
        };

        foreach (var handler in handlers)
        {
            var dependencias = handler.GetConstructors().Single()
                .GetParameters().Select(p => p.ParameterType.Name).ToList();

            // A3: y tampoco por AdminPlataforma. El alta de un Cliente Delegante
            // SÍ la autoriza esa capacidad —ahí el cliente todavía no existe y
            // nadie puede consentir (ADR-004 § 11.1)—, pero eso no le concede
            // capacidad GENÉRICA de gestionar delegaciones sobre tenants que ya
            // existen: eso sigue siendo del Administrador del Cliente Delegante.
            //
            // Se congela por la forma porque unificar ambas autoridades sería un
            // cambio de una línea en el constructor, y ningún test de
            // comportamiento de estos comandos lo detectaría: seguirían
            // denegando a quien no tiene ninguna de las dos.
            dependencias.Should().NotContain("IAutorizacionAdminPlataforma",
                $"{handler.Name} responde ante el dueño de los datos, no ante la administración de " +
                "la plataforma: ADR-004 § 12.2 pone 'aprueba, modifica y revoca' del lado del Cliente " +
                "Delegante");

            // Crear y Reactivar usan el contexto solo para restringir, después de resolver la autoridad
            // del Administrador del Tenant propietario (Reactivar, decisión SEG-7 del 2026-10-31).
            if (handler == typeof(Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommandHandler)
                || handler == typeof(ReactivarDelegacionTenantCommandHandler))
                dependencias.Should().Contain("IAutorizacionDelegacionTenant");
            else
                dependencias.Should().NotContain("ITenantActual",
                    $"{handler.Name} debe autorizar contra el tenant que el comando nombra, no contra el workspace " +
                    "activo: dentro de un workspace delegado ITenantActual es el del propietario y daría la " +
                    "respuesta correcta por casualidad");
        }
    }

    // ── ReactivarDelegacionTenant: desde SEG-7 (2026-10-31) la decide solo el Administrador del Tenant
    // propietario (AdministradorQueReactiva), nunca el Operador CAE externo. Sus tests viven en
    // RevocacionDelegacionTests, junto a los de revocar.

    /// <summary>
    /// Segundo acto excluido del Encargo de administración (decisión D-8, 2026-10-08): autorizar o
    /// retirar Operadores CAE externos. Quien administra por encargo tiene rol efectivo Administrador
    /// o Dirección CAE en el Tenant propietario, pero es miembro del Operador CAE: la autoridad sobre
    /// las delegaciones es de pertenencia, y el rol elevado no la abre en ninguno de los tres comandos.
    /// </summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task El_rol_elevado_por_un_encargo_no_autoriza_ni_asigna_ni_reactiva_Operadores_CAE_externos(string rolElevado)
    {
        // Administrador en su casa (el Operador CAE) y con el rol elevado en el Tenant propietario;
        // la pertenencia, preguntada sobre el Tenant propietario, contesta que no.
        var quienAdministraPorEncargo = new CurrentUserServiceFalso(Usuario, rolElevado, Consultora);
        var soloMiembroDelOperadorCae = AutorizacionDelegacionFalsa.AdministradorDe(Consultora);

        var delegacionesNuevas = new DelegacionTenantRepositorioFalso();
        var writerDelAlta = new AsignacionesOperativasWriterFalso();
        var guardadoDelAlta = new UnitOfWorkFalso();
        var alta = await new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommandHandler(
                delegacionesNuevas, tenantsContext: null!, writerDelAlta, soloMiembroDelOperadorCae,
                quienAdministraPorEncargo, guardadoDelAlta, new TransaccionDeComandoFalsa(), new AsignacionAutomaticaInerte(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(ClienteDelegante))
            .Handle(new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommand(
                Guid.NewGuid(), ClienteDelegante), CancellationToken.None);

        alta.Error.Codigo.Should().Be("DelegacionTenant.NoAutorizado", "no autoriza a otro Operador CAE externo");
        delegacionesNuevas.Delegaciones.Should().BeEmpty();
        writerDelAlta.OperacionesAbiertas.Should().BeEmpty();
        guardadoDelAlta.VecesGuardado.Should().Be(0);

        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();
        var asignacion = await new CrearAsignacionOperadorDelegadoCommandHandler(
                asignaciones, delegaciones,
                new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
                writer, soloMiembroDelOperadorCae, quienAdministraPorEncargo, unitOfWork, new TenantsQueryContextFalso())
            .Handle(new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"), CancellationToken.None);

        asignacion.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        asignaciones.Asignaciones.Should().BeEmpty();
        writer.CarterasAbiertas.Should().BeEmpty();

        // Reactivar se decide por la pertenencia al Tenant propietario, nunca por el rol elevado en él:
        // quien administra por encargo es del Operador CAE externo, y este nunca reactiva. El doble
        // autoriza a propósito para que lo que niegue sea el Tenant de origen.
        var rolesEnOrigen = new DirectorioRolesEnOrigen();
        delegacion.Desactivar();
        var reactivacion = await new ReactivarDelegacionTenantCommandHandler(
                delegaciones, quienAdministraPorEncargo,
                new AsignacionesOperativasWriterFalso(), unitOfWork, new TenantsQueryContextFalso(),
                new TransaccionDeComandoFalsa(), new CatalogoIncorporacionCarteraFalso(),
                new DirectorioDestinosCarteraFalso(null), rolesEnOrigen, new BloqueoCarteraUsuarioFalso(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(ClienteDelegante),
                new AutorizacionDelegacionFalsa(autoriza: true))
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        reactivacion.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada", "el rol elevado por un encargo no convierte en Tenant propietario");
        delegacion.Activa.Should().BeFalse();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    private static (DelegacionTenant, DelegacionTenantRepositorioFalso,
        AsignacionOperadorDelegadoRepositorioFalso, UnitOfWorkFalso) Preparar()
    {
        var delegacion = new DelegacionTenant(Consultora, ClienteDelegante);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);
        return (delegacion, delegaciones,
            new AsignacionOperadorDelegadoRepositorioFalso(), new UnitOfWorkFalso());
    }
}
