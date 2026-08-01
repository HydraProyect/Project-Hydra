namespace CaeManager.Domain.Common;

/// <summary>
/// Value object de un documento de identidad de persona (P2 #27 de
/// docs/business/MATURITY_REVIEW.md — no había ninguno en el dominio antes
/// de este cambio, solo <see cref="string"/> con validación repetida en
/// cada constructor). Acepta DNI, NIE, número de soporte TIE o cualquier
/// otro documento extranjero (pasaporte) — un trabajador no tiene por qué
/// ser español — y solo exige el dígito de control real cuando el formato
/// encaja con uno calculable (DNI/NIE), igual que ya validaba a mano el
/// constructor de <c>Trabajador</c> antes de este cambio.
///
/// <b>Sin migrar todavía ninguna entidad existente a este tipo</b>: Dni ya
/// se usa como <see cref="string"/> en ~95 sitios de 44 archivos (Domain,
/// Application, Infrastructure, Web, tests) — cambiar el tipo de
/// <c>Trabajador.Dni</c> ahora sería un cambio mecánico de alto volumen sin
/// poder compilar/ejecutar los tests en este entorno para verificarlo. Este
/// tipo queda disponible para código nuevo y como paso previo a migrar el
/// existente cuando se pueda validar con el SDK de .NET presente.
/// </summary>
public readonly record struct Dni
{
    public string Valor { get; }

    private Dni(string valor) => Valor = valor;

    public static Dni Crear(string valor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valor);

        var normalizado = valor.Trim().ToUpperInvariant();
        var resultado = ValidadorIdentificacion.Analizar(normalizado);

        // Solo se rechaza si el formato encaja con un tipo que lleva dígito
        // de control calculable (DNI/NIE/CIF de empresa) y ese dígito no
        // cuadra. Un TIE o un pasaporte extranjero (Tipo.Otros/TieSoporte)
        // se acepta sin validación estricta — no hay algoritmo de control
        // que aplicarles.
        if (!resultado.EsValido && resultado.Tipo is TipoIdentificacion.Dni or TipoIdentificacion.Nie or TipoIdentificacion.NifEmpresa)
        {
            var etiqueta = resultado.Tipo switch
            {
                TipoIdentificacion.Dni => "DNI",
                TipoIdentificacion.Nie => "NIE",
                _ => "CIF"
            };
            throw new ArgumentException($"El dígito de control del {etiqueta} no es válido.", nameof(valor));
        }

        return new Dni(normalizado);
    }

    public override string ToString() => Valor;

    public static implicit operator string(Dni dni) => dni.Valor;
}
