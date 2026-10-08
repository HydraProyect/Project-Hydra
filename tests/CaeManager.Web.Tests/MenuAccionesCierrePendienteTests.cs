using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public class MenuAccionesCierrePendienteTests : BunitContext
{
    public MenuAccionesCierrePendienteTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    [Fact]
    public async Task Escape_durante_el_registro_cancela_el_foco_del_item_que_retirara_el_cierre()
    {
        IRenderedComponent<MenuAcciones>? cut = null;
        string? idItem = null;
        var frontera = -1;
        var pasosHook = 0;
        var ejecuciones = 0;
        async Task CerrarDuranteRegistroAsync()
        {
            pasosHook++;
            var menu = cut!;
            menu.Find(".menu-acciones-disparador").GetAttribute("aria-expanded").Should().Be("true");
            var item = menu.FindComponent<ItemMenuAccion>();
            item.Find("[role=menuitem]").TextContent.Should().Be("Acción observable");
            // Solo ElementReference, mismo instrumento del MenuAccionesTests existente.
            var campo = typeof(ItemMenuAccion).GetField("_boton",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            idItem = ((ElementReference)campo.GetValue(item.Instance)!).Id;
            idItem.Should().NotBeNullOrEmpty("el ítem ya terminó su primer render");
            // bUnit2.10.3 expone Invocations como colección de solo lectura, sin Clear.
            // La frontera observable excluye solicitudes anteriores válidas sin borrar evidencia.
            frontera = JSInterop.Invocations.Count;
            await menu.Find(".menu-acciones-disparador").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        }

        cut = Render<MenuAcciones>(p => p.AddChildContent((RenderFragment)(b =>
        {
            b.OpenComponent<ItemMenuAccion>(0);
            b.AddAttribute(1, nameof(ItemMenuAccion.OnClick), EventCallback.Factory.Create(this, () => ejecuciones++));
            b.AddAttribute(2, nameof(ItemMenuAccion.ChildContent), (RenderFragment)(c => c.AddContent(0, "Acción observable")));
            b.CloseComponent();
            // Sibling posterior al ítem: su OnAfterRenderAsync ejecuta Escape cuando el
            // ítem ya registró su referencia, antes de concluir esta tanda de callbacks.
            b.OpenComponent<CerrarTrasRegistrarItem>(3);
            b.AddAttribute(4, nameof(CerrarTrasRegistrarItem.AlRenderizar),
                EventCallback.Factory.Create(this, CerrarDuranteRegistroAsync));
            b.CloseComponent();
        })));

        await cut.Find(".menu-acciones-disparador").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowDown" });
        cut.WaitForAssertion(() =>
        {
            pasosHook.Should().Be(1);
            frontera.Should().BeGreaterThanOrEqualTo(0);
            cut.Find(".menu-acciones-disparador").GetAttribute("aria-expanded").Should().Be("false");
            cut.FindAll("[role=menu]").Should().BeEmpty();
            cut.FindComponents<ItemMenuAccion>().Should().BeEmpty();
        });
        // Flush cerrado mediante API pública de render: no accede a flags/ciclo privados.
        cut.Render();
        var focosPosteriores = JSInterop.Invocations.Skip(frontera)
            .Where(i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Arguments[0]).OfType<ElementReference>().Select(e => e.Id).ToArray();
        focosPosteriores.Should().NotContain(idItem,
            "Escape durante el registro debe cancelar el foco pendiente del ítem retirado");
        ejecuciones.Should().Be(0, "cerrar con Escape no ejecuta la acción del ítem");
    }

    public sealed class CerrarTrasRegistrarItem : ComponentBase
    {
        [CascadingParameter] public MenuAcciones? Menu { get; set; }
        [Parameter] public EventCallback AlRenderizar { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, string.Empty);
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (!firstRender) return;
            Menu.Should().NotBeNull("el hook participa en el CascadingValue real del menú");
            await AlRenderizar.InvokeAsync();
        }
    }
}
