namespace CaeManager.Domain.Common;

/// <summary>
/// Value object del CIF de una empresa española (P2 #27 de
/// docs/business/MATURITY_REVIEW.md). A diferencia de <see cref="Dni"/>, no
/// acepta nada que no sea un CIF real: exige tipo <see cref="TipoIdentificacion.NifEmpresa"/>
/// con dígito de control válido, siempre — igual de estricto que ya
/// validaban a mano los constructores de <c>Cliente</c>/<c>Empresa</c> antes
/// de este cambio. Que Empresa admita CIF opcional se sigue expresando
/// envolviendo este tipo en <c>Cif?</c>, no relajando la validación de aquí.
///
/// Sin migrar todavía ninguna entidad existente a este tipo — mismo motivo
/// que <see cref="Dni"/>: ~57 sitios en 30 archivos ya lo usan como
/// <see cref="string"/>, un volumen que no se puede migrar de forma segura
/// sin poder compilar/ejecutar los tests en este entorno.
/// </summary>
public readonly record struct Cif
{
    public string Valor { get; }

    private Cif(string valor) => Valor = valor;

    public static Cif Crear(string valor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(valor);

        var normalizado = valor.Trim().ToUpperInvariant();
        var resultado = ValidadorIdentificacion.Analizar(normalizado);

        if (resultado.Tipo != TipoIdentificacion.NifEmpresa || !resultado.EsValido)
            throw new ArgumentException($"\"{valor}\" no es un CIF válido.", nameof(valor));

        return new Cif(normalizado);
    }

    public override string ToString() => Valor;

    public static implicit operator string(Cif cif) => cif.Valor;
}
