using System.Linq;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Retirar» a una persona de una delegación le quita el acceso y no se
/// deshace desde la pantalla: antes salía con un solo clic. Ahora pasa por
/// <see cref="CaeManager.Web.Components.DesignSystem.DialogoConfirmacion"/>.
///
/// <para>
/// <b>Lo que SÍ observa:</b> si <see cref="RevocarAsignacionOperadorDelegadoCommand"/>
/// llega al mediador, y con qué asignación, según se cancele o se confirme.
/// <b>Lo que NO observa:</b> la autorización del Command (Application.Tests) ni
/// el doble clic dentro del diálogo (<c>DialogoConfirmacionTests</c>).
/// </para>
/// </summary>
public partial class DelegacionesGen2Tests
{
    private static AngleSharp.Dom.IElement BotonDelDialogo(IRenderedComponent<CaeManager.Web.Features.Delegaciones.Pages.Delegaciones> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto);

    [Fact]
    public async Task Retirar_a_una_persona_pide_confirmacion_y_cancelar_no_envia_el_Command()
    {
        var delegacion = Delegacion();
        var (cut, mediador, _) = Renderizar(delegacion);

        await BotonConTexto(cut, "Retirar").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-pie").Should().NotBeNull("control positivo: el diálogo se abrió");
        cut.Markup.Should().Contain("no se puede deshacer");
        mediador.Enviadas.Select(e => e.Peticion).OfType<RevocarAsignacionOperadorDelegadoCommand>()
            .Should().BeEmpty("abrir el diálogo no retira a nadie");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(e => e.Peticion).OfType<RevocarAsignacionOperadorDelegadoCommand>().Should().BeEmpty();
        cut.FindAll(".modal-pie").Should().BeEmpty("cancelar cierra el diálogo");
    }

    [Fact]
    public async Task Retirar_a_una_persona_y_confirmar_envia_el_Command_de_esa_asignacion()
    {
        var delegacion = Delegacion();
        var (cut, mediador, toasts) = Renderizar(delegacion);

        await BotonConTexto(cut, "Retirar").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Retirar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.Select(e => e.Peticion).OfType<RevocarAsignacionOperadorDelegadoCommand>()
            .Should().ContainSingle()
            .Which.AsignacionId.Should().Be(delegacion.Operadores.Single().AsignacionId);
        toasts.Mensajes.Should().Contain(t => t.Mensaje == "Persona retirada de la delegación.");
        cut.FindAll(".modal-pie").Should().BeEmpty();
    }
}
