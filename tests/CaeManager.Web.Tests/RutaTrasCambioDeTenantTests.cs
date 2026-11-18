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
    [InlineData("/inbound/validacion/" + Id, "/inbound/validacion")]
    [InlineData("/inbound/contratos/" + Id, "/inbound/contratos")]
    [InlineData("/inbound/requerimientos/" + Id + "/subir", "/inbound/requerimientos")]
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
    /// D-4: con papel Inbound solo se conserva la ruta de una página que se abre con papel Inbound (las de Inbound y las
    /// marcadas como abiertas); las del Núcleo y las de Outbound, y las rutas que no son de una página, vuelven al inicio.
    /// Sin papel o con Outbound, la ruta de siempre.
    /// </summary>
    [Theory]
    [InlineData("/trabajadores", "Inbound", "/")]
    [InlineData("/trabajadores/" + Id, "Inbound", "/")]
    [InlineData("/configuracion/usuarios", "Inbound", "/")]
    [InlineData("/clientes", "Inbound", "/")]
    [InlineData("/trabajadores/no-es-un-guid", "Inbound", "/")]
    [InlineData("/inbound/contratos", "Inbound", "/inbound/contratos")]
    [InlineData("/inbound/contratos/" + Id, "Inbound", "/inbound/contratos")]
    [InlineData("/", "Inbound", "/")]
    [InlineData("/trabajadores", "Outbound", "/trabajadores")]
    [InlineData("/trabajadores/" + Id, null, "/trabajadores")]
    [InlineData("/inbound/contratos", "Outbound", "/inbound/contratos")]
    public void Con_papel_Inbound_solo_conserva_una_pagina_que_se_abre_con_Inbound(string camino, string? papel, string esperado) =>
        RutaTrasCambioDeTenant.Calcular(camino, papel is null ? null : Enum.Parse<CaeManager.Domain.Operaciones.ServicioCae>(papel))
            .Should().Be(esperado);

    /// <summary>
    /// Instrumento: la tabla de páginas que lee <see cref="RutaTrasCambioDeTenant.Calcular(string?, CaeManager.Domain.Operaciones.ServicioCae?)"/>
    /// tiene que conocer el inicio y una página de Inbound, o el test de arriba pasaría devolviendo siempre «/».
    /// </summary>
    [Fact]
    public void La_tabla_de_paginas_reconoce_el_inicio_y_las_de_Inbound_como_abiertas_con_Inbound()
    {
        RutaTrasCambioDeTenant.SeAbreConPapelInbound("/").Should().BeTrue();
        RutaTrasCambioDeTenant.SeAbreConPapelInbound("/inbound/contratos").Should().BeTrue();
        RutaTrasCambioDeTenant.SeAbreConPapelInbound("/trabajadores").Should().BeFalse();
    }

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
