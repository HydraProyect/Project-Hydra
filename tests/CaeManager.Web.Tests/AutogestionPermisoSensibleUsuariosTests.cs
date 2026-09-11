using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Usuarios.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// DEC-36 (REC-099): "solo otro Administrador puede concederlo o revocarlo"
/// — un Administrador que se edita a sí mismo no puede tocar su propio
/// PermisoConsultarAccesoDocumentosSensibles, ni para concedérselo ni para
/// revocárselo. Antes de este fix, EditarUsuarioAsync no comprobaba que el
/// usuario editado fuese distinto del actor, así que un Administrador podía
/// abrir "Editar" sobre su propia fila y otorgarse el permiso sin que otro
/// Administrador lo supiera.
/// </summary>
public class AutogestionPermisoSensibleUsuariosTests
{
    private static readonly Guid Actor = Guid.NewGuid();
    private static readonly Guid Otro = Guid.NewGuid();

    [Fact]
    public void Rechaza_autoconcesion_sobre_la_propia_cuenta() =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Actor, idActor: Actor, rolNuevo: Roles.Administrador,
                valorNuevo: true, valorActual: false)
            .Should().BeTrue();

    [Fact]
    public void Rechaza_autorrevocacion_sobre_la_propia_cuenta() =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Actor, idActor: Actor, rolNuevo: Roles.Administrador,
                valorNuevo: false, valorActual: true)
            .Should().BeTrue();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Permite_guardar_la_propia_cuenta_si_el_permiso_no_cambia(bool valor) =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Actor, idActor: Actor, rolNuevo: Roles.Administrador,
                valorNuevo: valor, valorActual: valor)
            .Should().BeFalse();

    [Fact]
    public void Permite_conceder_el_permiso_a_otra_cuenta() =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Otro, idActor: Actor, rolNuevo: Roles.Administrador,
                valorNuevo: true, valorActual: false)
            .Should().BeFalse();

    [Fact]
    public void Permite_revocar_el_permiso_a_otra_cuenta() =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Otro, idActor: Actor, rolNuevo: Roles.Administrador,
                valorNuevo: false, valorActual: true)
            .Should().BeFalse();

    /// <summary>
    /// El permiso se retira solo porque el rol deja de ser Administrador
    /// (defensa en profundidad ya existente), no porque nadie lo haya
    /// revocado explícitamente — bloquear este caso impediría a un
    /// Administrador cambiar su propio rol.
    /// </summary>
    [Fact]
    public void No_bloquea_la_caida_automatica_del_permiso_al_cambiar_el_propio_rol() =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Actor, idActor: Actor, rolNuevo: Roles.Consulta,
                valorNuevo: false, valorActual: true)
            .Should().BeFalse();

    [Fact]
    public void No_bloquea_nada_si_no_hay_actor_resuelto() =>
        Usuarios.EsAutogestionDelPermisoSensible(
                idEditado: Actor, idActor: null, rolNuevo: Roles.Administrador,
                valorNuevo: true, valorActual: false)
            .Should().BeFalse();
}
