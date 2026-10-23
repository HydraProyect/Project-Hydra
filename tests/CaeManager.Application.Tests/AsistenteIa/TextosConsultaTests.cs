using System.Collections;
using System.Globalization;
using System.Resources;
using CaeManager.Application.AsistenteIa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.AsistenteIa;

/// <summary>
/// <c>TextosConsulta.Obtener</c> hace <c>GetString(...)!</c>: una clave que falte en un idioma devolvería
/// null y reventaría más tarde, lejos de la causa. Aquí se cargan todas las claves del recurso neutro en
/// cada cultura.
/// </summary>
public class TextosConsultaTests
{
    private static readonly string[] Culturas = ["es-ES", "ca-ES"];

    private static string[] TodasLasClaves()
    {
        var recursos = new ResourceManager("CaeManager.Application.AsistenteIa.TextosConsulta", typeof(TextosConsulta).Assembly);
        var neutro = recursos.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false)!;
        return neutro.Cast<DictionaryEntry>().Select(e => (string)e.Key).OrderBy(k => k, StringComparer.Ordinal).ToArray();
    }

    private static string Obtener(string clave, string cultura)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultura);
            return TextosConsulta.Obtener(clave);
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }

    [Fact]
    public void El_recurso_tiene_las_claves_que_usa_el_codigo()
    {
        TodasLasClaves().Should().Contain(["PreguntaInvalida", "PreguntaDemasiadoLarga", "SinIdentidad", "CuotaUsuario", "CuotaTenant"]);
    }

    [Fact]
    public void Todas_las_claves_existen_en_es_ES_y_ca_ES_y_el_catalan_difiere_del_castellano()
    {
        foreach (var clave in TodasLasClaves())
        {
            var es = Obtener(clave, "es-ES");
            var ca = Obtener(clave, "ca-ES");

            es.Should().NotBeNullOrWhiteSpace($"la clave {clave} en es-ES");
            ca.Should().NotBeNullOrWhiteSpace($"la clave {clave} en ca-ES");
            ca.Should().NotBe(es, $"la clave {clave} debe estar traducida al catalán");
        }
    }

    [Fact]
    public void Las_claves_con_marcador_conservan_el_mismo_marcador_en_cada_cultura()
    {
        foreach (var clave in TodasLasClaves())
            Culturas.Select(c => Obtener(clave, c).Contains("{0}")).Distinct().Should().ContainSingle($"la clave {clave}");
    }
}
