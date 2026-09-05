using System.Reflection;
using CaeManager.Domain.Soporte;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Soporte;

/// <summary>
/// REC-208: <see cref="RegistroActividadSoporte"/> admite exactamente uno de
/// sus dos agrupadores posibles —una Delegación de Tenant o una Sesión
/// Privilegiada—, nunca los dos a la vez ni ninguno. Mismo patrón de prueba
/// que <c>DocumentoTests</c> (REC-101): las dos factorías públicas
/// (<see cref="RegistroActividadSoporte.PorViaHeredada"/> y
/// <see cref="RegistroActividadSoporte.PorSesionPrivilegiada"/>) nunca dejan
/// alcanzar la guarda del constructor privado con los dos agrupadores o con
/// ninguno, así que esas dos ramas de la invariante se prueban invocando el
/// constructor privado por reflexión — no hay ningún camino público honesto
/// que las alcance.
/// </summary>
public class RegistroActividadSoporteTests
{
    [Fact]
    public void PorViaHeredada_asigna_solo_DelegacionTenantId()
    {
        var usuarioId = Guid.NewGuid();
        var delegacionId = Guid.NewGuid();

        var registro = RegistroActividadSoporte.PorViaHeredada(
            usuarioId, delegacionId, TipoActividadSoporte.Navegacion, "/documentos");

        registro.UsuarioSoporteId.Should().Be(usuarioId);
        registro.DelegacionTenantId.Should().Be(delegacionId);
        registro.SesionPrivilegiadaId.Should().BeNull();
        registro.Tipo.Should().Be(TipoActividadSoporte.Navegacion);
        registro.Detalle.Should().Be("/documentos");
    }

    [Fact]
    public void PorSesionPrivilegiada_asigna_solo_SesionPrivilegiadaId()
    {
        var usuarioId = Guid.NewGuid();
        var sesionId = Guid.NewGuid();

        var registro = RegistroActividadSoporte.PorSesionPrivilegiada(
            usuarioId, sesionId, TipoActividadSoporte.Interaccion, "Exportar listado");

        registro.UsuarioSoporteId.Should().Be(usuarioId);
        registro.SesionPrivilegiadaId.Should().Be(sesionId);
        registro.DelegacionTenantId.Should().BeNull();
        registro.Tipo.Should().Be(TipoActividadSoporte.Interaccion);
        registro.Detalle.Should().Be("Exportar listado");
    }

    [Fact]
    public void PorViaHeredada_rechaza_Guid_Empty()
    {
        var accion = () => RegistroActividadSoporte.PorViaHeredada(
            Guid.NewGuid(), Guid.Empty, TipoActividadSoporte.AccesoConcedido);

        accion.Should().Throw<ArgumentException>().WithMessage("*delegación*");
    }

    [Fact]
    public void PorSesionPrivilegiada_rechaza_Guid_Empty()
    {
        var accion = () => RegistroActividadSoporte.PorSesionPrivilegiada(
            Guid.NewGuid(), Guid.Empty, TipoActividadSoporte.AccesoConcedido);

        accion.Should().Throw<ArgumentException>().WithMessage("*sesión*");
    }

    [Fact]
    public void PorViaHeredada_rechaza_usuario_Guid_Empty()
    {
        var accion = () => RegistroActividadSoporte.PorViaHeredada(
            Guid.Empty, Guid.NewGuid(), TipoActividadSoporte.AccesoConcedido);

        accion.Should().Throw<ArgumentException>().WithMessage("*usuario*");
    }

    [Fact]
    public void El_constructor_privado_rechaza_los_dos_agrupadores_a_la_vez()
    {
        var accion = () => InvocarConstructorConAgrupadores(Guid.NewGuid(), Guid.NewGuid());

        accion.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>()
            .WithMessage("*exactamente uno de sus dos agrupadores*");
    }

    [Fact]
    public void El_constructor_privado_rechaza_ningun_agrupador()
    {
        var accion = () => InvocarConstructorConAgrupadores(null, null);

        accion.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>()
            .WithMessage("*exactamente uno de sus dos agrupadores*");
    }

    /// <summary>
    /// Invoca el constructor privado de 5 parámetros directamente, saltándose
    /// las dos factorías públicas — es el único camino que puede llegar a la
    /// guarda con ambos agrupadores informados o con ninguno, porque
    /// <see cref="RegistroActividadSoporte.PorViaHeredada"/> y
    /// <see cref="RegistroActividadSoporte.PorSesionPrivilegiada"/> siempre
    /// informan exactamente uno.
    /// </summary>
    private static object InvocarConstructorConAgrupadores(Guid? delegacionTenantId, Guid? sesionPrivilegiadaId)
    {
        var constructor = typeof(RegistroActividadSoporte)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 5);

        return constructor.Invoke(
        [
            Guid.NewGuid(), delegacionTenantId, sesionPrivilegiadaId, TipoActividadSoporte.Navegacion, null
        ]);
    }
}
