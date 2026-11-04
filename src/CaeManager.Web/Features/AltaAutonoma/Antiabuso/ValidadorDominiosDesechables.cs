using System.Collections.Frozen;
using System.Globalization;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>
/// Dice si un correo pertenece a un dominio de correo desechable conocido (plan del alta autónoma, § 4.4). La lista es
/// estática y versionada (<c>DominiosDesechables.txt</c>, recurso incrustado): no consulta ningún servicio externo.
///
/// <para>
/// El dominio es lo que sigue a la última arroba, sin espacios, sin el punto final y en minúsculas; un dominio
/// internacionalizado se compara en su forma ASCII (punycode). Un dominio listado cubre sus subdominios, comparando por
/// etiquetas completas: <c>x.mailinator.com</c> es desechable y <c>notmailinator.com</c> no. Un correo sin arroba o sin
/// dominio no es desechable: validar el formato del correo no es cosa de esta clase.
/// </para>
/// </summary>
public sealed class ValidadorDominiosDesechables
{
    public const string NombreRecurso = "CaeManager.Web.AltaAutonoma.DominiosDesechables.txt";

    private const int LongitudMaximaDominio = 253;

    private static readonly IdnMapping Idn = new();

    private readonly FrozenSet<string> _dominios;

    public ValidadorDominiosDesechables()
        : this(LeerRecurso())
    {
    }

    private ValidadorDominiosDesechables(IEnumerable<string> lineas)
    {
        _dominios = lineas
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => Normalizar(l) ?? l.ToLowerInvariant())
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Validador sobre una lista dada, con el mismo formato que el recurso (para pruebas).</summary>
    public static ValidadorDominiosDesechables DesdeLineas(IEnumerable<string> lineas) => new(lineas);

    public int Cantidad => _dominios.Count;

    public bool EsDominioDesechable(string? correo)
    {
        if (string.IsNullOrWhiteSpace(correo))
            return false;

        var arroba = correo.LastIndexOf('@');
        if (arroba < 0)
            return false;

        var dominio = Normalizar(correo[(arroba + 1)..]);
        if (dominio is null)
            return false;

        // El dominio y cada uno de sus padres, cortando por puntos: a.b.c → a.b.c, b.c, c.
        for (var resto = dominio; ; )
        {
            if (_dominios.Contains(resto))
                return true;

            var punto = resto.IndexOf('.');
            if (punto < 0)
                return false;

            resto = resto[(punto + 1)..];
        }
    }

    private static string? Normalizar(string dominio)
    {
        dominio = dominio.Trim().TrimEnd('.');
        // 253 es el máximo de un nombre de dominio: más largo no es un dominio y no se normaliza.
        if (dominio.Length is 0 or > LongitudMaximaDominio)
            return null;

        try
        {
            return Idn.GetAscii(dominio).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IEnumerable<string> LeerRecurso()
    {
        using var flujo = typeof(ValidadorDominiosDesechables).Assembly.GetManifestResourceStream(NombreRecurso)
            ?? throw new InvalidOperationException($"Falta el recurso incrustado {NombreRecurso}.");
        using var lector = new StreamReader(flujo);
        var lineas = new List<string>();
        while (lector.ReadLine() is { } linea)
            lineas.Add(linea);
        return lineas;
    }
}
