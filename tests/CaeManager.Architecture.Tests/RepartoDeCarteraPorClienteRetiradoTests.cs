using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>El reparto de la Asignación de Cartera por Cliente empresarial está retirado</b> (D-7, decisión del
/// propietario del 2026-10-02; enmienda 2026-09-23 de ADR-011 § 2.7: la cartera de un Gestor CAE es siempre
/// sobre el Tenant entero). Nada en el código de producción vuelve a escribir una cartera con ámbito de
/// relación de Cliente, y nada deriva alcance de <c>Empresa.EjecutivoUsuarioId</c>, que es una referencia
/// (enrutado de WhatsApp, avisos, columna de la lista) y no una concesión.
///
/// <para>
/// <b>Qué observa.</b> Identificadores reales del árbol sintáctico de C#: un comentario o un literal que
/// nombre el identificador no cuenta, uno que lo use sí. Cada comprobación lleva su control positivo (el
/// mismo instrumento ve el identificador donde sí existe), para que un escaneo que dejara de mirar no
/// pasara por verde vacío.
/// </para>
/// </summary>
public class RepartoDeCarteraPorClienteRetiradoTests
{
    /// <summary>
    /// <c>AmbitoAsignacion.DeRelacionCliente</c> es la única forma de crear un ámbito por Cliente empresarial.
    /// Su definición es lo único que la nombra en <c>src</c>: ningún productor la llama (la
    /// <c>AsignacionOperacion</c> acotada por Cliente empresarial tampoco tiene productor).
    /// </summary>
    private const string FabricaDelAmbito = "DeRelacionCliente";

    private const string DefinicionDelAmbito = "src/CaeManager.Domain/Operaciones/AmbitoAsignacion.cs";

    [Fact]
    public void Ningun_codigo_de_produccion_crea_un_ambito_de_cartera_por_Cliente_empresarial()
    {
        var raiz = RaizDelRepositorio();

        var usos = ArchivosDeProduccion(raiz)
            .Where(a => ContieneIdentificador(a, FabricaDelAmbito))
            .Select(a => Rel(raiz, a))
            .ToList();

        usos.Should().BeEquivalentTo([DefinicionDelAmbito],
            "el reparto por Cliente empresarial está retirado (D-7): solo la definición de la fábrica del " +
            "ámbito nombra DeRelacionCliente; ningún productor la llama. Si necesitas dar alcance a un Gestor " +
            "CAE, es la cartera del Tenant entero por un acto explícito (IAsignacionesOperativasWriter." +
            "AsegurarCarteraTenantEnteroAsync, CatalogoIncorporacionCartera), no una cartera por Cliente " +
            "empresarial. Control positivo incluido: la definición tiene que aparecer, o el escaneo no mira");
    }

    [Theory]
    [InlineData("src/CaeManager.Infrastructure/Operaciones")]
    [InlineData("src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs")]
    [InlineData("src/CaeManager.Infrastructure/Persistence/Seed/CarterasDeSiembra.cs")]
    [InlineData("src/CaeManager.Infrastructure/Autorizacion/AlcanceDatosService.cs")]
    public void El_alcance_y_las_escrituras_de_cartera_no_leen_la_referencia_del_Cliente_empresarial(string ruta)
    {
        var raiz = RaizDelRepositorio();
        var absoluta = Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar));

        var archivos = File.Exists(absoluta)
            ? [absoluta]
            : Directory.EnumerateFiles(absoluta, "*.cs", SearchOption.AllDirectories).ToArray();
        archivos.Should().NotBeEmpty("el escaneo tiene que encontrar ficheros que mirar");

        var lectores = archivos
            .Where(a => ContieneIdentificador(a, "EjecutivoUsuarioId"))
            .Select(a => Rel(raiz, a))
            .ToList();

        lectores.Should().BeEmpty(
            "Empresa.EjecutivoUsuarioId es una referencia, no una cartera (D-7): ni el cálculo de alcance ni " +
            "las escrituras de Asignaciones de Cartera pueden derivar nada de ella. Quien sea la referencia de " +
            "un Cliente empresarial no gana ni pierde alcance por serlo");
    }

    [Fact]
    public void Control_positivo_el_instrumento_ve_la_referencia_donde_si_existe()
    {
        var raiz = RaizDelRepositorio();
        var empresa = Path.Combine(raiz, "src", "CaeManager.Domain", "Empresas", "Empresa.cs");

        File.Exists(empresa).Should().BeTrue();
        ContieneIdentificador(empresa, "EjecutivoUsuarioId").Should().BeTrue(
            "si el instrumento no viera el identificador donde está declarado, el test anterior pasaría por vacío");
    }

    private static bool ContieneIdentificador(string archivo, string identificador) =>
        CSharpSyntaxTree.ParseText(File.ReadAllText(archivo))
            .GetRoot()
            .DescendantTokens()
            .Any(t => t.IsKind(SyntaxKind.IdentifierToken) && t.Text == identificador);

    private static IEnumerable<string> ArchivosDeProduccion(string raiz)
    {
        var separador = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{separador}obj{separador}")
                        && !a.Contains($"{separador}bin{separador}")
                        && !a.Contains($"{separador}Migrations{separador}")
                        && !a.Contains($"{separador}Migrations.PostgreSQL{separador}"));
    }

    private static string Rel(string raiz, string archivo) =>
        Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
