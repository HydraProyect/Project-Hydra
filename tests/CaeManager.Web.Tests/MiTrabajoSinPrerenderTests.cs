using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MiTrabajoPagina = CaeManager.Web.Features.Bandeja.Pages.MiTrabajo;

namespace CaeManager.Web.Tests;

/// <summary>
/// M-23 (MJ-03): Mi trabajo carga la cartera entera en <c>OnInitializedAsync</c> y no usa
/// <c>PersistentComponentState</c>; con el prerender de <c>InteractiveServer</c> en una carga completa de la
/// página (F5, enlace externo) esa carga corría dos veces, una para el HTML estático y otra al conectar el
/// circuito. Sin prerender corre una sola vez. El resto de la página sigue siendo una raíz de circuito
/// <c>InteractiveServer</c> (lo exigen los ratchets de página interactiva).
/// </summary>
public class MiTrabajoSinPrerenderTests
{
    [Fact]
    public void Mi_trabajo_es_InteractiveServer_sin_prerender()
    {
        var atributo = typeof(MiTrabajoPagina).GetCustomAttribute<RenderModeAttribute>(inherit: true);

        atributo.Should().NotBeNull();
        var modo = atributo!.Mode.Should().BeOfType<InteractiveServerRenderMode>().Subject;
        modo.Prerender.Should().BeFalse(
            "la carga de la cartera corre en OnInitializedAsync y, con prerender, se ejecuta dos veces en la carga completa");
    }
}
