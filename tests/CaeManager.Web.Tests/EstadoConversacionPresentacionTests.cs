using System.Globalization;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Recursos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cómo se ve cada <see cref="EstadoConversacion"/> en la bandeja (hueco 2 de Comunicaciones, 2026-10-17): texto en los dos idiomas
/// para cada valor del enum —un estado nuevo sin recurso enseñaría la clave tal cual—, tono del chip y estados que ofrece el
/// selector «Cambiar estado…», que salen de la misma máquina de estados que valida el dominio.
/// </summary>
public class EstadoConversacionPresentacionTests
{
    private static IStringLocalizer<TextosEstadosConversacion> Textos()
    {
        var servicios = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        return servicios.GetRequiredService<IStringLocalizer<TextosEstadosConversacion>>();
    }

    private static T EnCultura<T>(string cultura, Func<T> accion)
    {
        var antes = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(cultura);
            return accion();
        }
        finally
        {
            CultureInfo.CurrentUICulture = antes;
        }
    }

    [Theory]
    [InlineData(EstadoConversacion.Abierta, "Abierta", "Oberta")]
    [InlineData(EstadoConversacion.Pendiente, "Pendiente", "Pendent")]
    [InlineData(EstadoConversacion.EsperandoAlCliente, "Esperando al Cliente", "Esperant el Client")]
    [InlineData(EstadoConversacion.EnEsperaDeTercero, "En espera de tercero", "En espera d'un tercer")]
    [InlineData(EstadoConversacion.Resuelta, "Resuelta", "Resolta")]
    [InlineData(EstadoConversacion.Cerrada, "Cerrada", "Tancada")]
    public void Cada_estado_tiene_su_texto_en_castellano_y_en_catalan(EstadoConversacion estado, string es, string ca)
    {
        var textos = Textos();

        EnCultura("es-ES", () => EstadoConversacionPresentacion.Etiqueta(textos, estado)).Should().Be(es);
        EnCultura("ca-ES", () => EstadoConversacionPresentacion.Etiqueta(textos, estado)).Should().Be(ca);
    }

    [Fact]
    public void Ningun_valor_del_enum_se_queda_sin_texto_ni_sin_sitio_en_el_selector()
    {
        var textos = Textos();

        foreach (var estado in Enum.GetValues<EstadoConversacion>())
        {
            textos[estado.ToString()].ResourceNotFound.Should().BeFalse($"falta el texto de {estado}");
            EstadoConversacionPresentacion.EnOrdenDeSelector.Should().Contain(estado);
        }
        EstadoConversacionPresentacion.EnOrdenDeSelector.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(EstadoConversacion.Abierta, TonoBadge.Info)]
    [InlineData(EstadoConversacion.Pendiente, TonoBadge.Info)]
    [InlineData(EstadoConversacion.EsperandoAlCliente, TonoBadge.Advertencia)]
    [InlineData(EstadoConversacion.EnEsperaDeTercero, TonoBadge.Advertencia)]
    [InlineData(EstadoConversacion.Resuelta, TonoBadge.Neutro)]
    [InlineData(EstadoConversacion.Cerrada, TonoBadge.Neutro)]
    public void El_tono_del_chip_usa_los_tonos_del_sistema_de_diseno(EstadoConversacion estado, TonoBadge esperado) =>
        EstadoConversacionPresentacion.Tono(estado).Should().Be(esperado);

    [Fact]
    public void El_selector_ofrece_exactamente_las_transiciones_permitidas_en_el_orden_de_la_bandeja()
    {
        EstadoConversacionPresentacion.Siguientes(EstadoConversacion.Abierta).Should().Equal(
            EstadoConversacion.Pendiente, EstadoConversacion.EsperandoAlCliente, EstadoConversacion.EnEsperaDeTercero,
            EstadoConversacion.Resuelta, EstadoConversacion.Cerrada);
        EstadoConversacionPresentacion.Siguientes(EstadoConversacion.EsperandoAlCliente).Should().Equal(
            EstadoConversacion.Abierta, EstadoConversacion.Pendiente, EstadoConversacion.EnEsperaDeTercero,
            EstadoConversacion.Resuelta, EstadoConversacion.Cerrada);
        EstadoConversacionPresentacion.Siguientes(EstadoConversacion.Resuelta).Should().Equal(
            EstadoConversacion.Abierta, EstadoConversacion.Cerrada);
        EstadoConversacionPresentacion.Siguientes(EstadoConversacion.Cerrada).Should().Equal(EstadoConversacion.Abierta);
    }
}
