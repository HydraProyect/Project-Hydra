using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using CaeManager.Infrastructure.Operaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La Operación nunca concede roles de Propiedad (ADR-011 § 2.7). El catálogo de incorporación a
/// cartera emite carteras externas con dos roles fijos —Gestor CAE al asignar, Coordinador CAE en
/// el relevo del principal (enmienda 2026-10-08)— y el rol no es un parámetro de quien llama.
///
/// <para>
/// Lo que observa: el valor de las constantes de rol del catálogo y que toda cartera externa y
/// toda fila heredada que el fichero construye lleve una de ellas. <b>No observa</b> la lectura:
/// que un rol de Propiedad ya escrito no abra el Tenant lo prueba, en integración,
/// <c>RolesDelegadosSoloDeOperacionTests</c>.
/// </para>
/// </summary>
public class RolesQueEmiteElCatalogoDeCarteraTests
{
    private const string Fichero = "src/CaeManager.Infrastructure/Operaciones/CatalogoIncorporacionCartera.cs";

    private static readonly Regex Emision = new(
        @"(?:AsignacionCartera\.Externa|new\s+AsignacionOperadorDelegado)\s*\((?<argumentos>[^;]*?)\)\s*[;)]",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    [Fact]
    public void Las_constantes_de_rol_del_catalogo_son_Gestor_CAE_y_Coordinador_CAE()
    {
        var constantes = typeof(CatalogoIncorporacionCartera)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.StartsWith("Rol", StringComparison.Ordinal))
            .ToDictionary(f => f.Name, f => (string?)f.GetRawConstantValue());

        constantes.Should().BeEquivalentTo(new Dictionary<string, string?>
        {
            ["RolIncorporado"] = "GestorCae",
            ["RolDeRelevo"] = "CoordinadorCae",
        }, "una cartera externa emitida con Administrador o DireccionCae daría la Propiedad del Tenant por la vía de Operación");
    }

    [Fact]
    public void Toda_cartera_y_toda_fila_heredada_que_emite_el_catalogo_lleva_una_de_esas_constantes()
    {
        var emisiones = Emisiones(File.ReadAllText(Path.Combine(
            FuentesDeSrc.RaizDelRepositorio(), Fichero.Replace('/', Path.DirectorySeparatorChar))));

        emisiones.Should().HaveCount(6, "tres carteras (incorporación, relevo y apoyo) y sus tres filas heredadas; si cambia, este test tiene que mirar la emisión nueva");
        emisiones.Where(e => !LlevaRolFijo(e)).Should().BeEmpty("el rol de una cartera emitida no sale de un parámetro ni de un literal");
    }

    /// <summary>
    /// La vía de apoyo (propuesta aceptada) emite con el rol Gestor CAE y nunca marca principal:
    /// una cartera de apoyo que naciera principal desplazaría, o duplicaría, a quien responde del
    /// Tenant, sin pasar por la designación ni por su autoridad.
    /// </summary>
    [Fact]
    public void La_via_de_apoyo_emite_Gestor_CAE_y_nunca_marca_principal()
    {
        var cuerpo = CuerpoDe(File.ReadAllText(Path.Combine(
            FuentesDeSrc.RaizDelRepositorio(), Fichero.Replace('/', Path.DirectorySeparatorChar))), "IncorporarApoyoAsync");

        var emisiones = Emisiones(cuerpo);
        emisiones.Should().HaveCount(2, "la cartera de apoyo y su fila heredada");
        emisiones.Should().OnlyContain(e => LlevaElRol(e, "RolIncorporado"), "un apoyo es siempre Gestor CAE, nunca el rol del relevo");
        MarcaPrincipal(cuerpo).Should().BeFalse("una cartera de apoyo nunca nace con la marca de principal");
    }

    [Theory]
    [InlineData("    public async Task<X> IncorporarApoyoAsync(P p)\n    {\n        var c = AsignacionCartera.Externa(o, u, RolIncorporado, a);\n        c.DesignarPrincipal(ahora);\n    }\n")]
    [InlineData("    public async Task<X> IncorporarApoyoAsync(P p)\n    {\n        var c = AsignacionCartera.Externa(o, u, RolIncorporado, a);\n        principal.RetirarPrincipal();\n        c .DesignarPrincipal (ahora);\n    }\n")]
    public void Mutacion_una_via_de_apoyo_que_marca_principal_se_detecta(string fuente) =>
        MarcaPrincipal(CuerpoDe(fuente, "IncorporarApoyoAsync")).Should().BeTrue();

