using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

public class AutorizacionCuadroDireccionTests
{
    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task La_direccion_y_el_Coordinador_CAE_pueden_leer(string rol)
    {
        var accion = () => AutorizacionCuadroDireccion.ExigirLecturaAsync(new CurrentUserServiceFalso(rol: rol));
        await accion.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Administrador")]
    [InlineData(null)]
    public async Task Sin_rol_autorizado_se_rechaza_la_lectura(string? rol)
    {
        var accion = () => AutorizacionCuadroDireccion.ExigirLecturaAsync(new CurrentUserServiceFalso(rol: rol));
        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("GestorCae")]
    [InlineData("Administrador")]
    [InlineData(null)]
    public async Task El_handler_rechaza_antes_de_consultar_datos_incluso_con_mes_invalido(string? rol)
    {
        var handler = CrearHandler(rol);
        var accion = () => handler.Handle(new ObtenerCuadroDireccionQuery(2026, 0), CancellationToken.None);
        await accion.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    public async Task El_handler_admite_los_roles_autorizados_y_valida_el_mes(string rol)
    {
        var resultado = await CrearHandler(rol).Handle(new ObtenerCuadroDireccionQuery(2026, 0), CancellationToken.None);
        resultado.SinCifras.Should().Be(CuadroDireccionSinCifras.MesNoValido);
    }

    private static ObtenerCuadroDireccionQueryHandler CrearHandler(string? rol) => new(
        null!, null!, null!, null!, null!, null!, null!, null!,
        new CurrentUserServiceFalso(rol: rol), null!, null!, TimeProvider.System);
}
