using System.Text.RegularExpressions;

namespace CaeManager.Application.AsistenteIa.Preparacion;

/// <summary>Qué clase de identificador sustituye un marcador.</summary>
public enum TipoIdentificadorEnmascarado
{
    /// <summary>DNI o NIE de una persona, con o sin letra, válido o no.</summary>
    DocumentoPersona,

    /// <summary>Pasaporte o número de soporte de TIE.</summary>
    Pasaporte,

    /// <summary>CIF / NIF de persona jurídica.</summary>
    NifEmpresa,

    /// <summary>Dirección de correo electrónico.</summary>
    Correo,
}

/// <summary>
/// Un identificador retirado del texto. <see cref="ValorOriginal"/> es el tramo tal
/// como se escribió, para devolverlo al texto; <see cref="ValorNormalizado"/> es el
/// que consumen las validaciones y los Commands (sin espacios ni guiones, en
/// mayúsculas, y el DNI de siete dígitos completado con el cero).
/// </summary>
public sealed record IdentificadorEnmascarado(
    string Marcador,
    string ValorOriginal,
    string ValorNormalizado,
    TipoIdentificadorEnmascarado Tipo);

/// <summary>Resultado del enmascarado: el texto que puede salir de TALVEG y la tabla para deshacerlo.</summary>
public sealed record TextoEnmascarado(string Texto, IReadOnlyList<IdentificadorEnmascarado> Identificadores)
{
    /// <summary>El identificador que corresponde a un marcador, o <c>null</c> si el marcador no es de esta tabla.</summary>
    public IdentificadorEnmascarado? Buscar(string marcador) =>
        Identificadores.FirstOrDefault(i => i.Marcador == marcador);

    /// <summary>
    /// Devuelve a un texto los valores originales de sus marcadores. Solo sustituye
    /// marcadores de esta tabla: un marcador que el modelo se invente se queda como
    /// está, y así se ve.
    /// </summary>
    public string Restaurar(string texto) =>
        Identificadores.Aggregate(texto, (acumulado, i) => acumulado.Replace(i.Marcador, i.ValorOriginal, StringComparison.Ordinal));
}

/// <summary>
/// Paso 1 del asistente de flujos: los identificadores personales y fiscales no
/// salen de TALVEG. DNI, NIE, pasaporte, CIF y correos se sustituyen por marcadores
/// (<c>[DOC_1]</c>, <c>[CIF_1]</c>, <c>[CORREO_1]</c>) antes de que el texto viaje al
/// proveedor de IA, y se restauran al volver.
/// <para>
/// Medido con el proveedor (propuesta del asistente, § 4.3.5): con marcadores la
/// selección acierta igual que en claro. Lo que <b>no</b> se enmascara, a propósito:
/// el Centro y el Cliente empresarial, porque son justo lo que hay que casar contra el
/// catálogo —enmascarados, el modelo se abstiene—, y las fechas, que son el dato que se
/// extrae. Los nombres de persona tampoco: el flujo los resuelve contra la base.
/// </para>
/// <para>
/// Enmascara el documento aunque su letra de control no cuadre: un DNI mal tecleado
/// sigue siendo el DNI de alguien. La validez la juzga
/// <see cref="ValidacionesDeterministasOrden"/>, no esta clase.
/// </para>
/// <para>
/// Esto reduce el dato personal que sale, pero no lo elimina —la orden sigue siendo
/// texto libre escrito por una persona— y no sustituye al régimen de subencargados.
/// </para>
/// </summary>
public static partial class EnmascaradorIdentificadores
{
    public static TextoEnmascarado Enmascarar(string? texto)
    {
        if (string.IsNullOrEmpty(texto))
            return new TextoEnmascarado(texto ?? string.Empty, []);

        // Un tramo que ya tenga forma de marcador se desactiva antes de nada. Si no,
        // quien escriba «[DOC_1]» en la orden haría que Restaurar le pusiera el DNI
        // de otra persona en ese sitio.
        texto = RegexMarcadorLiteral().Replace(texto, m => "(" + m.Value[1..^1] + ")");

        var tramos = BuscarTramos(texto);
        if (tramos.Count == 0)
            return new TextoEnmascarado(texto, []);

        var identificadores = new List<IdentificadorEnmascarado>();
        var porValor = new Dictionary<(TipoIdentificadorEnmascarado, string), string>();
        var contadores = new Dictionary<string, int>();
        var resultado = new System.Text.StringBuilder(texto.Length);
        var cursor = 0;

        foreach (var tramo in tramos)
        {
            resultado.Append(texto, cursor, tramo.Inicio - cursor);
            var original = texto.Substring(tramo.Inicio, tramo.Longitud);
            var normalizado = Normalizar(original, tramo.Tipo);

            // El mismo documento escrito dos veces es la misma persona: el mismo
            // marcador. Si recibiera dos, el modelo podría tratarlas como dos.
            if (!porValor.TryGetValue((tramo.Tipo, normalizado), out var marcador))
            {
                var prefijo = Prefijo(tramo.Tipo);
                contadores[prefijo] = contadores.GetValueOrDefault(prefijo) + 1;
                marcador = $"[{prefijo}_{contadores[prefijo]}]";
                porValor[(tramo.Tipo, normalizado)] = marcador;
                identificadores.Add(new IdentificadorEnmascarado(marcador, original, normalizado, tramo.Tipo));
            }

            resultado.Append(marcador);
            cursor = tramo.Inicio + tramo.Longitud;
        }

        resultado.Append(texto, cursor, texto.Length - cursor);
        return new TextoEnmascarado(resultado.ToString(), identificadores);
    }

