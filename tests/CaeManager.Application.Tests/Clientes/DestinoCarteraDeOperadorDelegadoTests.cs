using CaeManager.Application.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Clientes;

/// <summary>
/// Cómo se forma el destino de una cuenta de un Operador CAE externo a partir de sus delegaciones vigentes.
/// La sentencia que las lee se comprueba sin conexión en <c>AccesosDelDestinoTests</c> y bajo RLS en
/// <c>DestinoCarteraClienteBajoRuntimeTests</c>.
/// </summary>
public class DestinoCarteraDeOperadorDelegadoTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid OtroOperador = Guid.NewGuid();

    private static DestinoCartera? Destino(params AccesoDelDestino[] accesos) =>
        DestinoCartera.DeOperadorDelegado(true, null, Operador, accesos);

    [Fact]
    public void Una_delegacion_ordinaria_lleva_el_rol_guardado_y_el_perfil_de_origen()
    {
        Destino(new AccesoDelDestino(Operador, "CoordinadorCae", false, "GestorCae")).Should()
            .Be(new DestinoCartera(true, "CoordinadorCae", null, EsOperadorDelegado: true, Operador, "GestorCae"));
    }

    [Fact]
    public void Sin_delegaciones_la_cuenta_no_es_alcanzable()
    {
        Destino().Should().BeNull("una delegación de Soporte revocada a la vez deja la lectura sin filas, no con perfil");
    }

    [Fact]
    public void Una_delegacion_solo_de_otra_organizacion_no_hace_alcanzable_la_cuenta()
    {
        Destino(new AccesoDelDestino(OtroOperador, "GestorCae", false, "GestorCae")).Should().BeNull();
    }

    [Fact]
    public void Una_delegacion_de_Soporte_no_lleva_perfil()
    {
        Destino(new AccesoDelDestino(Operador, "Consulta", true, null))!.PerfilDeOrigen.Should().BeNull();
    }

    [Fact]
    public void Con_varias_delegaciones_una_de_Soporte_deja_la_cuenta_sin_perfil()
    {
        var destino = Destino(new AccesoDelDestino(Operador, "GestorCae", false, "Administrador"), new(Operador, "GestorCae", true, null));

        destino!.RolEfectivo.Should().Be("GestorCae");
        destino.PerfilDeOrigen.Should().BeNull();
    }

    [Fact]
    public void Una_delegacion_de_Soporte_de_otra_organizacion_tambien_deja_la_cuenta_sin_perfil()
    {
        Destino(new AccesoDelDestino(Operador, "GestorCae", false, "Administrador"), new(OtroOperador, "Consulta", true, null))!
            .PerfilDeOrigen.Should().BeNull();
    }

    [Fact]
    public void Con_varios_roles_de_Identity_no_hay_perfil()
    {
        Destino(new AccesoDelDestino(Operador, "GestorCae", false, "GestorCae"), new(Operador, "GestorCae", false, "Consulta"))!
            .PerfilDeOrigen.Should().BeNull();
    }

    [Fact]
    public void Sin_rol_de_Identity_no_hay_perfil()
    {
        Destino(new AccesoDelDestino(Operador, "GestorCae", false, null))!.PerfilDeOrigen.Should().BeNull();
    }

    [Fact]
    public void Dos_delegaciones_con_el_mismo_perfil_y_distinto_rol_guardado_conservan_el_perfil_y_no_el_rol()
    {
        var destino = Destino(new AccesoDelDestino(Operador, "GestorCae", false, "CoordinadorCae"), new(Operador, "Consulta", false, "CoordinadorCae"));

        destino!.RolEfectivo.Should().BeNull();
        destino.PerfilDeOrigen.Should().Be("CoordinadorCae");
    }
}