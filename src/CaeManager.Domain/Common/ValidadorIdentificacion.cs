using System.Text.RegularExpressions;

namespace CaeManager.Domain.Common;

/// <summary>Tipo de documento de identificación detectado por <see cref="ValidadorIdentificacion"/>.</summary>
public enum TipoIdentificacion
{
    /// <summary>DNI español (8 dígitos + letra de control).</summary>
    Dni,

    /// <summary>NIE — extranjero residente en España (X/Y/Z + 7 dígitos + letra de control).</summary>
    Nie,

    /// <summary>CIF/NIF de persona jurídica (empresa).</summary>
    NifEmpresa,

    /// <summary>Número de soporte de la TIE (tarjeta de extranjería) — no lleva dígito de control calculable.</summary>
    TieSoporte,

    /// <summary>No coincide con ningún formato español reconocido (p. ej. pasaporte extranjero).</summary>
    Otros
}

public readonly record struct ResultadoIdentificacion(bool EsValido, TipoIdentificacion Tipo);

/// <summary>
/// Detecta y valida (con dígito de control real) DNI, NIE y CIF/NIF de empresa
/// españoles, según el algoritmo oficial. No fuerza un único formato: un
/// trabajador puede ser extranjero con NIE, TIE o pasaporte, y una Empresa se
/// identifica con un CIF si es persona jurídica, pero con su DNI o su NIE si es
/// un autónomo — quién puede usar qué lo deciden los llamadores, no
/// <see cref="Analizar"/>, que solo dice qué es cada documento y si su dígito
/// de control cuadra. Para la identificación fiscal de una Empresa, el criterio
/// es <see cref="EsIdentificacionFiscalValida"/>.
/// </summary>
public static partial class ValidadorIdentificacion
{
    private const string LetrasControlPersona = "TRWAGMYFPDXBNJZSQVHLCKE";
    private const string LetrasControlEmpresa = "JABCDEFGHI";
    private const string LetrasOrganizacionDigitoNumerico = "ABEH";
    private const string LetrasOrganizacionDigitoLetra = "KPQS";

    /// <summary>
    /// Criterio único de identificación fiscal de una Empresa: DNI, NIE o NIF
    /// de empresa, los tres con dígito de control correcto. Un autónomo se
    /// identifica con su DNI o su NIE, así que restringirlo a
    /// <see cref="TipoIdentificacion.NifEmpresa"/> le impedía darse de alta.
    /// <para>
    /// Quedan fuera <see cref="TipoIdentificacion.TieSoporte"/> y
    /// <see cref="TipoIdentificacion.Otros"/>: el número de soporte de la TIE
    /// no es un identificador fiscal y no lleva dígito de control calculable
    /// —<see cref="Analizar"/> lo da por válido sin comprobar nada—, y
    /// aceptarlo metería texto sin verificar en un campo que sirve de ancla
    /// para reconocer a la misma organización. El identificador fiscal de un
    /// extranjero residente es su NIE, que sí se acepta. TALVEG solo opera en
    /// España por ahora (decisión del propietario, 2026-09-18); el día que haya
    /// un régimen extranjero, la dimensión que falta es el país, no ensanchar
    /// este criterio.
    /// </para>
    /// <para>
    /// No vale para identificar a una <c>persona</c>: un Trabajador puede
    /// acreditarse con pasaporte o con número de soporte de TIE, y por eso
    /// <c>Trabajador</c> y los lectores de importación de trabajadores tienen
    /// su propio criterio, deliberadamente más ancho que este.
    /// </para>
    /// </summary>
    public static bool EsIdentificacionFiscalValida(string? documento)
    {
        if (string.IsNullOrWhiteSpace(documento))
            return false;

        var resultado = Analizar(documento);

        return resultado.EsValido
            && resultado.Tipo is TipoIdentificacion.Dni
                or TipoIdentificacion.Nie
                or TipoIdentificacion.NifEmpresa;
    }

