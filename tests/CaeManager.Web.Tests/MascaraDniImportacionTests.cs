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
    [InlineData("Marta Ruiz (AB1234567)", "Marta Ruiz (****4567)")]
    [InlineData("Marta Ruiz (PA123456)", "Marta Ruiz (****3456)")]
    [InlineData("AB1234567 — Formación 20h", "****4567 — Formación 20h")]
    [InlineData("AB1234567 → Centro Norte", "****4567 → Centro Norte")]
    [InlineData("Marta Ruiz (nacida en 2024)", "Marta Ruiz (nacida en 2024)")]
    [InlineData("Plan (ver 2024)", "Plan (ver 2024)")]
    [InlineData("Marta Ruiz (AB123)", "Marta Ruiz (****B123)")]
    [InlineData("AB1234567", "****4567")]
    [InlineData("Ya existía un trabajador con el DNI AB1234567 al confirmar la importación.", "Ya existía un trabajador con el DNI ****4567 al confirmar la importación.")]
    [InlineData("El trabajador AB1234567 ya tenía un documento.", "El trabajador ****4567 ya tenía un documento.")]
    public void Sin_pedir_el_DNI_completo_sale_enmascarado(string original, string esperado) =>
        MascaraDni.Aplicar(original, completo: false).Should().Be(esperado);

    [Theory]
    [InlineData("12.345.678-Z", "****678Z")]
    [InlineData("12 345 678 Z", "****678Z")]
    [InlineData("12-345-678-Z", "****678Z")]
    [InlineData("12/345/678/Z", "****678Z")]
    [InlineData("12345678-Z", "****678Z")]
    [InlineData("12.345.678 z", "****678z")]
    [InlineData("12.345.678 - Z", "****678Z")]
    [InlineData("12 345 678 Z", "****678Z")]
    [InlineData("X-1234567-L", "****567L")]
    [InlineData("X 1234567 L", "****567L")]
    [InlineData("Y.1.234.567/B", "****567B")]
    [InlineData("z-1234567-k", "****567k")]
    [InlineData("Marta Ruiz (12.345.678-Z)", "Marta Ruiz (****678Z)")]
    [InlineData("12 345 678 Z — Formación 20h", "****678Z — Formación 20h")]
    [InlineData("Observaciones: DNI 12.345.678-Z y NIE X-1234567-L, llamar.", "Observaciones: DNI ****678Z y NIE ****567L, llamar.")]
    [InlineData("DNI 12 345 678 Z\ny otro 87.654.321-X.", "DNI ****678Z\ny otro ****321X.")]
    public void Un_DNI_o_NIE_escrito_con_separadores_sale_enmascarado_con_la_misma_forma(string original, string esperado) =>
        MascaraDni.Aplicar(original, completo: false).Should().Be(esperado);

    [Theory]
    [InlineData("Llamar al 91 234 56 78 mañana")]
    [InlineData("Teléfono 612 345 678")]
    [InlineData("Teléfono 612-345-678 Z")]
    [InlineData("Importe 12.345.678 euros")]
    [InlineData("Importe 12.345.678")]
    [InlineData("Del 12/11/2026 y 13/11/2026")]
    [InlineData("Del 12-11-2026 a las 10:00")]
    [InlineData("Desde 2026-11-12 y hasta 2026-11-30")]
    [InlineData("Planta Norte 2026, nave 12-B")]
    public void Un_telefono_un_importe_o_una_fecha_con_separadores_no_se_enmascara(string texto) =>
        MascaraDni.Aplicar(texto, completo: false).Should().Be(texto);

    [Fact]
    public void El_Administrador_ve_completo_el_DNI_con_separadores_en_pantalla() =>
        MascaraDni.Aplicar("DNI 12.345.678-Z", completo: true).Should().Be("DNI 12.345.678-Z");

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
