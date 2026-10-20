using System.Text.RegularExpressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Trabajadores;

/// <summary>Los únicos tipos de documento de identidad de un Trabajador (decisión de Chris, 2026-10-20).</summary>
public enum TipoDocumentoTrabajador
{
    Dni,
    Nie,
    Pasaporte,
}

/// <summary>
/// Resultado de clasificar un documento de identidad de Trabajador. Si <see cref="EsValido"/>, <see cref="Normalizado"/> es
/// lo que se guarda y <see cref="Tipo"/> su tipo; si no, <see cref="Error"/> dice por qué se rechaza.
/// </summary>
public sealed record ResultadoDocumentoTrabajador(
    bool EsValido, TipoDocumentoTrabajador? Tipo, string? Normalizado, string? Error)
{
    /// <summary>
    /// El valor no es un DNI ni un NIE y se guarda como Pasaporte: la pantalla avisa con
    /// <see cref="CriterioDocumentoIdentidadTrabajador.AvisoPasaporte"/> antes de guardar.
    /// </summary>
    public bool SeGuardaComoPasaporte => EsValido && Tipo == TipoDocumentoTrabajador.Pasaporte;
}

/// <summary>
/// Criterio único del documento de identidad de un Trabajador (decisión de Chris, 2026-10-20). Lo aplican el alta manual,
/// la alta guiada, la importación masiva, la detección de altas por RNT (todas pasan por el constructor de
/// <see cref="Trabajador"/>) y la corrección del documento desde la ficha 360.
///
/// <list type="bullet">
/// <item><b>Obligatorio</b>: vacío o solo separadores se rechaza.</item>
/// <item><b>Separadores</b>: se quitan espacios, guiones y puntos (<see cref="ValidadorIdentificacion.Normalizar"/>) y se
/// pasa a mayúsculas.</item>
/// <item><b>DNI y NIE</b>: lo que tras normalizar tiene forma de DNI o de NIE con su letra de control es ese tipo. Con la
/// forma pero con la letra mal se rechaza diciendo qué letra corresponde: un error de tecleo no puede convertir a un
/// español o a un residente en un pasaporte cobrable.</item>
/// <item><b>Pasaporte</b>: cualquier otro valor, incluido el DNI sin letra («12345678») y el número de soporte de la TIE,
/// que deja de ser un tipo propio del Trabajador. Se exige un pasaporte válido: solo letras y dígitos, sin espacios
/// internos, con al menos un dígito y entre <see cref="Trabajador.LongitudMinimaDni"/> y
/// <see cref="Trabajador.LongitudMaximaDni"/> caracteres. Así se rechazan los marcadores («PENDIENTE», «SIN DNI»,
/// «-», etc.).</item>
/// </list>
///
/// No sustituye a <see cref="ValidadorIdentificacion"/>, que sigue siendo el criterio común de Empresa, Cliente
/// empresarial y Subcontrata (y conserva <see cref="TipoIdentificacion.TieSoporte"/> para ellos).
/// </summary>
public static partial class CriterioDocumentoIdentidadTrabajador
{
    /// <summary>Aviso del formulario cuando el valor se guardará como Pasaporte.</summary>
    public const string AvisoPasaporte = "No coincide con un DNI ni un NIE: se guardará como Pasaporte";

    private const string LetrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";

    public static ResultadoDocumentoTrabajador Clasificar(string? valor)
    {
        if (string.IsNullOrWhiteSpace(valor))
            return Rechazo("El documento de identidad es obligatorio.");

        var normalizado = ValidadorIdentificacion.Normalizar(valor);
        if (normalizado is null)
            return Rechazo("El documento de identidad es obligatorio.");

        if (TipoEspanol(normalizado) is { } tipo)
        {
            var esperada = LetraEsperada(normalizado);
            if (normalizado[8] != esperada)
            {
                var etiqueta = tipo == TipoDocumentoTrabajador.Dni ? "DNI" : "NIE";
                return Rechazo($"La letra de control del {etiqueta} no es válida: debería ser {esperada}.");
            }

            return new ResultadoDocumentoTrabajador(true, tipo, normalizado, null);
        }

        // Pasaporte: los espacios internos no son un separador de pasaporte, son la señal de un marcador («SIN DNI»).
        if (valor.Trim().Any(char.IsWhiteSpace))
            return Rechazo("El documento de identidad no puede llevar espacios: escribe el número del DNI, NIE o pasaporte.");

        if (!normalizado.Any(char.IsAsciiDigit))
            return Rechazo("El documento de identidad debe llevar algún número: escribe el número del DNI, NIE o pasaporte.");

        if (!RegexPasaporte().IsMatch(normalizado))
            return Rechazo("El documento de identidad solo admite letras y números.");

        if (normalizado.Length < Trabajador.LongitudMinimaDni || normalizado.Length > Trabajador.LongitudMaximaDni)
            return Rechazo(
                $"El documento de identidad debe tener entre {Trabajador.LongitudMinimaDni} y {Trabajador.LongitudMaximaDni} caracteres.");

        return new ResultadoDocumentoTrabajador(true, TipoDocumentoTrabajador.Pasaporte, normalizado, null);
    }

    /// <summary>
    /// Tipo de un documento ya guardado (<see cref="Trabajador.Dni"/>), sin volver a validarlo: forma de DNI o de NIE es
    /// ese tipo (aunque un dato antiguo traiga la letra mal); cualquier otro valor, Pasaporte. <c>null</c> si no hay
    /// documento (Trabajador anonimizado).
    /// </summary>
    public static TipoDocumentoTrabajador? TipoGuardado(string? documento)
    {
        var normalizado = ValidadorIdentificacion.Normalizar(documento);
        if (normalizado is null)
            return null;

        return TipoEspanol(normalizado) ?? TipoDocumentoTrabajador.Pasaporte;
    }

    private static TipoDocumentoTrabajador? TipoEspanol(string normalizado) =>
        RegexDni().IsMatch(normalizado) ? TipoDocumentoTrabajador.Dni
        : RegexNie().IsMatch(normalizado) ? TipoDocumentoTrabajador.Nie
        : null;

    private static char LetraEsperada(string normalizado)
    {
        var digitos = normalizado[0] switch
        {
            'X' => "0" + normalizado[1..8],
            'Y' => "1" + normalizado[1..8],
            'Z' => "2" + normalizado[1..8],
            _ => normalizado[..8],
        };
        return LetrasControl[int.Parse(digitos) % 23];
    }

    private static ResultadoDocumentoTrabajador Rechazo(string error) => new(false, null, null, error);

    // [0-9] y no \d: \d admite dígitos Unicode, que LetraEsperada no puede convertir con int.Parse.
    [GeneratedRegex(@"^[0-9]{8}[A-Z]$")]
    private static partial Regex RegexDni();

    [GeneratedRegex(@"^[XYZ][0-9]{7}[A-Z]$")]
    private static partial Regex RegexNie();

    [GeneratedRegex(@"^[A-Z0-9]+$")]
    private static partial Regex RegexPasaporte();
}
