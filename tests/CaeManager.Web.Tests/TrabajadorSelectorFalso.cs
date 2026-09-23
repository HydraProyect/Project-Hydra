using System.Reflection;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;

namespace CaeManager.Web.Tests;

/// <summary>
/// Construye un <see cref="TrabajadorSelectorDto"/> para los fakes de los selectores de Trabajador
/// con un DNI conocido <b>en el origen</b>. El DTO no tiene hoy propiedad DNI (P4, 2026-09-23), así
/// que el DNI no llega al componente; pero si alguien la reintrodujera —con cualquier nombre que
/// contenga «Dni» o «Nif», en cualquier posición—, este constructor se la rellena con
/// <see cref="DniSembrado"/> y los tests de renderizado que buscan ese valor en el marcado pasan a
/// rojo en cuanto una etiqueta lo pinte. Sin esto, un DTO con DNI opcional y un fake que no lo
/// rellena darían verde aunque la etiqueta volviera a enseñarlo.
/// </summary>
public static class TrabajadorSelectorFalso
{
    public const string DniSembrado = "48291637K";

    public static TrabajadorSelectorDto Crear(Guid id, string nombreCompleto, string? alias = null, string? empleador = null)
    {
        var constructor = typeof(TrabajadorSelectorDto)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single(c => c.GetParameters().All(p => p.ParameterType != typeof(TrabajadorSelectorDto)));

        var argumentos = constructor.GetParameters().Select(p => p.Name switch
        {
            "Id" => (object?)id,
            "NombreCompleto" => nombreCompleto,
            "Alias" => alias,
            "EmpleadorNombre" => empleador,
            var nombre when nombre!.Contains("Dni", StringComparison.OrdinalIgnoreCase)
                         || nombre.Contains("Nif", StringComparison.OrdinalIgnoreCase) => DniSembrado,
            _ => p.HasDefaultValue ? p.DefaultValue : null,
        }).ToArray();

        return (TrabajadorSelectorDto)constructor.Invoke(argumentos);
    }
}
