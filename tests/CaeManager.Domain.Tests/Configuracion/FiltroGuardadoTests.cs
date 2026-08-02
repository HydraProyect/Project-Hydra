using CaeManager.Domain.Configuracion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Configuracion;

public class FiltroGuardadoTests
{
    [Fact]
    public void Se_crea_con_los_datos_dados()
    {
        var usuarioId = Guid.NewGuid();

        var filtro = new FiltroGuardado(usuarioId, "Clientes", "Críticos sin CIF", "{\"soloCriticos\":true}");

        filtro.UsuarioId.Should().Be(usuarioId);
        filtro.Pantalla.Should().Be("Clientes");
        filtro.Nombre.Should().Be("Críticos sin CIF");
        filtro.ValoresJson.Should().Be("{\"soloCriticos\":true}");
    }

    [Fact]
    public void No_se_puede_crear_sin_usuario()
    {
        var accion = () => new FiltroGuardado(Guid.Empty, "Clientes", "nombre", "{}");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_puede_crear_sin_pantalla()
    {
        var accion = () => new FiltroGuardado(Guid.NewGuid(), "", "nombre", "{}");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_puede_crear_sin_nombre()
    {
        var accion = () => new FiltroGuardado(Guid.NewGuid(), "Clientes", "", "{}");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_se_puede_crear_sin_valores()
    {
        var accion = () => new FiltroGuardado(Guid.NewGuid(), "Clientes", "nombre", "");

        accion.Should().Throw<ArgumentException>();
    }
}