    /// <summary>
    /// Letra de control que corresponde a un DNI o NIE, calculada solo con su
    /// parte numérica: sirve para decir «la letra debería ser E», no solo «no
    /// cuadra». Acepta el DNI de siete dígitos, que existe (el cero a la
    /// izquierda se omite a menudo al escribirlo), y devuelve <c>null</c> si el
    /// documento no es un DNI ni un NIE. La letra que traiga, si trae, se ignora.
    /// </summary>
    public static char? LetraControlEsperada(string? documento)
    {
        if (string.IsNullOrWhiteSpace(documento))
            return null;

        var limpio = documento.Trim().ToUpperInvariant();
        var numerico = RegexParteNumericaDni().Match(limpio);
        if (numerico.Success)
            return LetrasControlPersona[int.Parse(numerico.Groups[1].Value) % 23];

        numerico = RegexParteNumericaNie().Match(limpio);
        if (numerico.Success)
        {
            var prefijo = limpio[0] switch { 'X' => '0', 'Y' => '1', _ => '2' };
            return LetrasControlPersona[int.Parse(prefijo + numerico.Groups[1].Value) % 23];
        }

        return null;
    }

    public static ResultadoIdentificacion Analizar(string documento)
    {
        if (string.IsNullOrWhiteSpace(documento))
            return new ResultadoIdentificacion(false, TipoIdentificacion.Otros);

        var limpio = documento.Trim().ToUpperInvariant();

        if (RegexDni().IsMatch(limpio))
        {
            var numero = int.Parse(limpio[..8]);
            var letraCalculada = LetrasControlPersona[numero % 23];
            return new ResultadoIdentificacion(limpio[8] == letraCalculada, TipoIdentificacion.Dni);
        }

        if (RegexNie().IsMatch(limpio))
        {
            var primeraTransformada = limpio[0] switch { 'X' => '0', 'Y' => '1', 'Z' => '2', _ => limpio[0] };
            var numero = int.Parse(primeraTransformada + limpio[1..8]);
            var letraCalculada = LetrasControlPersona[numero % 23];
            return new ResultadoIdentificacion(limpio[8] == letraCalculada, TipoIdentificacion.Nie);
        }

        if (RegexNifEmpresa().IsMatch(limpio))
            return new ResultadoIdentificacion(EsNifEmpresaValido(limpio), TipoIdentificacion.NifEmpresa);

        if (RegexTieSoporte().IsMatch(limpio))
            return new ResultadoIdentificacion(true, TipoIdentificacion.TieSoporte);

        return new ResultadoIdentificacion(false, TipoIdentificacion.Otros);
    }

    private static bool EsNifEmpresaValido(string limpio)
    {
        var letraOrganizacion = limpio[0];
        var digitos = limpio[1..8];
        var digitoControlUsuario = limpio[8];

        var sumaPares = 0;
        var sumaImpares = 0;
        for (var i = 0; i < digitos.Length; i++)
        {
            var num = digitos[i] - '0';
            if (i % 2 == 1)
            {
                sumaPares += num;
            }
            else
            {
                var multiplicado = num * 2;
                sumaImpares += multiplicado > 9 ? multiplicado - 9 : multiplicado;
            }
        }

        var residuo = (sumaPares + sumaImpares) % 10;
        var digitoCalculado = residuo == 0 ? 0 : 10 - residuo;
        var letraCalculada = LetrasControlEmpresa[digitoCalculado];
        var digitoCalculadoTexto = digitoCalculado.ToString()[0];

        if (LetrasOrganizacionDigitoNumerico.Contains(letraOrganizacion))
            return digitoControlUsuario == digitoCalculadoTexto;

        if (LetrasOrganizacionDigitoLetra.Contains(letraOrganizacion))
            return digitoControlUsuario == letraCalculada;

        return digitoControlUsuario == digitoCalculadoTexto || digitoControlUsuario == letraCalculada;
    }

    [GeneratedRegex(@"^\d{8}[A-Z]$")]
    private static partial Regex RegexDni();

    [GeneratedRegex(@"^[XYZ]\d{7}[A-Z]$")]
    private static partial Regex RegexNie();

    [GeneratedRegex(@"^[ABCDEFGHJUVNPQRSW]\d{7}[A-Z0-9]$")]
    private static partial Regex RegexNifEmpresa();

    [GeneratedRegex(@"^[A-Z]{3}\d{6}$")]
    private static partial Regex RegexTieSoporte();

    [GeneratedRegex(@"^(\d{7,8})[A-Z]?$")]
    private static partial Regex RegexParteNumericaDni();

    [GeneratedRegex(@"^[XYZ](\d{7})[A-Z]?$")]
    private static partial Regex RegexParteNumericaNie();
}
