using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>Program.cs</c> envuelve el desafío de la cookie de sesión para que la ruta de un POST no
/// se guarde como <c>ReturnUrl</c> (LV-2). Sin la llamada, una sesión caducada seguida del envío
/// de un formulario —cambiar de Tenant activo, cerrar sesión— devuelve al usuario, tras volver a
/// entrar, a un endpoint que solo admite POST: un 405. El comportamiento de
/// <c>OmitirReturnUrlEnPeticionesNoNavegables.Configurar</c> lo prueba
/// <c>OmitirReturnUrlEnPeticionesNoNavegablesTests</c> en Web.Tests; este trinquete solo vigila
/// que <c>Program.cs</c> lo llame dentro de <c>ConfigureApplicationCookie</c>, porque el
/// proyecto no tiene un <c>WebApplicationFactory</c> que lo observe.
/// </summary>
public class ReturnUrlSoloNavegableRegistradoEnProgramTests
{
    private const string Llamada = "OmitirReturnUrlEnPeticionesNoNavegables.Configurar(options);";

    [Fact]
    public void Program_envuelve_el_desafio_de_la_cookie_de_sesion_dentro_de_ConfigureApplicationCookie()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        // El cuerpo del delegado: desde su apertura hasta el cierre «});» en la columna cero
        // de sangría. La llamada en cualquier otro punto de Program.cs no configuraría la
        // cookie de sesión de Identity.
        var bloque = Regex.Match(
            program,
            @"ConfigureApplicationCookie\(options =>\r?\n\{(?<cuerpo>.*?)\r?\n\}\);",
            RegexOptions.Singleline);

        bloque.Success.Should().BeTrue(
            "el trinquete tiene que encontrar el bloque ConfigureApplicationCookie para poder afirmar nada sobre él");
        bloque.Groups["cuerpo"].Value.Should().Contain("options.LoginPath",
            "control positivo: el bloque encontrado es el que configura la cookie de sesión");

        var lineasActivas = bloque.Groups["cuerpo"].Value
            .Split('\n')
            .Select(linea => linea.Trim())
            .Where(linea => !linea.StartsWith("//", StringComparison.Ordinal));

        lineasActivas.Should().Contain(Llamada,
            "sin esa llamada el POST de un formulario con la sesión caducada vuelve a guardar su ruta como ReturnUrl");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
