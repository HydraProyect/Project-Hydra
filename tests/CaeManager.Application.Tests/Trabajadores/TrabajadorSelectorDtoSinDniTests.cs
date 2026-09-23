using System.Reflection;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using FluentAssertions;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// P4 (2026-09-23): ningún selector de Trabajador enseña el DNI, en ninguno de los dos alcances
/// (<see cref="AlcanceSelectorTrabajadores"/>). La garantía es estructural: el tipo que devuelve
/// <see cref="ObtenerTrabajadoresParaSelectorQuery"/> no tiene dónde llevarlo, así que ninguna
/// etiqueta puede pintarlo sin cambiar antes este contrato.
/// </summary>
public class TrabajadorSelectorDtoSinDniTests
{
    [Fact]
    public void La_consulta_de_selector_devuelve_TrabajadorSelectorDto()
    {
        typeof(ObtenerTrabajadoresParaSelectorQuery)
            .GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(MediatR.IRequest<>))
            .GetGenericArguments().Single()
            .Should().Be(typeof(IReadOnlyList<TrabajadorSelectorDto>),
                "el test de abajo solo protege los selectores si este es el tipo que les llega");
    }

    [Fact]
    public void TrabajadorSelectorDto_no_expone_ninguna_propiedad_de_DNI_ni_NIF()
    {
        var propiedades = typeof(TrabajadorSelectorDto)
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        propiedades.Should().Contain("NombreCompleto", "control positivo: la reflexión sí ve las propiedades del DTO");
        propiedades.Should().NotContain(
            nombre => nombre.Contains("Dni", StringComparison.OrdinalIgnoreCase)
                   || nombre.Contains("Nif", StringComparison.OrdinalIgnoreCase),
            "la etiqueta de un selector de Trabajador no es una vista autorizada para el DNI (P4)");
    }

    [Fact]
    public void Ningun_parametro_del_constructor_de_TrabajadorSelectorDto_lleva_DNI_ni_NIF()
    {
        var parametros = typeof(TrabajadorSelectorDto)
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.Name ?? string.Empty)
            .ToList();

        parametros.Should().Contain("NombreCompleto", "control positivo");
        parametros.Should().NotContain(
            nombre => nombre.Contains("Dni", StringComparison.OrdinalIgnoreCase)
                   || nombre.Contains("Nif", StringComparison.OrdinalIgnoreCase));
    }
}
