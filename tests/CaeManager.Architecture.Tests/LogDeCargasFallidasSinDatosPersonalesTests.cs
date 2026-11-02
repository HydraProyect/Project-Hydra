using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// M-06 / MJ-02 (plan de mejora del código, 2026-10-31): los avisos de «no se pudo cargar» de las páginas de
/// <c>src/CaeManager.Web</c> registran <b>solo el tipo de la excepción</b>, nunca la excepción ni su mensaje.
///
/// <para>
/// <b>Por qué.</b> El mensaje de una excepción de EF o de Npgsql puede llevar el valor que falló (un DNI, un correo, una
/// razón social); el log de Seq/Sentry sale de la frontera del Tenant. Los <c>catch</c> que dejaron de ser silenciosos con
/// M-06 usan la plantilla <c>"…: {TipoExcepcion}."</c> con <c>ex.GetType().Name</c>. Este test exige que toda llamada a un
/// <c>Logger.Log*</c> con ese marcador pase exactamente eso, y que el uso no desaparezca (control positivo).
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): (1) solo mira las llamadas que usan la plantilla <c>{TipoExcepcion}</c>:
/// un <c>Logger.LogError(ex, …)</c> escrito a mano en otro sitio no entra aquí (los hay legítimos, de errores sin datos
/// personales); (2) comprueba el texto del código, no el contenido del log en ejecución; (3) un <c>catch</c> nuevo que
/// siga siendo silencioso no lo detecta: eso es el trabajo pendiente de M-06, no esta propiedad.
/// </para>
/// </summary>
public class LogDeCargasFallidasSinDatosPersonalesTests
{
    private const string Marcador = "{TipoExcepcion}";

    private static readonly Regex LlamadaConMarcador = new(
        @"\bLogger\s*\.\s*Log(?:Warning|Error|Information|Debug|Critical)\s*\((?<args>[^;]*?\{TipoExcepcion\}\.""[^;]*?)\)\s*;",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Las_cargas_fallidas_registran_solo_el_tipo_de_la_excepcion()
    {
        var llamadas = LlamadasEnWeb();

        // Control positivo: si el escaneo no ve los avisos de M-06, la comprobación de abajo valdría por vacío.
        llamadas.Count.Should().BeGreaterThanOrEqualTo(9, "M-06 y MJ-02 dejaron al menos nueve avisos con esta plantilla");

        var infractoras = llamadas.Where(l => !PasaSoloElTipo(l.Argumentos)).Select(l => l.Fichero).Distinct().ToList();
        infractoras.Should().BeEmpty(
            "con la plantilla {TipoExcepcion} el único argumento es ex.GetType().Name; pasar la excepción o su Message " +
            "saca datos personales al log");
    }

    [Fact]
    public void El_detector_acepta_el_tipo_y_rechaza_la_excepcion_y_su_mensaje()
    {
        PasaSoloElTipo("\"No se pudo: {TipoExcepcion}.\", ex.GetType().Name").Should().BeTrue();
        PasaSoloElTipo("\"No se pudo en {CentroId}: {TipoExcepcion}.\", centroId, ex.GetType().Name").Should().BeTrue();
        PasaSoloElTipo("ex, \"No se pudo: {TipoExcepcion}.\", ex.GetType().Name").Should().BeFalse();
        PasaSoloElTipo("\"No se pudo: {TipoExcepcion}.\", ex.Message").Should().BeFalse();
        PasaSoloElTipo("\"No se pudo: {TipoExcepcion}.\", ex.ToString()").Should().BeFalse();
        PasaSoloElTipo("\"No se pudo: {TipoExcepcion}.\", ex").Should().BeFalse();
    }

    private static bool PasaSoloElTipo(string argumentos)
    {
        // Debe terminar en ex.GetType().Name y la excepcion no puede aparecer de ninguna otra forma entre los argumentos.
        var m = Regex.Match(argumentos, @"^(?<antes>.*?)\bex\.GetType\(\)\.Name\s*$", RegexOptions.Singleline);
        if (!m.Success)
            return false;

        var sinLiterales = Regex.Replace(m.Groups["antes"].Value, @"""(?:[^""\\]|\\.)*""", string.Empty);
        return !Regex.IsMatch(sinLiterales, @"\b(?:ex|excepcion)\b");
    }

    private static List<(string Fichero, string Argumentos)> LlamadasEnWeb()
    {
        var raiz = FuentesDeSrc.RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;
        var resultado = new List<(string, string)>();

        foreach (var a in Directory.EnumerateFiles(Path.Combine(raiz, "src", "CaeManager.Web"), "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}")))
        {
            var texto = File.ReadAllText(a);
            if (!texto.Contains(Marcador, StringComparison.Ordinal))
                continue;

            var limpio = LimpiadorDeComentarios.Quitar(texto, razor: a.EndsWith(".razor", StringComparison.Ordinal));
            var relativo = Path.GetRelativePath(raiz, a).Replace(separador, '/');
            foreach (Match m in LlamadaConMarcador.Matches(limpio))
                resultado.Add((relativo, m.Groups["args"].Value.Trim()));
        }

        return resultado;
    }
}
