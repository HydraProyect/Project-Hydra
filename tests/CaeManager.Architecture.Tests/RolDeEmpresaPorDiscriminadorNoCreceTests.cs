using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Trinquete por ubicación del rol de Empresa por discriminador</b> — Fase 0 del plan de
/// migración de los restos del modelo <c>Cliente</c> (ADR-011) y S11 del análisis de causas raíz,
/// ambos de 2026-10-02 (<c>Project-Hydra-Negocio/tecnico/</c>).
///
/// <para>
/// <b>El resto R1.</b> ADR-011 § 2.3 dice que la entidad <c>Empresa</c> no lleva ningún rol CAE.
/// Hoy lo lleva: «es Cliente empresarial» es <c>EsCritico != null</c> y «es Subcontrata» es
/// <c>NivelServicio != null</c>, y las crean dos fábricas por rol. Los propios doc-comments de
/// <c>Empresa.cs</c> lo declaran deuda transitoria. Ningún instrumento impedía que siguiera
/// extendiéndose. Dos listas:
/// <list type="bullet">
/// <item><c>Discriminador-nulo</c>: dónde se decide algo por la nulidad de <c>EsCritico</c> o
/// <c>NivelServicio</c> (<c>!= null</c>, <c>== null</c>, <c>is null</c>, <c>is not null</c>,
/// <c>is { }</c>, <c>.HasValue</c>).</item>
/// <item><c>Fabricas-por-rol</c>: dónde se nombra cualquier <c>&lt;Verbo&gt;ComoCliente</c> o
/// <c>&lt;Verbo&gt;ComoSubcontrata</c> —<c>CrearComo…</c>, <c>ActualizarComo…</c> y
/// <c>CambiarNivelServicioComoSubcontrata</c>, que fija el rol escribiendo <c>NivelServicio</c>— en
/// su declaración y en sus llamadas de <c>src</c>.</item>
/// </list>
/// Las dos son por ubicación (<see cref="ListaCongelada"/>): un uso nuevo en un fichero nuevo, o más
/// usos en uno listado, ponen el test en rojo, y menos usos también, para que la lista baje con el
/// código hasta la Fase 7 (contraer).
/// </para>
///
/// <para>
/// <b>Qué no ve</b> (declarado): <c>tests/</c> (cientos de usos de las fábricas en ficheros de test, que
/// se reescriben al contraer); la nulidad leída a través de una variable intermedia, un
/// <c>switch</c> con brazo <c>null</c> o <c>??</c> (ver
/// <c>FuentesDeSrc.NombreComparadoConNull</c>); y cualquier otra forma de decidir el rol
/// (<c>EjecutivoUsuarioId != null</c> lo vigila <c>TerminologiaCanonicaTests</c>).
/// </para>
/// </summary>
public class RolDeEmpresaPorDiscriminadorNoCreceTests
{
    // «<verbo>ComoCliente» / «<verbo>ComoSubcontrata»: la familia de fábricas y mutadores que fijan el rol
    // de la Empresa (CrearComo…, ActualizarComo…, CambiarNivelServicioComo…). Anclada por los dos lados:
    // un nombre que solo contiene la secuencia no cuenta.
    private static readonly Regex Fabrica = new(@"^[A-Z][A-Za-z0-9]*Como(Cliente|Subcontrata)$", RegexOptions.Compiled);

    [Fact]
    public void Las_lecturas_por_discriminador_EsCritico_y_NivelServicio_solo_decrecen()
    {
        var medido = FuentesDeSrc.UbicacionesDeComparacionesConNull(FuentesDeSrc.Analisis);

        var fallo = ListaCongelada.Verificar("Discriminador-nulo", medido,
            "Decidir si una Empresa es Cliente empresarial o Subcontrata por la nulidad de EsCritico o NivelServicio " +
            "contradice ADR-011 § 2.3 (la entidad Empresa no lleva ningún rol CAE): el rol se deriva de la " +
            "RelacionEmpresarial. No añadas lecturas nuevas; si hace falta distinguir, espera al puerto de " +
            "clasificación (Fase 3 del plan) o pide una decisión. Si el uso es inevitable y deliberado, añade la " +
            "línea en el mismo commit y justifícalo en la PR; si has RETIRADO lecturas, baja o borra su línea. " +
            "Formato: 'fichero :: EsCritico|NivelServicio = n'.");

        fallo.Should().BeNull();
    }

