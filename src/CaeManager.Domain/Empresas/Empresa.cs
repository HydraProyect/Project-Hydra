using CaeManager.Domain.Common;

namespace CaeManager.Domain.Empresas;

/// <summary>
/// Empresa contratista cuyo personal se coordina (la organización que opera
/// CAE Manager). Puede haber más de una razón social — p. ej. una entidad
/// para personal nacional y otra para personal extranjero.
/// </summary>
public class Empresa : EntidadBase
{
    public const int LongitudMaximaRazonSocial = 200;
    public const int LongitudCif = 9;
    public const int LongitudMaximaCnae = 10;
    public const int LongitudMaximaConvenioAplicable = 300;

    public string RazonSocial { get; private set; } = string.Empty;

    /// <summary>
    /// A diferencia de Cliente, el CIF de Empresa es opcional — hay Empresas
    /// ya creadas (sembradas o importadas) sin CIF, y las plantillas de
    /// importación tampoco lo recogen todavía (ver ROADMAP.md). El alta
    /// nueva (CrearEmpresaCommand) sí lo exige para MVP-1 (Escenario 2,
    /// tecnico/docs/MULTITENANCY.md § 2): sin CIF no se puede emitir un F-22
    /// válido — va en cabecera y en la cláusula RGPD.
    /// </summary>
    public string? Cif { get; private set; }

    /// <summary>Código CNAE de la actividad — eje E1 de la capa sectorial (MATRIZ_SECTORIAL_PRL.md § 9.1).</summary>
    public string? Cnae { get; private set; }

    /// <summary>Convenio colectivo aplicado — eje E2 de la capa sectorial. Lo declara el cliente, nunca se deduce.</summary>
    public string? ConvenioAplicable { get; private set; }

    /// <summary>
    /// Si la actividad de la Empresa está en el Anexo I del RSP — determina si
    /// el supuesto b) del art. 32 bis.1 LPRL aplica al designar un recurso
    /// preventivo (F-40) y si el tope de F-13/F-05 por plantilla reducida es
    /// aplicable.
    /// </summary>
    public bool EsActividadAnexoI { get; private set; }

    /// <summary>
    /// F3 (verificación del modelo real, `f3-diseno-fisico-empresa-unificada…`
    /// §3) — <c>true</c> para las filas que ya eran Empresa antes de la
    /// unificación, <c>false</c> para las filas incorporadas desde
    /// Cliente/Subcontrata. NOT NULL: no hay "no aplica" para esta columna,
    /// a diferencia de las cuatro de abajo.
    /// </summary>
    public bool EsPropia { get; private set; } = true;

    /// <summary>
    /// Deuda transitoria de F3 (§2 del diseño físico) — antiguo
    /// <c>Cliente.EjecutivoUsuarioId</c>. NULL = "no aplica" (no es una fila
    /// ex-Cliente). Se retira cuando F4 redefina "gestor principal" con
    /// carteras múltiples por relación — no antes.
    /// </summary>
    public Guid? EjecutivoUsuarioId { get; private set; }

    /// <summary>
    /// Deuda transitoria de F3 — antiguo <c>Cliente.EsCritico</c>. NULL =
    /// "no aplica". Destino final: <c>RelacionEmpresarial</c> (F4).
    /// </summary>
    public bool? EsCritico { get; private set; }

    /// <summary>
    /// Deuda transitoria de F3 — antiguo <c>Cliente.Notas</c>. NULL =
    /// "no aplica". Destino final: <c>RelacionEmpresarial</c> (F4).
    /// </summary>
    public string? Notas { get; private set; }

    /// <summary>
    /// Deuda transitoria de F3 — antiguo <c>Subcontrata.NivelServicio</c>.
    /// Texto y no el enum <c>NivelServicioSubcontrata</c> a propósito: evita
    /// acoplar <c>Domain.Empresas</c> a <c>Domain.Subcontratas</c> por una
    /// columna que F4 va a retirar. NULL = "no aplica" (no es una fila
    /// ex-Subcontrata).
    /// </summary>
    public string? NivelServicio { get; private set; }

    private Empresa()
    {
    }

    public Empresa(string razonSocial, string? cif = null, string? cnae = null, string? convenioAplicable = null, bool esActividadAnexoI = false)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
        EstablecerCnae(cnae);
        EstablecerConvenioAplicable(convenioAplicable);
        EsActividadAnexoI = esActividadAnexoI;
        EsPropia = true;
    }

    public void Actualizar(string razonSocial, string? cif, string? cnae = null, string? convenioAplicable = null, bool esActividadAnexoI = false)
    {
        EstablecerRazonSocial(razonSocial);
        EstablecerCif(cif);
        EstablecerCnae(cnae);
        EstablecerConvenioAplicable(convenioAplicable);
        EsActividadAnexoI = esActividadAnexoI;
    }

    private void EstablecerRazonSocial(string razonSocial)
    {
        if (string.IsNullOrWhiteSpace(razonSocial))
            throw new ArgumentException("La razón social es obligatoria.", nameof(razonSocial));

        var normalizada = razonSocial.Trim();

        if (normalizada.Length > LongitudMaximaRazonSocial)
            throw new ArgumentException(
                $"La razón social no puede superar {LongitudMaximaRazonSocial} caracteres.", nameof(razonSocial));

        RazonSocial = normalizada;
    }

    private void EstablecerCif(string? cif)
    {
        if (string.IsNullOrWhiteSpace(cif))
        {
            Cif = null;
            return;
        }

        var normalizado = cif.Trim().ToUpperInvariant();
        var resultado = ValidadorIdentificacion.Analizar(normalizado);

        if (resultado.Tipo != TipoIdentificacion.NifEmpresa || !resultado.EsValido)
            throw new ArgumentException("El CIF no es válido.", nameof(cif));

        Cif = normalizado;
    }

    private void EstablecerCnae(string? cnae)
    {
        if (string.IsNullOrWhiteSpace(cnae))
        {
            Cnae = null;
            return;
        }

        var normalizado = cnae.Trim();
        if (normalizado.Length > LongitudMaximaCnae)
            throw new ArgumentException($"El CNAE no puede superar {LongitudMaximaCnae} caracteres.", nameof(cnae));

        Cnae = normalizado;
    }

    private void EstablecerConvenioAplicable(string? convenioAplicable)
    {
        if (string.IsNullOrWhiteSpace(convenioAplicable))
        {
            ConvenioAplicable = null;
            return;
        }

        var normalizado = convenioAplicable.Trim();
        if (normalizado.Length > LongitudMaximaConvenioAplicable)
            throw new ArgumentException(
                $"El convenio aplicable no puede superar {LongitudMaximaConvenioAplicable} caracteres.", nameof(convenioAplicable));

        ConvenioAplicable = normalizado;
    }
}
