using Bunit;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Escribe la fecha de emisión cuando el test necesita cambiarla. Tras subir un archivo nuevo nace vacía;
/// al corregir el mismo registro sin archivo nuevo, conserva la fecha actual.
/// </summary>
internal static class EmisionDeRenovacion
{
    public static Task EscribirAsync<TComponente>(IRenderedComponent<TComponente> cut, string? fecha = null)
        where TComponente : IComponent
    {
        var campo = ((IRenderedComponent<IComponent>)cut).FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de emisión");
        return cut.InvokeAsync(() => campo.Instance.ValorChanged.InvokeAsync(fecha ?? DiaDeNegocio.Hoy().ToString("yyyy-MM-dd")));
    }
}
