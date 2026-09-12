using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.Legal;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

public class LegalGen2Tests : BunitContext
{
    private static void ElAvisoPendienteContiene<T>(IRenderedComponent<T> cut, string fragmento) where T : IComponent
    {
        cut.FindAll(".documento-legal-pendiente").Should().ContainSingle(aviso =>
            aviso.TextContent.Contains(fragmento), $"el aviso pendiente que contiene '{fragmento}' sigue señalado como pendiente");
    }

    private static void ElIndiceApuntaAEncabezadosUnicos<T>(IRenderedComponent<T> cut, int cantidadEsperada) where T : IComponent
    {
        var enlaces = cut.FindAll(".documento-legal-indice a").ToList();
        enlaces.Should().HaveCount(cantidadEsperada, "el control positivo evita que OnlyContain acepte un índice vacío");
        // OnlyContain recibe un arbol de expresion: ni lambdas con cuerpo ni "is". Se afirma enlace a enlace.
        foreach (var destino in enlaces.Select(enlace => enlace.GetAttribute("href") ?? string.Empty))
        {
            destino.Should().MatchRegex("^#.+", "cada entrada del índice apunta a un ancla");
            cut.FindAll(destino).Should().ContainSingle("cada entrada del índice lleva a una única ancla existente ({0})", destino);
        }

        var encabezados = cut.FindAll(".documento-legal-contenido h2[id]").ToList();
        encabezados.Should().HaveCount(cantidadEsperada, "cada apartado visible debe poder recibir un enlace del índice");
        encabezados.Select(encabezado => encabezado.Id).Should().OnlyHaveUniqueItems("dos apartados no pueden compartir ancla");
    }

    [Fact]
    public void La_politica_tiene_un_indice_con_anclas_reales_y_un_enlace_a_terminos()
    {
        var cut = Render<PoliticaPrivacidad>();

        ElIndiceApuntaAEncabezadosUnicos(cut, 8);
        cut.FindAll(".documento-legal-pie a").Should().ContainSingle()
            .Which.GetAttribute("href").Should().Be("/legal/terminos");
    }

    [Fact]
    public void Los_terminos_tienen_un_indice_con_anclas_reales_y_un_enlace_a_privacidad()
    {
        var cut = Render<TerminosCondiciones>();

        ElIndiceApuntaAEncabezadosUnicos(cut, 14);
        cut.FindAll(".documento-legal-pie a").Should().ContainSingle()
            .Which.GetAttribute("href").Should().Be("/legal/privacidad");
    }

    [Fact]
    public void La_presentacion_no_inventa_una_fecha_de_actualizacion_manual()
    {
        var privacidad = Render<PoliticaPrivacidad>();
        var terminos = Render<TerminosCondiciones>();

        privacidad.Markup.Should().NotContain("Última actualización");
        terminos.Markup.Should().NotContain("Última actualización");
    }

    [Fact]
    public void Los_datos_legales_pendientes_siguen_senalados_como_pendientes()
    {
        var privacidad = Render<PoliticaPrivacidad>();
        var terminos = Render<TerminosCondiciones>();

        ElAvisoPendienteContiene(privacidad, "razón social, contacto de privacidad");
        ElAvisoPendienteContiene(privacidad, "Consultora de PRL que opera un Delegated Workspace");
        ElAvisoPendienteContiene(terminos, "encaje contractual exacto de la figura de Operador Delegado");
        ElAvisoPendienteContiene(terminos, "plazo de preaviso para oponerse a la renovación");
        ElAvisoPendienteContiene(terminos, "plazo exacto de subsistencia de la confidencialidad");
        ElAvisoPendienteContiene(terminos, "domicilio/fuero exacto para la resolución de controversias");
    }
}
