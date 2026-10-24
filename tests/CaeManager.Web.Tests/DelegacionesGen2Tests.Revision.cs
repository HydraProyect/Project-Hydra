using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class DelegacionesGen2Tests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task La_busqueda_solo_encuentra_la_contraparte_visible(bool somosLaConsultora)
    {
        _abrirDetalles = false;
        var fila = Delegacion(somosLaConsultora: somosLaConsultora) with { ClienteNombre = "Empresa Norte", ConsultoraNombre = "Organización Sur" };
        var (cut, _, _) = Renderizar(fila);
        var barra = cut.FindComponent<BarraFiltros>();
        await cut.InvokeAsync(() => barra.Instance.BusquedaChanged.InvokeAsync(somosLaConsultora ? "SUR" : "NORTE"));
        cut.FindAll(".delegaciones-fila").Should().BeEmpty();
        cut.Markup.Should().Contain("Cambia o borra los filtros").And.NotContain("Esta organización no presta ni recibe");
        await cut.InvokeAsync(() => barra.Instance.BusquedaChanged.InvokeAsync(somosLaConsultora ? "norte" : "sur"));
        cut.FindAll(".delegaciones-fila").Should().ContainSingle();
    }

    [Fact]
    public async Task Recargar_reajusta_la_pagina_si_el_total_se_reduce()
    {
        _abrirDetalles = false;
        var filas = Enumerable.Range(0, 21).Select(_ => Delegacion()).ToArray();
        var (cut, mediador, _) = Renderizar(filas);
        await cut.InvokeAsync(() => cut.FindComponent<PaginadorSimple>().Instance.PaginaActualChanged.InvokeAsync(2));
        cut.FindAll(".delegaciones-fila").Should().ContainSingle();
        cut.Find(".delegaciones-abrir").Click();
        mediador.Delegaciones = filas.Take(20).ToArray();
        BotonConTexto(cut, "Revocar acceso").Click();
        await cut.InvokeAsync(() => cut.FindComponents<DialogoConfirmacion>().Single(d => d.Instance.Visible).Instance.OnConfirmar.InvokeAsync());
        cut.FindComponent<PaginadorSimple>().Instance.PaginaActual.Should().Be(1);
        cut.FindAll(".delegaciones-fila").Should().HaveCount(20);
    }

    [Fact]
    public void No_hay_seleccion_multiple_sin_accion_en_lote()
    {
        var (cut, _, _) = Renderizar(Delegacion());
        cut.FindComponent<CabeceraListado>().Instance.SeleccionMultipleChanged.HasDelegate.Should().BeFalse();
        cut.FindAll(".delegaciones-lista input[type=checkbox]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, false, "Activa", TonoBadge.Exito, true)]
    [InlineData(false, false, "Revocada", TonoBadge.Neutro, false)]
    [InlineData(true, false, "Acceso abierto", TonoBadge.Advertencia, true)]
    [InlineData(true, true, "Caducada", TonoBadge.Neutro, true)]
    [InlineData(true, false, "Sin acceso", TonoBadge.Neutro, false)]
    public void Fila_detalle_y_franja_comparten_el_estado_y_su_tono(bool soporte, bool caducada, string estado, TonoBadge tono, bool activa)
    {
        var fila = Delegacion(soporte: soporte, activa: activa);
        if (caducada) fila = fila with { ExpiraEnUtc = DateTime.UtcNow.AddHours(-1) };
        var (cut, _, _) = Renderizar(fila);
        var etiquetas = cut.FindAll(".delegaciones-fila td");
        var detalle = cut.Find(".delegaciones-tarjeta");
        detalle.TextContent.Should().Contain(etiquetas[1].TextContent.Trim()).And.Contain(estado).And.Contain("Alta");
        detalle.TextContent.Should().NotContain("Ventana caducada").And.NotContain("Sin abrir").And.NotContain("Desde");
        cut.FindComponents<Badge>().Where(b => b.Find("span").TextContent.Trim() == estado).Should().HaveCount(2)
            .And.OnlyContain(b => b.Instance.Tono == tono);
        cut.FindComponent<FranjaEstado>().Instance.Opciones.Single(o => o.Texto == estado).Tono.Should().Be(tono);
    }

    [Theory]
    [InlineData("es-ES", 0, "0 personas autorizadas")]
    [InlineData("es-ES", 1, "1 persona autorizada")]
    [InlineData("es-ES", 2, "2 personas autorizadas")]
    [InlineData("ca-ES", 0, "0 persones autoritzades")]
    [InlineData("ca-ES", 1, "1 persona autoritzada")]
    [InlineData("ca-ES", 2, "2 persones autoritzades")]
    public void El_recuento_de_personas_usa_singular_y_plural(string idioma, int cantidad, string texto)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(idioma);
            _abrirDetalles = false;
            var fila = Delegacion();
            fila = fila with { Operadores = Enumerable.Range(0, cantidad).Select(_ => fila.Operadores[0]).ToArray() };
            var (cut, _, _) = Renderizar(fila);
            cut.FindAll(".delegaciones-fila td")[2].TextContent.Should().Be(texto);
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = anterior; }
    }

    [Fact]
    public void Un_acceso_sin_permiso_muestra_la_raya()
    {
        _abrirDetalles = false;
        var (cut, _, _) = Renderizar(Delegacion(soporte: true) with { Operadores = [] });
        cut.FindAll(".delegaciones-fila td")[2].TextContent.Should().Be("—");
    }
}
