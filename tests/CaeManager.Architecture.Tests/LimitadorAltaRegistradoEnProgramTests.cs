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
        // PoliticaLimitadorAlta reparte con LimitadorAlta.Particion y las opciones configuradas, y lleva el rechazo que
        // apaga las páginas de estado; su comportamiento lo prueba LimitadorAltaTests sobre el pipeline real.
        var politica = Regex.Match(ProgramSinComentarios(),
            @"AddPolicy<string,\s*[\w.]*\bPoliticaLimitadorAlta>\(\s*[\w.]*LimitadorAlta\.Politica\s*\);", RegexOptions.Singleline);

        politica.Success.Should().BeTrue("la política alta debe registrarse con PoliticaLimitadorAlta y la constante de LimitadorAlta");
    }

    [Fact]
    public void La_politica_alta_no_se_registra_ademas_con_un_repartidor_suelto()
    {
        // Un AddPolicy(LimitadorAlta.Politica, contexto => ...) perdería el rechazo propio y el 429 volvería a reejecutarse.
        Regex.Matches(ProgramSinComentarios(), @"LimitadorAlta\.Politica\b").Count.Should().Be(1);
    }

    [Fact]
    public void El_POST_de_verificacion_del_correo_del_alta_tiene_techo_por_IP()
    {
        // Cada confirmación abre conexión con contexto firmado y bloquea una fila (app_alta_verificar_correo): sin techo,
        // un POST anónimo ilimitado martillea la base. El limitador global lo parte por IP con el techo anónimo de /cuenta.
        var bloque = Regex.Match(ProgramSinComentarios(),
            @"if\s*\(\s*HttpMethods\.IsPost\(contexto\.Request\.Method\)\s*&&\s*contexto\.Request\.Path\.StartsWithSegments\(""/alta/verificar""\)\s*\)\s*" +
            @"return\s+System\.Threading\.RateLimiting\.RateLimitPartition\.GetFixedWindowLimiter\(\s*\$""alta-verificar:\{contexto\.Connection\.RemoteIpAddress.*?PermitLimit\s*=\s*limiteCuentaAnonimo,",
            RegexOptions.Singleline);

        bloque.Success.Should().BeTrue("el POST de /alta/verificar debe tener su partición por IP en el limitador global");
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
