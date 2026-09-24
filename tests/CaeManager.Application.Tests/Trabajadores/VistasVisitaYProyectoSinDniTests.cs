using System.Reflection;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using FluentAssertions;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// Decisión del 2026-09-24 (DNI residual fuera de los selectores, S1 y S2): el detalle de la
/// Visita y la lista de técnicos del Proyecto no son vistas autorizadas para el DNI del
/// Trabajador. La garantía es estructural, como en <see cref="TrabajadorSelectorDtoSinDniTests"/>:
/// los DTO que llegan a esas vistas no tienen dónde llevarlo. Quien necesite el DNI va a la
/// ficha del Trabajador.
/// </summary>
public class VistasVisitaYProyectoSinDniTests
{
    public static TheoryData<Type, string> DtosSinDni => new()
    {
        { typeof(TrabajadorDocumentacionDto), nameof(TrabajadorDocumentacionDto.NombreCompleto) },
        { typeof(TrabajadorVisitaDto), nameof(TrabajadorVisitaDto.NombreCompleto) },
        { typeof(TecnicoProyectoDto), nameof(TecnicoProyectoDto.TrabajadorNombreCompleto) },
    };

    [Theory]
    [MemberData(nameof(DtosSinDni))]
    public void El_DTO_no_expone_ninguna_propiedad_de_DNI_ni_NIF(Type dto, string propiedadDeControl)
    {
        var propiedades = dto
            .GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(p => p.Name)
            .ToList();

        propiedades.Should().Contain(propiedadDeControl, "control positivo: la reflexión sí ve las propiedades del DTO");
        propiedades.Should().NotContain(
            nombre => nombre.Contains("Dni", StringComparison.OrdinalIgnoreCase)
                   || nombre.Contains("Nif", StringComparison.OrdinalIgnoreCase),
            $"{dto.Name} alimenta una vista que no está autorizada para el DNI (decisión del 2026-09-24)");
    }

    [Theory]
    [MemberData(nameof(DtosSinDni))]
    public void Ningun_parametro_del_constructor_lleva_DNI_ni_NIF(Type dto, string propiedadDeControl)
    {
        var parametros = dto
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(c => c.GetParameters())
            .Select(p => p.Name ?? string.Empty)
            .ToList();

        parametros.Should().Contain(propiedadDeControl, "control positivo");
        parametros.Should().NotContain(
            nombre => nombre.Contains("Dni", StringComparison.OrdinalIgnoreCase)
                   || nombre.Contains("Nif", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Las_vistas_reciben_esos_DTO()
    {
        // Sin esto, los tests de arriba podrían proteger un tipo que ya no llega a la vista.
        typeof(DocumentacionVisitaDto).GetProperty(nameof(DocumentacionVisitaDto.Trabajadores))!.PropertyType
            .Should().Be(typeof(IReadOnlyList<TrabajadorDocumentacionDto>));
        typeof(DetalleVisitaDto).GetProperty(nameof(DetalleVisitaDto.Trabajadores))!.PropertyType
            .Should().Be(typeof(IReadOnlyList<TrabajadorVisitaDto>));
        RespuestaDe(typeof(ObtenerTecnicosProyectoQuery))
            .Should().Be(typeof(IReadOnlyList<TecnicoProyectoDto>));
    }

    private static Type RespuestaDe(Type query) =>
        query.GetInterfaces()
            .Single(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(MediatR.IRequest<>))
            .GetGenericArguments().Single();
}
