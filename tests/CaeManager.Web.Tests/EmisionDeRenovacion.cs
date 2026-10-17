using Bunit;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Renovar un documento abre el formulario con la fecha de emisión VACÍA (no con la del documento anterior): quien
/// renueva tiene que escribirla. Los tests que guardan una renovación sin ocuparse de la fecha la escriben con esto.
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
