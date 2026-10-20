using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// Aviso del documento en el alta de Trabajador (decisión de Chris, 2026-10-20): lo que no es DNI ni NIE se guardará como
/// Pasaporte y el formulario lo dice antes de guardar; un DNI válido no avisa.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private static async Task EscribirDocumentoDelAltaAsync(IRenderedComponent<Trabajadores> cut, string valor)
    {
        var campo = PanelDeAlta(cut).FindComponents<CampoTexto>()
            .Single(c => c.Instance.Etiqueta is { } e && (e.Contains("DNI") || e.Contains("ocumento")));
        var input = campo.Find("input");
        await input.InputAsync(new ChangeEventArgs { Value = valor });
        await campo.Find("input").BlurAsync(new Microsoft.AspNetCore.Components.Web.FocusEventArgs());
    }

    [Theory]
    [InlineData("12345678")]
    [InlineData("AB1234567")]
    [InlineData("ABC123456")]
    public async Task Lo_que_no_es_DNI_ni_NIE_avisa_de_que_se_guardara_como_Pasaporte(string documento)
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        await EscribirDocumentoDelAltaAsync(cut, documento);

        cut.Find("[data-pista='documento']").TextContent.Should()
            .Contain("No coincide con un DNI ni un NIE: se guardará como Pasaporte");
    }

    [Theory]
    [InlineData("12345678Z")]
    [InlineData("X1234567L")]
    public async Task Un_DNI_o_un_NIE_validos_no_avisan(string documento)
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        await EscribirDocumentoDelAltaAsync(cut, documento);

        cut.Find("[data-pista='documento']").TextContent.Should().NotContain("Pasaporte");
    }
}