    [Fact]
    public void Las_fabricas_de_Empresa_por_rol_solo_decrecen()
    {
        var medido = FuentesDeSrc.UbicacionesDePalabras(FuentesDeSrc.Analisis, p => Fabrica.IsMatch(p));

        var fallo = ListaCongelada.Verificar("Fabricas-por-rol", medido,
            "CrearComoCliente/CrearComoSubcontrata (y sus Actualizar…) fijan el rol de la Empresa en el momento de " +
            "crearla: es el discriminador R1 con otro nombre (plan de migración de Cliente, Fase 0 y Fase 7). No las " +
            "llames desde código nuevo. Si es inevitable y deliberado, añade la línea en el mismo commit y " +
            "justifícalo en la PR; si has RETIRADO llamadas, baja o borra su línea.");

        fallo.Should().BeNull();
    }

    // ───────────── control positivo del detector, con fuentes sintéticas ─────────────

    [Theory]
    [InlineData("var q = db.Empresas.Where(e => e.EsCritico != null);", "EsCritico")]
    [InlineData("var q = db.Empresas.Where(e => e.NivelServicio != null);", "NivelServicio")]
    [InlineData("var b = e.EsCritico == null;", "EsCritico")]
    [InlineData("var b = null == e.EsCritico;", "EsCritico")]
    [InlineData("var b = null != e.NivelServicio;", "NivelServicio")]
    [InlineData("var b = e.EsCritico is null;", "EsCritico")]
    [InlineData("var b = e.NivelServicio is not null;", "NivelServicio")]
    [InlineData("var b = e.EsCritico is { };", "EsCritico")]
    [InlineData("var b = e.EsCritico is not { };", "EsCritico")]
    [InlineData("var b = e.EsCritico is bool;", "EsCritico")]
    [InlineData("var b = e.EsCritico is bool presente;", "EsCritico")]
    [InlineData("var b = e.NivelServicio is string nivel;", "NivelServicio")]
    [InlineData("var b = e.EsCritico is { Value: true };", "EsCritico")]
    [InlineData("var b = e.EsCritico.HasValue;", "EsCritico")]
    [InlineData("var b = !string.IsNullOrEmpty(e.NivelServicio);", "NivelServicio")]
    [InlineData("var b = !String.IsNullOrWhiteSpace(e.NivelServicio);", "NivelServicio")]
    [InlineData("var b = IsNullOrEmpty(e.NivelServicio);", "NivelServicio")]
    [InlineData("var b = e.NivelServicio is null or \"\";", "NivelServicio")]
    [InlineData("var b = e.NivelServicio is not (null or \"\");", "NivelServicio")]
    [InlineData("var b = e.EsCritico is true or false;", "EsCritico")]
    [InlineData("var b = e.NivelServicio == default;", "NivelServicio")]
    [InlineData("var b = e.EsCritico != default(bool?);", "EsCritico")]
    [InlineData("var b = (e.EsCritico) != null;", "EsCritico")]
    [InlineData("var b = e?.EsCritico != null;", "EsCritico")]
    [InlineData("var b = e.NivelServicio! is null;", "NivelServicio")]
    [InlineData("if (EsCritico != null) { }", "EsCritico")]
    public void Cada_forma_de_decidir_el_rol_por_nulidad_se_detecta(string sentencia, string nombre)
    {
        var fuente = $"class C {{ void M(dynamic e, dynamic db, object EsCritico) {{ {sentencia} }} }}";

        FuentesDeSrc.AnalizarCSharp(fuente).ComparacionesConNull.Should().Equal(new Dictionary<string, int> { [nombre] = 1 });
    }

