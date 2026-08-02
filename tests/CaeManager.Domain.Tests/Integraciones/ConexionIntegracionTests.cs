using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class ConexionIntegracionTests
{
    [Fact]
    public void Se_crea_habilitada_con_los_datos_informados()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.BuzonEmail.Should().Be("cae@cliente.com");
        conexion.Nombre.Should().Be("Buzón CAE");
        conexion.ClienteId.Should().BeNull();
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
    }

    [Fact]
    public void Rechaza_un_buzon_vacio()
    {
        var accion = () => new ConexionIntegracion(" ", "Buzón CAE");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_nombre_vacio()
    {
        var accion = () => new ConexionIntegracion("cae@cliente.com", " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarConError_cambia_el_estado_y_guarda_el_mensaje()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.MarcarConError("El refresh token ha expirado.");

        conexion.Estado.Should().Be(EstadoConexionIntegracion.ConError);
        conexion.UltimoError.Should().Be("El refresh token ha expirado.");
    }

    [Fact]
    public void Rehabilitar_limpia_el_ultimo_error()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.MarcarConError("fallo");

        conexion.Rehabilitar();

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
    }

    [Fact]
    public void Deshabilitar_cambia_el_estado()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.Deshabilitar();

        conexion.Estado.Should().Be(EstadoConexionIntegracion.Deshabilitada);
    }
}
