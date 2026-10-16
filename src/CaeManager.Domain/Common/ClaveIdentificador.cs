namespace CaeManager.Domain.Common;

/// <summary>Tipo de documento que distingue una clave de agrupación.</summary>
public enum TipoClaveIdentificador
{
    /// <summary>DNI español.</summary>
    Dni,
    /// <summary>NIE español.</summary>
    Nie,
    /// <summary>NIF de Empresa.</summary>
    NifEmpresa,
    /// <summary>Pasaporte identificado también por su país.</summary>
    Pasaporte
}

/// <summary>
/// Clave por valor para agrupar requisitos en vistas de solo lectura, sin otorgar autorización.
/// Un NIE y un DNI generan claves distintas; su vinculación será una acción manual y auditada en otro incremento.
/// La clave no lleva el tipo de entidad: la de un autónomo (Empresa contratista con DNI) y la de un Trabajador con el
/// mismo DNI son iguales, y quien agrupe debe filtrar por entidad. Las fábricas devuelven <c>null</c> tanto para un
/// documento inválido como para uno válido de otra familia (NIF de persona física con K, TIE); no se agrupa por <c>null</c>.
/// Un pasaporte sin país oficial ISO 3166-1 (p. ej. Kosovo, XK, que no es código asignado) no genera clave.
/// </summary>
public sealed record ClaveIdentificador
{
    // Lista cerrada de los 249 códigos oficialmente asignados ISO 3166-1 alfa-2.
    // Fuente: https://www.iso.org/iso-3166-country-codes.html
    private static readonly HashSet<string> Paises = new((
        "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ " +
        "CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR " +
        "GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT " +
        "JE JM JO JP KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT MU MV MW MX MY MZ " +
        "NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW " +
        "SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ " +
        "UA UG UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW").Split(' '), StringComparer.Ordinal);

    private ClaveIdentificador(TipoClaveIdentificador tipo, string valor, string? pais = null)
    {
        Tipo = tipo;
        Valor = valor;
        Pais = pais;
    }

    /// <summary>Tipo de documento de la clave.</summary>
    public TipoClaveIdentificador Tipo { get; }
    /// <summary>Número normalizado, conservando sus ceros iniciales.</summary>
    public string Valor { get; }
    /// <summary>Código ISO 3166-1 alfa-2, solo para pasaportes.</summary>
    public string? Pais { get; }

    /// <summary>Crea una clave de DNI, NIE o NIF de Empresa con control correcto; en otro caso devuelve <c>null</c>.</summary>
    public static ClaveIdentificador? DesdeDocumentoEspanol(string? documento)
    {
        if (documento is null || documento.Any(NoEsAsciiNiEspacio))
            return null;

        var valor = ValidadorIdentificacion.Normalizar(documento);
        if (valor is null || valor.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')))
            return null;

        var resultado = ValidadorIdentificacion.Analizar(valor);
        if (!resultado.EsValido)
            return null;

        return resultado.Tipo switch
        {
            TipoIdentificacion.Dni => new(TipoClaveIdentificador.Dni, valor),
            TipoIdentificacion.Nie => new(TipoClaveIdentificador.Nie, valor),
            TipoIdentificacion.NifEmpresa => new(TipoClaveIdentificador.NifEmpresa, valor),
            _ => null
        };
    }

    /// <summary>Crea una clave de pasaporte alfanumérico de 5 a 20 caracteres con país oficial obligatorio.</summary>
    public static ClaveIdentificador? DesdePasaporte(string? pais, string? numero)
    {
        if (pais is null || numero is null || pais.Any(NoEsAsciiNiEspacio) || numero.Any(NoEsAsciiNiEspacio))
            return null;

        var codigoPais = pais.Trim().ToUpperInvariant();
        if (!Paises.Contains(codigoPais))
            return null;

        var valor = new string(numero.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
        if (valor.Length is < 5 or > 20
            || valor.Any(c => c is not (>= 'A' and <= 'Z') and not (>= '0' and <= '9')))
            return null;

        return new(TipoClaveIdentificador.Pasaporte, valor, codigoPais);
    }

    private static bool NoEsAsciiNiEspacio(char c) => c > 127 && !char.IsWhiteSpace(c);

    /// <summary>Representación estable que incluye el tipo y, para pasaportes, el país.</summary>
    public override string ToString() => Tipo switch
    {
        TipoClaveIdentificador.Dni => $"DNI:{Valor}",
        TipoClaveIdentificador.Nie => $"NIE:{Valor}",
        TipoClaveIdentificador.NifEmpresa => $"NIF:{Valor}",
        _ => $"PAS:{Pais}:{Valor}"
    };
}
