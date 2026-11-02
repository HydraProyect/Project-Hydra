using Bunit;
using CaeManager.Application.Importacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Importacion;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Decisión provisional (2026-11-02, pendiente de Chris): el DNI completo del plan de importación solo lo ve el
/// Administrador (decisión S3, 2026-09-24); Dirección CAE y Coordinador CAE, que cargan desde el 2026-11-01, lo ven
/// enmascarado. Falla cerrado: sin pedir el DNI completo, sale enmascarado.
/// </summary>
public class MascaraDniImportacionTests : BunitContext
{
    [Theory]
    [InlineData("12345678Z", "****678Z")]
    [InlineData("X1234567L", "****567L")]
    [InlineData("Marta Ruiz (12345678Z)", "Marta Ruiz (****678Z)")]
    [InlineData("12345678Z — Formación 20h", "****678Z — Formación 20h")]
    [InlineData("El trabajador con DNI 12345678z no existe.", "El trabajador con DNI ****678z no existe.")]
    public void Sin_pedir_el_DNI_completo_sale_enmascarado(string original, string esperado) =>
        MascaraDni.Aplicar(original, completo: false).Should().Be(esperado);


    [Theory]
    [InlineData("12345678", "****5678")]
    [InlineData("AB1234567", "****4567")]
    [InlineData("12345678Z", "****678Z")]
    [InlineData("123", "****")]
    public void Un_campo_de_documento_se_enmascara_entero_aunque_no_sea_un_DNI_valido(string documento, string esperado) =>
        MascaraDni.AplicarDocumento(documento, completo: false).Should().Be(esperado);

    [Fact]
    public void Un_campo_de_documento_completo_no_cambia_para_el_Administrador()
    {
        MascaraDni.AplicarDocumento("AB1234567", completo: true).Should().Be("AB1234567");
        MascaraDni.Aplicar("DNI sin letra 12345678 en la fila", completo: false).Should().Be("DNI sin letra ****5678 en la fila");
    }

    [Fact]
    public void El_Administrador_ve_el_DNI_completo_y_el_texto_sin_DNI_no_cambia()
    {
        MascaraDni.Aplicar("Marta (12345678Z)", completo: true).Should().Be("Marta (12345678Z)");
        MascaraDni.Aplicar("Planta Norte 2026", completo: false).Should().Be("Planta Norte 2026");
        MascaraDni.Aplicar(null, completo: false).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, "****678Z")]
    [InlineData(true, "12345678Z")]
    public void La_tabla_de_avisos_y_omitidos_enmascara_Descripcion_y_Motivo_salvo_para_el_Administrador(bool dniCompleto, string visible)
    {
        Services.AddLocalization();
        var items = new[] { new ItemImportacionDto("Empleados", 3, "12345678Z — Formación", "El trabajador 12345678Z no existe.") };

        var cut = Render<TablaItemsImportacion>(p => p
            .Add(c => c.Items, items).Add(c => c.Tono, TonoBadge.Peligro).Add(c => c.DniCompleto, dniCompleto));

        cut.Markup.Should().Contain(visible);
        if (!dniCompleto) cut.Markup.Should().NotContain("12345678Z");
    }

    [Fact]
    public void La_tabla_sin_indicar_nada_enmascara()
    {
        Services.AddLocalization();
        var cut = Render<TablaItemsImportacion>(p => p
            .Add(c => c.Items, new[] { new ItemImportacionDto("Empleados", 1, "12345678Z", "motivo") })
            .Add(c => c.Tono, TonoBadge.Peligro));

        cut.Markup.Should().NotContain("12345678Z");
    }
}