    private readonly record struct Tramo(int Inicio, int Longitud, TipoIdentificadorEnmascarado Tipo);

    private static List<Tramo> BuscarTramos(string texto)
    {
        // El orden importa solo para desempatar dos tramos que empiezan en el mismo
        // sitio: gana el primero de esta lista. El correo va delante porque su parte
        // local puede contener algo con forma de DNI.
        var candidatos = new List<Tramo>();
        Añadir(candidatos, RegexCorreo(), texto, TipoIdentificadorEnmascarado.Correo);
        Añadir(candidatos, RegexNie(), texto, TipoIdentificadorEnmascarado.DocumentoPersona);
        Añadir(candidatos, RegexDni(), texto, TipoIdentificadorEnmascarado.DocumentoPersona);
        Añadir(candidatos, RegexDniSinLetraTrasPalabraClave(), texto, TipoIdentificadorEnmascarado.DocumentoPersona);
        Añadir(candidatos, RegexCif(), texto, TipoIdentificadorEnmascarado.NifEmpresa);
        Añadir(candidatos, RegexPasaporteOTie(), texto, TipoIdentificadorEnmascarado.Pasaporte);
        Añadir(candidatos, RegexPasaporteTrasPalabraClave(), texto, TipoIdentificadorEnmascarado.Pasaporte);

        var elegidos = new List<Tramo>();
        var fin = 0;
        foreach (var tramo in candidatos
                     .Select((t, orden) => (t, orden))
                     .OrderBy(x => x.t.Inicio)
                     .ThenByDescending(x => x.t.Longitud)
                     .ThenBy(x => x.orden)
                     .Select(x => x.t))
        {
            if (tramo.Inicio < fin)
                continue;
            elegidos.Add(tramo);
            fin = tramo.Inicio + tramo.Longitud;
        }

        return elegidos;
    }

    private static void Añadir(List<Tramo> destino, Regex regex, string texto, TipoIdentificadorEnmascarado tipo)
    {
        foreach (Match m in regex.Matches(texto))
        {
            // Las expresiones con palabra clave capturan solo el valor en el grupo
            // «valor»: la palabra «DNI» o «pasaporte» se queda en el texto, porque le
            // dice al modelo qué es el marcador.
            var g = m.Groups["valor"];
            var grupo = g.Success ? g : m.Groups[0];
            destino.Add(new Tramo(grupo.Index, grupo.Length, tipo));
        }
    }

    private static string Normalizar(string valor, TipoIdentificadorEnmascarado tipo)
    {
        if (tipo == TipoIdentificadorEnmascarado.Correo)
            return valor.Trim().ToLowerInvariant();

        var compacto = RegexSeparadores().Replace(valor, string.Empty).ToUpperInvariant();
        if (tipo == TipoIdentificadorEnmascarado.DocumentoPersona && char.IsDigit(compacto[0]))
        {
            var digitos = compacto.TakeWhile(char.IsDigit).Count();
            if (digitos == 7)
                compacto = "0" + compacto;
        }

        return compacto;
    }

    private static string Prefijo(TipoIdentificadorEnmascarado tipo) => tipo switch
    {
        TipoIdentificadorEnmascarado.NifEmpresa => "CIF",
        TipoIdentificadorEnmascarado.Correo => "CORREO",
        _ => "DOC",
    };

    [GeneratedRegex(@"\[(?:DOC|CIF|CORREO)_\d+\]", RegexOptions.IgnoreCase)]
    private static partial Regex RegexMarcadorLiteral();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}")]
    private static partial Regex RegexCorreo();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[XYZ][\s\-]?\d{7}[\s\-]?[A-Z](?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex RegexNie();

    // Siete u ocho dígitos y una letra. El de siete existe: el cero de delante se
    // omite a menudo al escribirlo.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])\d{7,8}[\s\-]?[A-Z](?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex RegexDni();

    // Sin letra solo cuando lo precede la palabra que dice qué es: siete u ocho
    // dígitos sueltos pueden ser cualquier otra cosa.
    [GeneratedRegex(@"\b(?:DNI|NIF|NIE|documento)\b\s*(?:n[º°o.]*\s*)?:?\s*(?<valor>\d{7,8})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex RegexDniSinLetraTrasPalabraClave();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[ABCDEFGHJNPQRSUVW][\s\-]?\d{7}[\s\-]?[0-9A-J](?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex RegexCif();

    // Formato del pasaporte español y del número de soporte de la TIE.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])[A-Z]{3}\d{6}(?![\p{L}\p{N}])")]
    private static partial Regex RegexPasaporteOTie();

    // Un pasaporte extranjero no tiene formato fijo: se reconoce por la palabra que
    // lo precede, y el valor tiene que llevar algún dígito («pasaporte francés» no
    // es un número).
    [GeneratedRegex(@"\bpasaporte\b\s*(?:n[º°o.]*\s*)?:?\s*(?<valor>(?=[A-Z0-9]*\d)[A-Z0-9]{5,12})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex RegexPasaporteTrasPalabraClave();

    [GeneratedRegex(@"[\s\-]")]
    private static partial Regex RegexSeparadores();
}
