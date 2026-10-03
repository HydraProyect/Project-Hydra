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

    /// <summary>
    /// Desde la contracción (incremento 3) el dominio y el CHECK <c>CK_AsignacionesCartera_TenantEnteroSalvoCerrada</c>
    /// impiden una cartera no cerrada por Cliente empresarial. Fabricar una en una prueba solo es legítimo para
    /// ejercitar la migración de datos o el rechazo de la base (<c>CarteraLegadaPorCliente</c>); en cualquier otra
    /// prueba, acotar el alcance a un Cliente empresarial se hace acotando la Asignación de Operación.
    /// </summary>
    [Fact]
    public void Solo_las_pruebas_de_migracion_fabrican_una_cartera_por_Cliente_empresarial()
    {
        var raiz = RaizDelRepositorio();
        var carpetaPermitida = "tests/CaeManager.IntegrationTests/Migraciones/";

        var usos = Directory.EnumerateFiles(Path.Combine(raiz, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => ContieneIdentificador(a, "CarteraLegadaPorCliente"))
            .Select(a => Rel(raiz, a))
            .ToList();

        usos.Should().NotBeEmpty("control positivo: la pruebas de migración sí la usan y el instrumento las ve");
        usos.Should().OnlyContain(u => u.StartsWith(carpetaPermitida, StringComparison.Ordinal),
            "una cartera por Cliente empresarial ya no es un estado válido: solo las pruebas de la migración de datos y del " +
            "CHECK la fabrican, saltándose la guarda de dominio. Cualquier otra prueba acota la Asignación de Operación " +
            "(AsignacionOperacion.Interna/Externa con AmbitoAsignacion.DeRelacionCliente) y cuelga de ella una cartera universal");
    }

    [Fact]
    public void Nadie_construye_un_AmbitoAsignacion_a_mano_fuera_de_su_definicion()
    {
        // new AmbitoAsignacion(clienteId, ...) crea un ámbito por Cliente empresarial sin pasar por la fábrica
        // DeRelacionCliente. (Un new(...) de destino implícito no se ve desde la sintaxis: el modelo de EF y
        // la propiedad Ambito de las asignaciones lo usan para leer, no para conceder.)
        var raiz = RaizDelRepositorio();

        var infractores = ArchivosDeProduccion(raiz)
            .Where(a => CSharpSyntaxTree.ParseText(File.ReadAllText(a)).GetRoot()
                .DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax>()
                .Any(n => n.Type.ToString() == "AmbitoAsignacion"))
            .Select(a => Rel(raiz, a))
            .ToList();

        infractores.Should().BeEquivalentTo([DefinicionDelAmbito],
            "solo la definición del ámbito lo construye (en su fábrica DeRelacionCliente); control positivo incluido");
    }

    /// <summary>
    /// <c>AsegurarCarteraTenantEnteroAsync</c> concede el Tenant entero a un Gestor CAE sin comprobar la
    /// autoridad de quien llama: es el acto de una siembra, no de un Command. Un handler que la inyectara
    /// concedería alcance saltándose <c>AutoridadSobreCarteraDeGestorCae</c>.
    /// </summary>
    [Fact]
    public void Solo_las_siembras_y_el_propio_escritor_conceden_la_cartera_del_Tenant_entero_sin_autoridad()
    {
        var raiz = RaizDelRepositorio();
        var permitidos = new[]
        {
            "src/CaeManager.Application/Operaciones/IAsignacionesOperativasWriter.cs",
            "src/CaeManager.Infrastructure/Operaciones/AsignacionesOperativasWriter.cs",
        };

        var usos = ArchivosDeProduccion(raiz)
            .Where(a => ContieneIdentificador(a, "AsegurarCarteraTenantEnteroAsync"))
            .Select(a => Rel(raiz, a))
            .ToList();

        usos.Should().Contain(permitidos, "control positivo: el instrumento ve la definición y la implementación");
        usos.Except(permitidos).Should().OnlyContain(u => u.Contains("/Persistence/Seed/"),
            "fuera del escritor, solo las siembras (src/CaeManager.Infrastructure/Persistence/Seed) conceden la cartera del " +
            "Tenant entero sin autoridad; un Command debe pasar por AutoridadSobreCarteraDeGestorCae");
        usos.Except(permitidos).Should().NotBeEmpty("las siembras lo usan");
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
