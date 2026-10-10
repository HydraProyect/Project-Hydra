using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Configuracion;

/// <summary>
/// La pantalla de un filtro guardado es una lista cerrada: el validador admite los diez listados
/// que los ofrecen y rechaza cualquier otro nombre.
/// </summary>
public class GuardarFiltroCommandValidatorTests
{
    private static readonly GuardarFiltroCommandValidator Validador = new();

    [Theory]
    [InlineData(PantallasConFiltrosGuardados.Clientes)]
    [InlineData(PantallasConFiltrosGuardados.Documentos)]
    [InlineData(PantallasConFiltrosGuardados.Trabajadores)]
    [InlineData(PantallasConFiltrosGuardados.Empresas)]
    [InlineData(PantallasConFiltrosGuardados.Centros)]
    [InlineData(PantallasConFiltrosGuardados.Subcontratas)]
    [InlineData(PantallasConFiltrosGuardados.Vehiculos)]
    [InlineData(PantallasConFiltrosGuardados.Proyectos)]
    [InlineData(PantallasConFiltrosGuardados.Visitas)]
    [InlineData(PantallasConFiltrosGuardados.Gestiones)]
    public void Admite_los_listados_con_filtros_guardados(string pantalla)
    {
        var resultado = Validador.Validate(new GuardarFiltroCommand(pantalla, "Vencidos", "{\"estado\":\"Vencido\"}"));

        resultado.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("empresas")]
    [InlineData("Inventada")]
    [InlineData("")]
    public void Rechaza_una_pantalla_que_no_esta_en_la_lista(string pantalla)
    {
        var resultado = Validador.Validate(new GuardarFiltroCommand(pantalla, "Vencidos", "{\"estado\":\"Vencido\"}"));

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle(e => e.PropertyName == nameof(GuardarFiltroCommand.Pantalla));
    }

    [Fact]
    public void La_lista_de_admitidas_son_las_diez_y_no_se_repiten()
    {
        PantallasConFiltrosGuardados.Admitidas.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        // La columna Pantalla admite 50 caracteres (FiltroGuardadoConfiguration).
        PantallasConFiltrosGuardados.Admitidas.Should().OnlyContain(p => p.Length <= 50);
    }
}