    [Fact]
    public void Control_positivo_el_cuerpo_se_corta_en_el_metodo_siguiente()
    {
        const string Fuente =
            "    public async Task<X> IncorporarApoyoAsync(P p)\n    {\n        var c = AsignacionCartera.Externa(o, u, RolIncorporado, a);\n    }\n\n"
            + "    public async Task<Y> DesignarAsync(P p)\n    {\n        c.DesignarPrincipal(ahora);\n    }\n";

        var cuerpo = CuerpoDe(Fuente, "IncorporarApoyoAsync");

        cuerpo.Should().Contain("AsignacionCartera.Externa");
        MarcaPrincipal(cuerpo).Should().BeFalse("la designación es del método siguiente, no de la vía de apoyo");
        MarcaPrincipal(CuerpoDe(Fuente, "DesignarAsync")).Should().BeTrue();
    }

    [Theory]
    [InlineData("var c = AsignacionCartera.Externa(operacion, usuarioId, rol, AmbitoAsignacion.Universal, ahora, null, ahora);")]
    [InlineData("var c = AsignacionCartera.Externa(operacion, usuarioId, Roles.Administrador, AmbitoAsignacion.Universal, ahora, null, ahora);")]
    [InlineData("contexto.Add(new AsignacionOperadorDelegado(vinculoId.Value, usuarioId, \"DireccionCae\"));")]
    public void Mutacion_una_emision_con_el_rol_abierto_o_de_Propiedad_se_detecta(string fuente) =>
        Emisiones(fuente).Should().ContainSingle().Which.Should().Match(e => !LlevaRolFijo(e));

    [Fact]
    public void Control_positivo_una_emision_con_la_constante_pasa() =>
        Emisiones("var c = AsignacionCartera.Externa(\n    operacion, coordinadorUsuarioId, RolDeRelevo, AmbitoAsignacion.Universal,\n    ahora, vigenciaHasta: null, ahora, actorId);")
            .Should().ContainSingle().Which.Should().Match(e => LlevaRolFijo(e));

    private static List<string> Emisiones(string fuente) =>
        Emision.Matches(fuente).Select(m => m.Groups["argumentos"].Value).ToList();

    private static bool LlevaRolFijo(string argumentos) =>
        argumentos.Split(',').Select(a => a.Trim()).Any(a => a is "RolIncorporado" or "RolDeRelevo");

    private static bool LlevaElRol(string argumentos, string constante) =>
        argumentos.Split(',').Select(a => a.Trim()).Any(a => a == constante);

    private static readonly Regex DesignacionDePrincipal = new(
        @"\.\s*DesignarPrincipal\s*\(", RegexOptions.CultureInvariant);

    private static readonly Regex MiembroSiguiente = new(
        @"\n    (?:public|private|internal|protected)\s", RegexOptions.CultureInvariant);

    private static bool MarcaPrincipal(string cuerpo) => DesignacionDePrincipal.IsMatch(cuerpo);

    /// <summary>
    /// El texto de un método de la clase: desde su declaración hasta la del miembro siguiente
    /// (los miembros de la clase van sangrados a cuatro espacios). Lanza si no encuentra el
    /// método: un cuerpo vacío daría por bueno lo que no se ha mirado.
    /// </summary>
    private static string CuerpoDe(string fuente, string metodo)
    {
        fuente = fuente.Replace("\r\n", "\n", StringComparison.Ordinal);
        var declaracion = new Regex(
            @"\n?    (?:public|private|internal|protected)[^\n;{]*\b" + Regex.Escape(metodo) + @"\s*\(",
            RegexOptions.CultureInvariant).Match(fuente);
        if (!declaracion.Success)
            throw new InvalidOperationException($"No se encuentra la declaración de {metodo}.");

        var resto = fuente[(declaracion.Index + declaracion.Length)..];
        var siguiente = MiembroSiguiente.Match(resto);
        return siguiente.Success ? resto[..siguiente.Index] : resto;
    }
}
