using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La forma de la vista unificada de Comunicaciones entre Tenants (D-6 b, § 4.6 y § 7.3 del diseño en
/// Project-Hydra-Negocio/tecnico/DISENO-D6B-COMUNICACIONES-MULTITENANT-2026-11-17.md).
/// <list type="bullet">
/// <item><see cref="AmbitoTenantAutorizado"/> e <see cref="IdentidadVistaUnificada"/> solo nacen por su fábrica: sin
/// constructor accesible, sin <c>set</c> ni <c>init</c>, no <c>record</c> ni <c>struct</c>, y nunca serializables.</item>
/// <item>La marca <see cref="AmbitoSinViaHeredada"/> solo se abre desde Application (<c>Marcar</c> es <c>internal</c>).</item>
/// <item>Dentro de la carpeta de la vista, solo <c>AmbitoTenantAutorizado.cs</c> abre un <c>AmbitoTenantExplicito</c>, y
/// ningún fichero suma la vía heredada (<c>ObtenerClientesAutorizadosQuery</c>, <c>EstaAutorizadoAsync</c>).</item>
/// </list>
/// </summary>
public class VistaUnificadaComunicacionesTests
{
    private const BindingFlags Todos =
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(typeof(AmbitoTenantAutorizado))]
    [InlineData(typeof(IdentidadVistaUnificada))]
    public void El_tipo_solo_nace_por_su_fabrica(Type tipo)
    {
        tipo.IsSealed.Should().BeTrue();
        tipo.IsClass.Should().BeTrue();
        tipo.IsValueType.Should().BeFalse();
        tipo.GetMethod("<Clone>$", Todos).Should().BeNull("un record se copia con with y saltaría la fábrica");
        tipo.GetCustomAttributes(inherit: true)
            .Select(a => a.GetType().Name)
            .Should().NotContain(n => n.Contains("Serializ") || n.Contains("DataContract") || n.Contains("Json"));

        tipo.GetConstructors(Todos)
            .Should().OnlyContain(c => c.IsPrivate, "fuera del tipo no se puede construir un ámbito sin re-comprobar");

        tipo.GetProperties(Todos)
            .Where(p => p.SetMethod is not null)
            .Select(p => p.Name)
            .Should().BeEmpty("ninguna propiedad se asigna después de la fábrica");
    }

    [Fact]
    public void La_marca_sin_via_heredada_solo_se_abre_desde_Application()
    {
        var marcar = typeof(AmbitoSinViaHeredada).GetMethod("Marcar", Todos);
        marcar.Should().NotBeNull();
        marcar!.IsAssembly.Should().BeTrue("Marcar es internal: solo AmbitoTenantAutorizado la abre");
        typeof(AmbitoSinViaHeredada).GetProperty(nameof(AmbitoSinViaHeredada.Activo))!.SetMethod.Should().BeNull();
    }

    [Fact]
    public void Solo_AmbitoTenantAutorizado_abre_un_ambito_y_nadie_suma_la_via_heredada()
    {
        var carpeta = Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Application", "Comunicaciones", "VistaUnificada");
        var ficheros = Directory.GetFiles(carpeta, "*.cs", SearchOption.AllDirectories);
        ficheros.Should().Contain(f => Path.GetFileName(f) == "AmbitoTenantAutorizado.cs",
            "el instrumento tiene que ver la carpeta de la vista");

        foreach (var fichero in ficheros)
        {
            var codigo = SinComentarios(File.ReadAllText(fichero));
            var nombre = Path.GetFileName(fichero);

            if (nombre != "AmbitoTenantAutorizado.cs")
            {
                codigo.Should().NotContain("AmbitoTenantExplicito.Establecer", $"{nombre} no abre ámbitos");
                codigo.Should().NotContain("AmbitoSinViaHeredada.Marcar", $"{nombre} no abre la marca");
            }

            codigo.Should().NotContain("ObtenerClientesAutorizadosQuery", $"{nombre} no suma la vía heredada (DN-2 (a))");
            codigo.Should().NotContain("EstaAutorizadoAsync", $"{nombre} no suma la vía heredada (DN-2 (a))");
        }
    }

    [Fact]
    public void El_instrumento_de_comentarios_no_borra_codigo()
    {
        SinComentarios("var a = 1; // AmbitoTenantExplicito.Establecer(x)\n/* EstaAutorizadoAsync */ var b = 2;")
            .Should().NotContain("Establecer").And.NotContain("EstaAutorizadoAsync").And.Contain("var b = 2;");
        SinComentarios("AmbitoTenantExplicito.Establecer(t);").Should().Contain("AmbitoTenantExplicito.Establecer");
    }

    private static string SinComentarios(string codigo) =>
        Regex.Replace(codigo, @"/\*.*?\*/|//[^\n]*", string.Empty, RegexOptions.Singleline);

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;
        return actual?.FullName
               ?? throw new InvalidOperationException("No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
