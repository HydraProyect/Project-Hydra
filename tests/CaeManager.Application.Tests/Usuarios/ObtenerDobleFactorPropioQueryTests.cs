using CaeManager.Application.Usuarios.Queries.ObtenerDobleFactorPropio;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>El aviso «activa la autenticación en dos pasos» lee el mismo dato que el behavior que la exige.</summary>
public class ObtenerDobleFactorPropioQueryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Devuelve_el_estado_del_doble_factor_de_la_cuenta_que_pregunta(bool activo)
    {
        var handler = new ObtenerDobleFactorPropioQueryHandler(
            new CurrentUserServiceFalso(usuarioId: Guid.NewGuid(), tieneDobleFactorActivo: activo));

        var resultado = await handler.Handle(new ObtenerDobleFactorPropioQuery(), CancellationToken.None);

        resultado.Should().Be(activo);
    }
}
