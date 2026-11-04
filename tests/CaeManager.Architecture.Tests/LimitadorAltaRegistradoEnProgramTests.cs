using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Cableado del antiabuso del alta autónoma en <c>Program.cs</c>: la política <c>alta</c> reparte con
/// <c>LimitadorAlta.Particion</c> y las opciones configuradas, y los servicios del reto se registran. El comportamiento
/// lo prueban <c>LimitadorAltaTests</c> y vecinos en Web.Tests; aquí se vigila que nadie lo desenchufe, sobre el código
/// sin comentarios (el proyecto no tiene un <c>WebApplicationFactory</c> que lo observe).
/// </summary>
public class LimitadorAltaRegistradoEnProgramTests
{
    [Fact]
    public void La_politica_alta_reparte_con_LimitadorAlta_y_las_opciones_configuradas()
    {
        var politica = Regex.Match(ProgramSinComentarios(),
            @"AddPolicy\([\w.]*LimitadorAlta\.Politica,.*?\);", RegexOptions.Singleline);

        politica.Success.Should().BeTrue("la política alta debe registrarse con la constante de LimitadorAlta");
        politica.Value.Should().Contain("LimitadorAlta.Particion(")
            .And.Contain("IOptions<CaeManager.Web.Features.AltaAutonoma.Antiabuso.OpcionesAntiabusoAlta>")
            .And.NotContain(".User");
    }

    [Fact]
    public void Program_registra_los_servicios_antiabuso_del_alta()
    {
        ProgramSinComentarios().Should().MatchRegex(@"AddAntiabusoAlta\(\s*builder\.Services\s*,\s*builder\.Configuration\s*\)");
    }

    private static string ProgramSinComentarios()
    {
        var codigo = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        codigo = Regex.Replace(codigo, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(codigo, @"(?<!:)//[^\n]*", string.Empty);
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
