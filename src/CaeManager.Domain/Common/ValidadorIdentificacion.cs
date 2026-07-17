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
/// españoles, según el algoritmo oficial. No fuerza un único formato — un
/// trabajador puede ser extranjero con NIE, TIE o pasaporte, y una empresa se
/// identifica con CIF, nunca con DNI.
/// </summary>
public static partial class ValidadorIdentificacion
{
    private const string LetrasControlPersona = "TRWAGMYFPDXBNJZSQVHLCKE";
    private const string LetrasControlEmpresa = "JABCDEFGHI";
    private const string LetrasOrganizacionDigitoNumerico = "ABEH";
    private const string LetrasOrganizacionDigitoLetra = "KPQS";

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
}
