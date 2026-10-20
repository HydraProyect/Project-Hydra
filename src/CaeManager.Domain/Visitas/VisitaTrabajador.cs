using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Domain.Visitas;

/// <summary>
/// Trabajador incluido en una Visita — mismo patrón que SubcontrataCliente:
/// sin ciclo de vida propio, desvincular es una baja física, no un soft
/// delete. Una Visita puede implicar a varios Trabajadores (un equipo que
/// entra junto al Centro).
/// </summary>
public class VisitaTrabajador : EntidadConTenant
{
    public Guid VisitaId { get; private set; }
    public Guid TrabajadorId { get; private set; }

    /// <summary>
    /// Cuándo se asignó el Trabajador a la Visita: la «fecha de solicitud» de la exportación de Trabajadores
    /// extranjeros del mes (decisión de Chris, 2026-10-20). <c>null</c> en las filas anteriores a esta columna.
    /// </summary>
    public DateTime? FechaAsignacionUtc { get; private set; }

    /// <summary>
    /// La persona que asignó el Trabajador a la Visita: el Actor real (<c>ActorAuditoria.ActorRealUsuarioId</c>), nunca
    /// el Usuario simulado de una impersonación ni el principal de la Asignación de Cartera. Es el «Gestor CAE que lo
    /// gestionó» de la exportación de Trabajadores extranjeros del mes. <c>null</c> en las filas anteriores a esta
    /// columna y en las altas sin identidad resuelta (procesos de fondo, siembras).
    /// </summary>
    public Guid? GestionadoPorUsuarioId { get; private set; }

    /// <summary>
    /// Foto, tomada al asignar, de si el Trabajador contaba como extranjero cobrable
    /// (<see cref="CriterioExtranjeroCobrable.Efectivo"/>). Histórico congelado (decisión de Chris, 2026-10-20): la
    /// exportación y el conteo de extranjeros del mes la leen a ella, así que corregir después el documento o la casilla
    /// del Trabajador no altera meses ya gestionados. Las filas anteriores a la columna se rellenaron en la migración
    /// <c>FotoDelTrabajadorEnLaAsignacionAVisita</c> con el criterio vigente entonces.
    /// </summary>
    public bool EsExtranjeroCobrable { get; private set; }

    /// <summary>
    /// Foto del documento de identidad del Trabajador al asignarlo, solo si la asignación es cobrable (dato personal,
    /// minimizado; ver <see cref="EsExtranjeroCobrable"/>).
    /// </summary>
    public string? DocumentoIdentidad { get; private set; }

    /// <summary>
    /// Foto del nombre y apellidos del Trabajador al asignarlo, solo si la asignación es cobrable (dato personal,
    /// minimizado; ver <see cref="EsExtranjeroCobrable"/>).
    /// </summary>
    public string? NombreMostrado { get; private set; }

    private VisitaTrabajador()
    {
    }

    /// <summary>
    /// Asignación sin foto del Trabajador (<see cref="EsExtranjeroCobrable"/> falso, documento y nombre vacíos). Solo para
    /// fixtures que no tienen el Trabajador en memoria: toda asignación de la aplicación usa la sobrecarga con
    /// <see cref="Trabajador"/> (<c>CrearVisitaCommand</c>, <c>EditarVisitaCommand</c>, <c>AnadirTrabajadorAVisitaCommand</c>).
    /// </summary>
    public VisitaTrabajador(Guid visitaId, Guid trabajadorId, Guid? gestionadoPorUsuarioId = null, DateTime? fechaAsignacionUtc = null)
    {
        if (visitaId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener una visita.", nameof(visitaId));
        if (trabajadorId == Guid.Empty)
            throw new ArgumentException("La asociación debe tener un trabajador.", nameof(trabajadorId));

        VisitaId = visitaId;
        TrabajadorId = trabajadorId;
        GestionadoPorUsuarioId = gestionadoPorUsuarioId == Guid.Empty ? null : gestionadoPorUsuarioId;
        FechaAsignacionUtc = fechaAsignacionUtc ?? DateTime.UtcNow;
    }

    /// <summary>Asignación con la foto del Trabajador tomada ahora (ver <see cref="EsExtranjeroCobrable"/>).</summary>
    public VisitaTrabajador(
        Guid visitaId, Trabajador trabajador, Guid? gestionadoPorUsuarioId = null, DateTime? fechaAsignacionUtc = null)
        : this(visitaId, (trabajador ?? throw new ArgumentNullException(nameof(trabajador))).Id,
            gestionadoPorUsuarioId, fechaAsignacionUtc)
    {
        EsExtranjeroCobrable = CriterioExtranjeroCobrable.Efectivo(trabajador.MarcaExtranjeroCobrable, trabajador.Dni);

        // Minimización: solo la exportación de extranjeros del mes lee el documento y el nombre de la foto, y solo de las
        // asignaciones cobrables; las demás no guardan otra copia de esos datos personales.
        if (EsExtranjeroCobrable)
        {
            DocumentoIdentidad = trabajador.Dni;
            NombreMostrado = trabajador.NombreCompleto.Trim();
        }
    }

    /// <summary>
    /// Vacía la foto del Trabajador cuando se anonimiza (supresión de datos personales): el documento y el nombre son
    /// datos personales. <see cref="EsExtranjeroCobrable"/> se conserva porque la asignación ya se gestionó y se cobró.
    /// </summary>
    public void AnonimizarFoto()
    {
        DocumentoIdentidad = null;
        NombreMostrado = null;
    }
}
