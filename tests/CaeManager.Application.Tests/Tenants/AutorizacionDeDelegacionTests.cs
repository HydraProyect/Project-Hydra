using CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones;
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
/// Se añadió después un tercer comando, <c>ReactivarDelegacionTenant</c>, que sí
/// tenía política — pero la de revocar. La equivalencia
/// <c>revocar ↔ reactivar</c> es falsa desde seguridad: una reduce capacidad y
/// la otra la devuelve. Compartirla dejaba que la parte que RECIBE el acceso
/// deshiciera la decisión de la parte que lo CONCEDE. Es la misma propiedad que
/// los otros dos, por eso vive en la misma batería: <b>las mutaciones de la
/// relación de delegación respetan la autoridad del lado correcto</b>.
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
            new AutorizacionDelegacionFalsa(autoriza: false), new CurrentUserServiceFalso(Usuario), unitOfWork);

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
            autorizacion, new CurrentUserServiceFalso(Usuario), unitOfWork);

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
            new AutorizacionDelegacionFalsa(autoriza: false), new CurrentUserServiceFalso(Usuario), unitOfWork);

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
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(usuarioId: null), unitOfWork);

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.SinUsuario");
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
            new AutorizacionDelegacionFalsa(autoriza: false), new CurrentUserServiceFalso(Usuario), unitOfWork);

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
            autorizacion, new CurrentUserServiceFalso(Usuario), new UnitOfWorkFalso());

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
            new UnitOfWorkFalso());

        var resultado = await handler.Handle(
            new Application.Tenants.Commands.CrearDelegacionTenant.CrearDelegacionTenantCommand(
                Consultora, ClienteDelegante), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.SinUsuario");
    }

    /// <summary>
    /// La tercera ruta que hay que dejar atrapada, y esta no se puede probar por
    /// comportamiento: que el tenant autorizador <b>no</b> se derive de
    /// <c>ITenantActual</c>.
    ///
    /// Sería la sustitución tentadora justamente aquí, porque estas operaciones
    /// se ejecutan desde una pantalla que puede estar dentro de un workspace
    /// delegado — y entonces <c>ITenantActual</c> es el del propietario, que a
    /// veces coincidiría con el Cliente Delegante y daría la respuesta correcta
    /// <i>por casualidad</i>. Un test de comportamiento pasaría, y la autoridad
    /// quedaría mal fundada.
    ///
    /// Se congela por la forma: ninguno de los dos handlers recibe
    /// <c>ITenantActual</c>, así que no puede derivarlo aunque quisiera.
    /// </summary>
    [Fact]
    public void Ningun_handler_de_delegacion_deriva_la_autoridad_del_tenant_activo()
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

            dependencias.Should().NotContain("ITenantActual",
                $"{handler.Name} debe autorizar contra el tenant que el comando nombra, no contra el workspace " +
                "activo: dentro de un workspace delegado ITenantActual es el del propietario y daría la " +
                "respuesta correcta por casualidad");
        }
    }

    // ── ReactivarDelegacionTenant: restaurar autoridad no es revocarla ────

    [Fact]
    public async Task El_administrador_del_cliente_delegante_puede_reactivar()
    {
        var (delegacion, delegaciones, _, unitOfWork) = Preparar();
        delegacion.Desactivar();

        var handler = new ReactivarDelegacionTenantCommandHandler(
            delegaciones, AutorizacionDelegacionFalsa.AdministradorDe(ClienteDelegante),
            new CurrentUserServiceFalso(Usuario), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
    }

    /// <summary>
    /// El caso que motiva el cambio, y el que un doble booleano no distinguiría:
    /// los dos usuarios tienen rol <c>Administrador</c>. Lo que los separa es de
    /// qué tenant lo son.
    /// </summary>
    [Fact]
    public async Task El_administrador_de_la_consultora_no_puede_deshacer_la_revocacion_del_cliente()
    {
        var (delegacion, delegaciones, _, unitOfWork) = Preparar();
        delegacion.Desactivar();

        var handler = new ReactivarDelegacionTenantCommandHandler(
            delegaciones, AutorizacionDelegacionFalsa.AdministradorDe(Consultora),
            new CurrentUserServiceFalso(Usuario), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        delegacion.Activa.Should().BeFalse("la parte que recibe el acceso no revierte la decisión de la que lo concede");
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_administrador_de_otro_tenant_no_reactiva_nada()
    {
        var (delegacion, delegaciones, _, unitOfWork) = Preparar();
        delegacion.Desactivar();

        var handler = new ReactivarDelegacionTenantCommandHandler(
            delegaciones, AutorizacionDelegacionFalsa.AdministradorDe(Guid.NewGuid()),
            new CurrentUserServiceFalso(Usuario), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("tener el rol correcto en el tenant equivocado no es tenerlo");
        delegacion.Activa.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_usuario_identificado_no_se_reactiva_nada()
    {
        var (delegacion, delegaciones, _, unitOfWork) = Preparar();
        delegacion.Desactivar();

        var handler = new ReactivarDelegacionTenantCommandHandler(
            delegaciones, new AutorizacionDelegacionFalsa(autoriza: true),
            new CurrentUserServiceFalso(usuarioId: null), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var resultado = await handler.Handle(
            new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("DelegacionTenant.SinUsuario");
        delegacion.Activa.Should().BeFalse();
    }

    /// <summary>
    /// La política anterior colgaba del <b>tenant de origen</b>. Este test fija
    /// que ya no: la decisión sale de quién es el usuario y de qué tenant nombra
    /// la delegación, y el workspace desde el que se pulse el botón no la mueve
    /// ni en un sentido ni en el otro.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_workspace_activo_no_altera_la_decision_de_reactivar(bool autorizado)
    {
        var decisiones = new List<bool>();

        foreach (var origen in new Guid?[] { Consultora, ClienteDelegante, Guid.NewGuid(), null })
        {
            var (delegacion, delegaciones, _, unitOfWork) = Preparar();
            delegacion.Desactivar();

            var handler = new ReactivarDelegacionTenantCommandHandler(
                delegaciones,
                AutorizacionDelegacionFalsa.AdministradorDe(autorizado ? ClienteDelegante : Consultora),
                new CurrentUserServiceFalso(Usuario, tenantOrigenId: origen),
                new AsignacionesOperativasWriterFalso(), unitOfWork);

            var resultado = await handler.Handle(
                new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

            decisiones.Add(resultado.EsExitoso);
        }

        decisiones.Should().AllBeEquivalentTo(autorizado,
            "el tenant de origen refleja el workspace desde el que se opera, no la autoridad de quien opera");
    }

    /// <summary>
    /// Sin esto, la diferencia entre "ya estaba activa" y "no encontrada" le
    /// contaría a un tercero si una delegación concreta está revocada ahora
    /// mismo — sobre un catálogo global que además dice qué Consultora opera
    /// sobre qué Cliente.
    /// </summary>
    [Fact]
    public async Task La_autorizacion_de_reactivar_corre_antes_que_el_estado_de_la_delegacion()
    {
        var (delegacion, delegaciones, _, unitOfWork) = Preparar();   // nace activa

        var ajeno = new ReactivarDelegacionTenantCommandHandler(
            delegaciones, AutorizacionDelegacionFalsa.AdministradorDe(Guid.NewGuid()),
            new CurrentUserServiceFalso(Usuario), new AsignacionesOperativasWriterFalso(), unitOfWork);

        var sinAutoridad = await ajeno.Handle(
            new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        var conAutoridad = await new ReactivarDelegacionTenantCommandHandler(
                delegaciones, AutorizacionDelegacionFalsa.AdministradorDe(ClienteDelegante),
                new CurrentUserServiceFalso(Usuario), new AsignacionesOperativasWriterFalso(), unitOfWork)
            .Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        // Misma delegación, mismo estado, dos respuestas — y la diferencia la
        // causa la autoridad, no el estado. Es lo que prueba el orden.
        sinAutoridad.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        conAutoridad.Error.Codigo.Should().Be("DelegacionTenant.YaActiva");
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
