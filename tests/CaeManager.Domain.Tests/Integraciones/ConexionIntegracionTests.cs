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
    public void Por_defecto_no_es_un_buzon_compartido_ni_tiene_directorio()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.EsBuzonCompartido.Should().BeFalse();
        conexion.DirectorioEntraId.Should().BeNull();
    }

    [Fact]
    public void Un_buzon_compartido_es_de_Microsoft365_sin_Gestor_y_guarda_el_directorio_normalizado_y_quien_lo_dio_de_alta()
    {
        var usuarioId = Guid.NewGuid();

        var conexion = ConexionIntegracion.CrearBuzonCompartido(
            "piloto@cliente.com", "Buzón piloto", "{AABBCCDD-0000-1111-2222-333344445555}", usuarioId);

        conexion.EsBuzonCompartido.Should().BeTrue();
        conexion.Proveedor.Should().Be(ProveedorIntegracion.Microsoft365);
        conexion.GestorPropietarioId.Should().BeNull();
        conexion.DirectorioEntraId.Should().Be("aabbccdd-0000-1111-2222-333344445555");
        conexion.ConsentidoPorUsuarioId.Should().Be(usuarioId);
    }

    [Theory]
    [InlineData("common")]
    [InlineData("organizations")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void Rechaza_un_buzon_compartido_sin_un_directorio_GUID(string directorio)
    {
        var accion = () => ConexionIntegracion.CrearBuzonCompartido(
            "piloto@cliente.com", "Buzón piloto", directorio, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_buzon_compartido_sin_usuario_que_lo_diera_de_alta()
    {
        var accion = () => ConexionIntegracion.CrearBuzonCompartido(
            "piloto@cliente.com", "Buzón piloto", Guid.NewGuid().ToString(), Guid.Empty);

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

    [Fact]
    public void La_identidad_del_buzon_se_fija_una_vez_y_admite_repetir_la_misma()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        conexion.FijarIdentidadBuzon("inbox-1");
        conexion.FijarIdentidadBuzon("inbox-1");

        conexion.IdentidadBuzon.Should().Be("inbox-1");
    }

    [Fact]
    public void La_identidad_del_buzon_no_se_cambia_por_la_de_otro_buzon()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");
        conexion.FijarIdentidadBuzon("inbox-1");

        var accion = () => conexion.FijarIdentidadBuzon("inbox-2");

        accion.Should().Throw<InvalidOperationException>();
        conexion.IdentidadBuzon.Should().Be("inbox-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Rechaza_una_identidad_del_buzon_vacia(string identidad)
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        var accion = () => conexion.FijarIdentidadBuzon(identidad);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_una_identidad_del_buzon_demasiado_larga()
    {
        var conexion = new ConexionIntegracion("cae@cliente.com", "Buzón CAE");

        var accion = () => conexion.FijarIdentidadBuzon(new string('a', ReclamacionBuzonIntegracion.LongitudMaximaIdentidadBuzon + 1));

        accion.Should().Throw<ArgumentException>();
    }
}
