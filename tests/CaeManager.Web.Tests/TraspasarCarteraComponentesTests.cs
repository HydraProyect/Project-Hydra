using Bunit;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Operaciones.Traspasos;
using CaeManager.Web.Features.CarterasGestores.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

public class TraspasarCarteraComponentesTests : BunitContext
{
    private readonly Guid _gestor = Guid.NewGuid(), _destino = Guid.NewGuid();
    private readonly Guid _op1 = Guid.NewGuid(), _op2 = Guid.NewGuid();
    public TraspasarCarteraComponentesTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLocalization(); }
    private IRenderedComponent<DialogoTraspasarCartera> Abrir(Action<TraspasarCarteraCommand> confirmar) => Render<DialogoTraspasarCartera>(p => p
        .Add(c => c.Visible, true).Add(c => c.GestorUsuarioId, _gestor).Add(c => c.Nombre, "Marta")
        .Add(c => c.Empresas, new List<CarteraVivaDeOperacion> { new(_op1, Guid.NewGuid(), "Empresa Uno", _gestor, "GestorCae", true, null), new(_op2, Guid.NewGuid(), "Empresa Dos", _gestor, "GestorCae", false, null) })
        .Add(c => c.Destinatarios, new List<GestorDeCartera> { new(_destino, "Lucía", true) })
        .Add(c => c.AlConfirmar, confirmar));

    [Fact]
    public void No_confirma_hasta_elegir_destinatario_para_todas_las_Empresas()
    {
        TraspasarCarteraCommand? enviado = null; var cut = Abrir(c => enviado = c);
        cut.Find("button.confirmar").HasAttribute("disabled").Should().BeTrue();
        cut.Find("select[aria-label='Gestor CAE para Empresa Uno']").Change(_destino.ToString());
        cut.Find("button.confirmar").HasAttribute("disabled").Should().BeTrue();
        enviado.Should().BeNull();
        cut.Find("select[aria-label='Gestor CAE para Empresa Dos']").Change(_destino.ToString());
        cut.Find("button.confirmar").Click();
        enviado.Should().NotBeNull();
        enviado!.Destinos.Select(d => d.AsignacionOperacionId).Should().Equal(_op1, _op2);
        enviado.Destinos.Should().OnlyContain(d => d.DestinatarioUsuarioId == _destino);
    }

    [Fact]
    public void Todas_a_solo_prepara_el_traspaso_y_el_error_conserva_las_selecciones()
    {
        TraspasarCarteraCommand? enviado = null; var cut = Abrir(c => enviado = c);
        cut.Find("select[aria-label='Todas a']").Change(_destino.ToString());
        enviado.Should().BeNull();
        cut.Render(p => p.Add(c => c.Error, "No se ha cambiado nada; vuelve a intentarlo."));
        cut.Find("[role=dialog]").Should().NotBeNull();
        cut.Find("[role=alert]").TextContent.Should().Contain("vuelve a intentarlo");
        cut.Find("button.confirmar").HasAttribute("disabled").Should().BeFalse();
        cut.Find("button.confirmar").Click();
        enviado.Should().NotBeNull();
    }

    [Fact]
    public void Guardando_impide_doble_envio_y_cancelacion()
    {
        var envios = 0; var cut = Abrir(_ => envios++);
        cut.Find("select[aria-label='Todas a']").Change(_destino.ToString());
        cut.Render(p => p.Add(c => c.Guardando, true));
        cut.FindAll("select").Should().OnlyContain(s => s.HasAttribute("disabled"));
        cut.Find("button.confirmar").Click();
        envios.Should().Be(0);
    }
}
