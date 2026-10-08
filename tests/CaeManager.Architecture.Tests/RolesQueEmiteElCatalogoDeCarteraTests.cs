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

        emisiones.Should().HaveCount(4, "dos carteras y sus dos filas heredadas; si cambia, este test tiene que mirar la emisión nueva");
        emisiones.Where(e => !LlevaRolFijo(e)).Should().BeEmpty("el rol de una cartera emitida no sale de un parámetro ni de un literal");
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
}
