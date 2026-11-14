using System.Text.RegularExpressions;

namespace CaeManager.Domain.CatalogoBase;

/// <summary>
/// Sistema de clasificación oficial al que se liga una Actividad del catálogo base (CA-2): la CNAE-2025
/// (RD 10/2025) describe el sector de la Empresa, el código de convenio del REGCON fija las obligaciones del
/// convenio y la CNO-11 describe la ocupación del Trabajador. Un código oficial solo propone la Actividad;
/// nunca la decide. Se guarda por nombre, con un CHECK que repite la lista.
/// </summary>
public enum SistemaCodigoOficial
{
    Cnae2025 = 1,
    Regcon = 2,
    Cno11 = 3,
}

/// <summary>
/// Nivel de exigencia de un requisito de base: <see cref="N1"/> lo exige una norma, <see cref="N2"/> un convenio y
/// <see cref="N3"/> es práctica sectorial o exigencia contractual, que nunca se presenta como obligación legal.
/// Se guarda por nombre, con un CHECK que repite la lista.
/// </summary>
public enum NivelExigencia
{
    N1 = 1,
    N2 = 2,
    N3 = 3,
}

/// <summary>
/// Reglas de forma compartidas por las entidades del catálogo base de Plataforma.
/// </summary>
public static class ReglasCatalogoBase
{
    public const int LongitudMaximaCodigo = 50;
    public const int LongitudMaximaNombre = 200;
    public const int LongitudMaximaFundamento = 300;
    public const int LongitudMaximaCodigoOficial = 20;

    /// <summary>
    /// Slug estable en minúsculas, dígitos y guiones: identificador de código, no texto para el usuario. Es el patrón
    /// del CHECK de PostgreSQL, donde <c>$</c> solo casa al final de la cadena.
    /// </summary>
    public const string PatronCodigo = "^[a-z0-9]+(-[a-z0-9]+)*$";

    /// <summary>
    /// Un código oficial (CNAE, REGCON o CNO) solo lleva letras, dígitos y puntos. Patrón del CHECK de PostgreSQL, como
    /// <see cref="PatronCodigo"/>.
    /// </summary>
    public const string PatronCodigoOficial = "^[A-Za-z0-9.]+$";

    // En .NET, «$» también casa delante de un salto de línea final («obra\n» pasaría); «\z» es el final real de la
    // cadena. Los patrones públicos conservan «$» porque son los de los CHECK de PostgreSQL, donde «\z» no existe.
    private static readonly Regex Codigo = new(HastaElFinal(PatronCodigo), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex CodigoOficialValido = new(HastaElFinal(PatronCodigoOficial), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static string HastaElFinal(string patronConDolar) => patronConDolar[..^1] + @"\z";

    public static string ValidarCodigo(string codigo, string parametro)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            throw new ArgumentException("El código es obligatorio.", parametro);
        if (codigo.Length > LongitudMaximaCodigo)
            throw new ArgumentException($"El código tiene como máximo {LongitudMaximaCodigo} caracteres.", parametro);
        if (!Codigo.IsMatch(codigo))
            throw new ArgumentException("El código solo admite minúsculas, dígitos y guiones entre ellos.", parametro);
        return codigo;
    }

    public static string ValidarTexto(string texto, int longitudMaxima, string parametro)
    {
        var limpio = texto?.Trim();
        if (string.IsNullOrEmpty(limpio))
            throw new ArgumentException("El texto es obligatorio.", parametro);
        if (limpio.Length > longitudMaxima)
            throw new ArgumentException($"El texto tiene como máximo {longitudMaxima} caracteres.", parametro);
        return limpio;
    }

    public static string ValidarCodigoOficial(string codigo, string parametro)
    {
        var limpio = codigo?.Trim();
        if (string.IsNullOrEmpty(limpio))
            throw new ArgumentException("El código oficial es obligatorio.", parametro);
        if (limpio.Length > LongitudMaximaCodigoOficial)
            throw new ArgumentException($"El código oficial tiene como máximo {LongitudMaximaCodigoOficial} caracteres.", parametro);
        if (!CodigoOficialValido.IsMatch(limpio))
            throw new ArgumentException("El código oficial solo admite letras, dígitos y puntos.", parametro);
        return limpio;
    }

    public static T ValidarEnum<T>(T valor, string parametro) where T : struct, Enum =>
        Enum.IsDefined(valor) ? valor : throw new ArgumentOutOfRangeException(parametro, valor, "Valor fuera del catálogo.");
}
