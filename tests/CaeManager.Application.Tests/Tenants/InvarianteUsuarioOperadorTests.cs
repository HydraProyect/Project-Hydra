using CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// El invariante de la cadena de autorización (ADR-011 § 2.7, endurecimiento
/// E3 del plan): quien recibe una cartera pertenece al tenant que opera.
///
/// Sin esta comprobación, autorizar como operador delegado a un usuario <b>del
/// tenant propietario</b> le crea una cartera externa cuyo propietario es su
/// propio tenant. Cuando ese usuario entra en su PROPIO workspace, el alcance
/// casa esa cartera y le concede lo que la cartera diga — que en el caso
/// universal es todos los clientes de su organización. Es una escalada de
/// privilegio, no un dato mal puesto.
///
/// La comprobación de visibilidad que ya existía no sirve para esto: acepta a
/// propósito tanto a los usuarios del tenant activo como a los operadores
/// delegados, así que da por bueno justamente el caso que hay que rechazar.
/// </summary>
public class InvarianteUsuarioOperadorTests
{
    private static readonly Guid Consultora = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rechaza_crear_desde_un_Tenant_ajeno_o_sin_Tenant_activo(bool sinTenant)
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones, new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            writer, new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(Guid.NewGuid()),
            unitOfWork, new TenantsQueryContextFalso(),
            new CaeManager.Application.Tests.Configuracion.TenantActualFijo(sinTenant ? null : Guid.NewGuid()));

        var resultado = await handler.Handle(new(delegacion.Id, Guid.NewGuid(), "GestorCae"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        resultado.Error.Mensaje.Should().Be("Para autorizar a esta persona, vuelve a tu organización.");
        asignaciones.Asignaciones.Should().BeEmpty();
        writer.CarterasAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// La guarda de extremos lo admitiría, pero la cartera es dato del Tenant propietario:
    /// con el Operador CAE externo activo, posicion_en_la_asignacion no deja escribirla y, con
    /// el origen en el Tenant propietario, tampoco localizar la Asignación de Operación.
    /// </summary>
    [Fact]
    public async Task Rechaza_crear_la_de_un_Operador_CAE_externo_con_el_Operador_CAE_activo()
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();
        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones, new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            writer, new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(Guid.NewGuid()),
            unitOfWork, new TenantsQueryContextFalso(),
            new CaeManager.Application.Tests.Configuracion.TenantActualFijo(Consultora));

        var resultado = await handler.Handle(new(delegacion.Id, Guid.NewGuid(), "GestorCae"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.RequiereTenantPropietario");
        asignaciones.Asignaciones.Should().BeEmpty();
        writer.CarterasAbiertas.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Rechaza_asignar_como_operador_a_un_usuario_del_tenant_propietario()
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();

        // El usuario existe y es "visible" — pero es de la casa del cliente
        // delegante, no de la consultora que opera.
        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Propietario),
            new AsignacionesOperativasWriterFalso(),
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork,
            new TenantsQueryContextFalso(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(Propietario));

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.UsuarioDeOtroTenant");
        asignaciones.Asignaciones.Should().BeEmpty();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Acepta_a_un_usuario_de_la_consultora_que_opera()
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();
        var writer = new AsignacionesOperativasWriterFalso();

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: Consultora),
            writer,
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork,
            new TenantsQueryContextFalso(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(Propietario));

        var usuarioId = Guid.NewGuid();
        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, usuarioId, "GestorCae"),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignaciones.Asignaciones.Should().ContainSingle();

        // Y la doble escritura acompaña: no basta con la fila antigua.
        writer.CarterasAbiertas.Should().ContainSingle()
            .Which.Usuario.Should().Be(usuarioId);
    }

    [Fact]
    public async Task Rechaza_a_un_usuario_que_no_existe()
    {
        var (delegacion, delegaciones, asignaciones, unitOfWork) = Preparar();

        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            asignaciones, delegaciones,
            new DirectorioUsuariosServiceFalso(esVisible: true, tenantDelUsuario: null),
            new AsignacionesOperativasWriterFalso(),
            new AutorizacionDelegacionFalsa(autoriza: true), new CurrentUserServiceFalso(Guid.NewGuid()), unitOfWork,
            new TenantsQueryContextFalso(), new CaeManager.Application.Tests.Configuracion.TenantActualFijo(Propietario));

        var resultado = await handler.Handle(
            new CrearAsignacionOperadorDelegadoCommand(delegacion.Id, Guid.NewGuid(), "GestorCae"),
            CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.UsuarioDeOtroTenant");
    }

    private static (DelegacionTenant, DelegacionTenantRepositorioFalso,
        AsignacionOperadorDelegadoRepositorioFalso, UnitOfWorkFalso) Preparar()
    {
        var delegacion = new DelegacionTenant(Consultora, Propietario);
        var delegaciones = new DelegacionTenantRepositorioFalso();
        delegaciones.Agregar(delegacion);

        return (delegacion, delegaciones, new AsignacionOperadorDelegadoRepositorioFalso(), new UnitOfWorkFalso());
    }
}