    [Theory]
    [InlineData("// e.EsCritico != null")]
    [InlineData("/* e.NivelServicio is null */")]
    [InlineData("var s = \"e.EsCritico != null\";")]
    [InlineData("var b = e.EsCritico == true;")]
    [InlineData("var b = e.EsCritico is true;")]
    [InlineData("var b = e.OtroEsCritico != null;")]
    [InlineData("var b = e.NombreDeEsCritico is null;")]
    [InlineData("var b = e.Notas != null;")]
    [InlineData("var b = e.NivelServicio == \"critico\";")]
    [InlineData("var x = e.EsCritico;")]
    [InlineData("var b = e.NivelServicio is \"Supervisada\";")]
    [InlineData("var b = string.IsNullOrEmpty(e.Notas);")]
    [InlineData("var b = string.IsNullOrEmpty(e.NivelServicio + \"x\");")]
    [InlineData("var b = e.EsCritico is true or true;")]
    public void Lo_que_no_decide_el_rol_por_nulidad_no_cuenta(string sentencia)
    {
        var fuente = $"class C {{ void M(dynamic e) {{ {sentencia}\n }} }}";

        FuentesDeSrc.AnalizarCSharp(fuente).ComparacionesConNull.Should().BeEmpty();
    }

    [Fact]
    public void Un_mismo_fichero_acumula_las_comparaciones_por_nombre()
    {
        const string fuente = """
            class C
            {
                void M(dynamic e)
                {
                    var a = e.EsCritico != null;
                    var b = e.EsCritico == null;
                    var c = e.NivelServicio != null;
                }
            }
            """;

        FuentesDeSrc.AnalizarCSharp(fuente).ComparacionesConNull
            .Should().Equal(new Dictionary<string, int> { ["EsCritico"] = 2, ["NivelServicio"] = 1 });
    }

    [Fact]
    public void En_Razor_se_detectan_las_formas_y_se_ignoran_los_comentarios()
    {
        const string fuente = """
            @* e.EsCritico != null en un comentario Razor *@
            <!-- e.NivelServicio != null en un comentario HTML -->
            @code {
                // e.EsCritico != null en un comentario de C#
                bool EsCliente(dynamic e) => e.EsCritico != null;
                bool EsSub(dynamic e) => e.NivelServicio is not null;
                bool EsSub2(dynamic e) => !string.IsNullOrEmpty(e.NivelServicio);
                bool EsCli2(dynamic e) => e.EsCritico is bool;
                bool NoCuenta(dynamic e) => e.EsCritico == true;
            }
            """;

        FuentesDeSrc.AnalizarRazor(fuente).ComparacionesConNull
            .Should().Equal(new Dictionary<string, int> { ["EsCritico"] = 2, ["NivelServicio"] = 2 });
    }

    [Theory]
    [InlineData("CrearComoCliente", true)]
    [InlineData("CrearComoSubcontrata", true)]
    [InlineData("ActualizarComoCliente", true)]
    [InlineData("ActualizarComoSubcontrata", true)]
    [InlineData("CambiarNivelServicioComoSubcontrata", true)]
    [InlineData("CrearComoClienteDeDemo", false)]
    [InlineData("ComoCliente", false)]
    [InlineData("crearComoCliente", false)]
    [InlineData("CrearComoEmpresa", false)]
    [InlineData("CrearEmpresa", false)]
    [InlineData("ComoClienteEmpresarial", false)]
    public void El_detector_de_fabricas_esta_anclado_no_casa_por_prefijo_ni_por_sufijo(string palabra, bool esperado) =>
        Fabrica.IsMatch(palabra).Should().Be(esperado);

    [Fact]
    public void Las_fabricas_se_cuentan_como_identificador_no_como_comentario()
    {
        const string fuente = """
            class C
            {
                // Empresa.CrearComoCliente(...) citado en un comentario
                void M() { var e = Empresa.CrearComoCliente("x", "y", false, null, null); var s = "CrearComoSubcontrata"; }
            }
            """;

        var palabras = FuentesDeSrc.AnalizarCSharp(fuente).Palabras;

        palabras.Should().ContainKey("CrearComoCliente").WhoseValue.Should().Be(1);
        palabras.Should().NotContainKey("CrearComoSubcontrata");
    }
}
