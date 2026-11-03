using System;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Mapeo ruta → lista tras cambiar de Tenant en el selector de la cabecera (contrato del selector,
/// § 4.4): desde una ficha por Id se vuelve a la lista de ese árbol; desde cualquier otra ruta, a la
/// misma ruta sin consulta.
/// </summary>
public class RutaTrasCambioDeTenantTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    [Theory]
    [InlineData("/trabajadores/" + Id, "/trabajadores")]
    [InlineData("/empresas/" + Id, "/empresas")]
    [InlineData("/centros/" + Id, "/centros")]
    [InlineData("/clientes/" + Id, "/clientes")]
    [InlineData("/proyectos/" + Id, "/proyectos")]
    [InlineData("/subcontratas/" + Id, "/subcontratas")]
    [InlineData("/vehiculos/" + Id, "/vehiculos")]
    [InlineData("/visitas/" + Id, "/visitas")]
    [InlineData("/trabajadores/" + Id + "/", "/trabajadores")]
    [InlineData("/clientes/" + Id + "/lectura-ia", "/clientes")]
    [InlineData("/empresas/" + Id + "/deteccion-trabajadores", "/empresas")]
    [InlineData("/documentos/tipos/" + Id, "/documentos")]
    [InlineData("/plantillas/" + Id + "/editar", "/plantillas")]
    [InlineData("/trabajadores/" + Id + "?ctx=Trabajador:" + Id + ":resumen&q=x", "/trabajadores")]
    [InlineData("/TRABAJADORES/" + "11111111-2222-3333-4444-55555555AAAA", "/TRABAJADORES")]
    public void Desde_una_ficha_por_Id_vuelve_a_la_lista_de_ese_arbol(string camino, string lista) =>
        RutaTrasCambioDeTenant.Calcular(camino).Should().Be(lista);

    [Theory]
    [InlineData("/trabajadores", "/trabajadores")]
    [InlineData("/trabajadores?q=ana&empresaId=" + Id, "/trabajadores")]
    [InlineData("/trabajadores/no-es-un-guid", "/trabajadores/no-es-un-guid")]
    [InlineData("/configuracion/usuarios", "/configuracion/usuarios")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    [InlineData("/" + Id, "/")]
    [InlineData("/plataforma/incidentes/" + Id, "/")]
    [InlineData("/plataforma/conectores-cae", "/plataforma/conectores-cae")]
    public void Fuera_de_una_ficha_por_Id_conserva_la_ruta_sin_consulta(string? camino, string esperado) =>
        RutaTrasCambioDeTenant.Calcular(camino).Should().Be(esperado);

    /// <summary>
    /// Toda página con un Id en la ruta, también las que se añadan después, aterriza tras el cambio en
    /// una página que existe y que no lleva Id: nunca en un 404 ni en otra ficha del Tenant anterior.
    /// </summary>
    [Fact]
    public void Toda_ruta_con_Id_aterriza_en_una_pagina_registrada_sin_parametros()
    {
        var plantillas = typeof(RutaTrasCambioDeTenant).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: false))
            .Select(r => r.Template)
            .ToList();
        var sinParametros = plantillas.Where(p => !p.Contains('{')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conId = plantillas.Where(p => p.Contains(":guid}", StringComparison.OrdinalIgnoreCase)).ToList();

        conId.Should().NotBeEmpty("si no hay fichas por Id, el test no mide nada");
        foreach (var plantilla in conId)
        {
            var camino = Regex.Replace(plantilla, @"\{[^}]*\}", Id);
            var destino = RutaTrasCambioDeTenant.Calcular(camino);

            sinParametros.Should().Contain(destino, $"«{plantilla}» debe volver a una lista que exista tras cambiar de Tenant");
        }
    }
}
