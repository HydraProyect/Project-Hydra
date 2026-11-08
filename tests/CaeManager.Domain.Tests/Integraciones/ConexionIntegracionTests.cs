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
    public void Por_defecto_no_es_un_buzon_compartido_y_se_puede_crear_como_tal()
    {
        new ConexionIntegracion("cae@cliente.com", "Buzón CAE").EsBuzonCompartido.Should().BeFalse();
        new ConexionIntegracion("piloto@cliente.com", "Buzón piloto", esBuzonCompartido: true).EsBuzonCompartido.Should().BeTrue();
    }

    [Fact]
    public void Rechaza_un_buzon_compartido_con_Gestor_propietario()
    {
        var accion = () => new ConexionIntegracion(
            "piloto@cliente.com", "Buzón piloto", gestorPropietarioId: Guid.NewGuid(), esBuzonCompartido: true);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_buzon_compartido_de_un_proveedor_que_no_es_Microsoft365()
    {
        var accion = () => new ConexionIntegracion(
            "34600000000", "Línea", proveedor: ProveedorIntegracion.WhatsApp, esBuzonCompartido: true);

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
