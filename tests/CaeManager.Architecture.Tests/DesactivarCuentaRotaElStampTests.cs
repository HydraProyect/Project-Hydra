using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Quien escribe <c>LockoutEnd</c> desactiva (o bloquea) una cuenta, y desactivar
/// tiene que cortar las sesiones ya abiertas: la pieza que las corta es el
/// rechazo de una cuenta desactivada en <c>SignInManagerCuentaDesactivada</c>, pero la
/// rotación del security stamp es lo que impide que <b>reactivar</b> devuelva
/// la vida a una cookie anterior (auditoría de seguridad 2026-09-20). Sin ella,
/// un escritor nuevo de <c>LockoutEnd</c> —otra pantalla, un comando, un
/// seeder— dejaría cookies viejas resucitables sin que nada se pusiera en rojo.
///
/// <para>
/// <b>Qué mide y qué no.</b> Por texto, sobre el <c>src</c> sin comentarios (una mención en
/// un comentario no cuenta como escritura ni como rotación): todo fichero que asigna
/// <c>LockoutEnd</c> o llama a <c>SetLockoutEndDateAsync</c> tiene que rotar
/// también el stamp (<c>UpdateSecurityStampAsync</c> o asignar <c>SecurityStamp</c>;
/// hoy lo hace <c>ApplicationUser.Desactivar</c>, el único escritor). Es una vara a nivel de fichero, no
/// de método: no demuestra que la rotación ocurra en la rama de desactivar —eso
/// lo demuestra la prueba de integración de la sesión de cuenta desactivada—,
/// solo que quien escribe no puede olvidarse de la idea entera. Un bloqueo
/// temporal por intentos fallidos no pasa por aquí: lo escribe Identity dentro
/// de <c>SignInManager</c>, no el código de <c>src</c>.
/// </para>
/// </summary>
public class DesactivarCuentaRotaElStampTests
{
    private static readonly Regex EscribeElBloqueo = new(
        @"\bLockoutEnd\s*=(?!=)|\bSetLockoutEndDateAsync\s*\(", RegexOptions.Compiled);

    private static readonly Regex AsignaElStamp = new(@"\bSecurityStamp\s*=(?!=)", RegexOptions.Compiled);

    /// <summary>
    /// Control positivo: el único escritor conocido de la desactivación. Si el
    /// escaneo deja de verlo, el ratchet estaría dando verde por no mirar.
    /// </summary>
    private const string EscritorConocido = "src/CaeManager.Infrastructure/Identity/ApplicationUser.cs";

    [Fact]
    public void El_escaneo_ve_al_escritor_conocido_de_la_desactivacion()
    {
        Escritores().Select(e => e.Ruta).Should().Contain(EscritorConocido,
            "si el escaneo no ve a ApplicationUser.Desactivar, dejó de mirar donde cree que mira");
    }

    [Fact]
    public void Todo_escritor_de_LockoutEnd_rota_tambien_el_security_stamp()
    {
        var sinRotar = Escritores()
            .Where(e => !e.RotaElStamp)
            .Select(e => e.Ruta)
            .OrderBy(r => r)
            .ToList();

        string.Join("\n", sinRotar).Should().BeEmpty(
            "desactivar una cuenta sin rotar su security stamp deja que reactivarla resucite cualquier cookie " +
            "anterior a la desactivación (el rechazo por «cuenta desactivada» desaparece al reactivar); usa " +
            "ApplicationUser.Desactivar(), que fija el bloqueo y el stamp juntos, o llama a UserManager.UpdateSecurityStampAsync");
    }

    private static List<(string Ruta, bool RotaElStamp)> Escritores()
    {
        var raiz = RaizDelRepositorio();
        var resultado = new List<(string, bool)>();
        var directorio = Path.Combine(raiz, "src");

        var archivos = Directory
            .EnumerateFiles(directorio, "*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || a.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !a.Contains("Migrations"));

        foreach (var archivo in archivos)
        {
            var texto = LimpiadorDeComentarios.Quitar(
                File.ReadAllText(archivo), razor: archivo.EndsWith(".razor", StringComparison.OrdinalIgnoreCase));
            if (!EscribeElBloqueo.IsMatch(texto)) continue;

            resultado.Add((
                Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/'),
                texto.Contains("UpdateSecurityStampAsync", StringComparison.Ordinal)
                || AsignaElStamp.IsMatch(texto)));
        }

        return resultado;
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
