namespace CaeManager.Domain.Inbound.ContratosAcceso;

/// <summary>
/// Las condiciones del contrato de acceso que fija el Tenant TCE (plan backend Inbound § 2.2, B-05): tipo (CA-10),
/// propia actividad (R-7), nivel máximo de subcontrata (0 a 3) y cupos de Trabajadores asignados y de subcontratas.
///
/// <para>
/// Todo es opcional: null es «sin fijar» (los contratos anteriores a B-05 no tienen ninguna, y el alta agrupada que las
/// pide llega en B-06). En un cupo, null es «sin límite»; si hay algún cupo, el motivo es obligatorio (plan § 2.2). Qué
/// hace el sistema con el cupo (no confirmar por encima, duda 4) y con el nivel (subcontrata de nivel ≤ máximo) entra con
/// B-17 y B-19: aquí solo se guardan y se validan sus límites.
/// </para>
/// </summary>
public sealed record CondicionesContratoAcceso
{
    public const int NivelSubcontrataMaximo = 3;
    public const int LongitudMaximaMotivoCupo = 500;

    public static readonly CondicionesContratoAcceso SinFijar = new(null, null, null, null, null, null);

    public TipoContratoAcceso? Tipo { get; }
    public PropiaActividadContrato? PropiaActividad { get; }
    public int? NivelMaxSubcontrata { get; }
    public int? CupoTrabajadores { get; }
    public int? CupoSubcontratas { get; }
    public string? MotivoCupo { get; }

    public CondicionesContratoAcceso(
        TipoContratoAcceso? tipo, PropiaActividadContrato? propiaActividad, int? nivelMaxSubcontrata,
        int? cupoTrabajadores, int? cupoSubcontratas, string? motivoCupo)
    {
        if (tipo is { } t && !Enum.IsDefined(t))
            throw new ArgumentException("El tipo de contrato no es válido.", nameof(tipo));
        if (propiaActividad is { } p && !Enum.IsDefined(p))
            throw new ArgumentException("La respuesta de propia actividad no es válida.", nameof(propiaActividad));
        if (nivelMaxSubcontrata is < 0 or > NivelSubcontrataMaximo)
            throw new ArgumentException(
                $"El nivel máximo de subcontrata va de 0 a {NivelSubcontrataMaximo}.", nameof(nivelMaxSubcontrata));
        if (cupoTrabajadores is < 0)
            throw new ArgumentException("El cupo de Trabajadores no puede ser negativo.", nameof(cupoTrabajadores));
        if (cupoSubcontratas is < 0)
            throw new ArgumentException("El cupo de subcontratas no puede ser negativo.", nameof(cupoSubcontratas));

        var motivo = string.IsNullOrWhiteSpace(motivoCupo) ? null : motivoCupo.Trim();
        if (motivo?.Length > LongitudMaximaMotivoCupo)
            throw new ArgumentException(
                $"El motivo del cupo no puede superar {LongitudMaximaMotivoCupo} caracteres.", nameof(motivoCupo));
        var hayCupo = cupoTrabajadores is not null || cupoSubcontratas is not null;
        if (hayCupo && motivo is null)
            throw new ArgumentException("Si el contrato tiene cupo, el motivo del cupo es obligatorio.", nameof(motivoCupo));
        if (!hayCupo && motivo is not null)
            throw new ArgumentException("Sin cupo no hay motivo de cupo.", nameof(motivoCupo));

        Tipo = tipo;
        PropiaActividad = propiaActividad;
        NivelMaxSubcontrata = nivelMaxSubcontrata;
        CupoTrabajadores = cupoTrabajadores;
        CupoSubcontratas = cupoSubcontratas;
        MotivoCupo = motivo;
    }
}
