using CaeManager.Domain.Common;

namespace CaeManager.Domain.Telemetria;

/// <summary>
/// Un tramo de trabajo efectivo sobre una Conversacion: cuándo empezó, cuándo
/// acabó y cuántos segundos estuvo realmente en pantalla y con el foco puesto.
///
/// <b>Lo que esta tabla no guarda, y no debe guardar nunca</b>: pulsaciones de
/// teclado, coordenadas o movimientos de ratón, patrones de scroll, capturas de
/// pantalla, ni actividad fuera de la aplicación. Solo el intervalo agregado de
/// una gestión concreta. Esa minimización es lo que hace el módulo proporcionado
/// (art. 5.1.c RGPD) y defendible como medición de un proceso de trabajo y no
/// como vigilancia de una persona — ver RGPD-TRATAMIENTO-DATOS.md.
///
/// Se escribe una fila por tramo cerrado, no un evento por interacción, y la
/// captura entera está condicionada a
/// <see cref="Configuracion.ParametroSistema.MedicionTiempoActiva"/>, apagado de
/// fábrica.
/// </summary>
public class RegistroTiempoGestion : EntidadConTenant
{
    public Guid ConversacionId { get; private set; }
    public Guid UsuarioId { get; private set; }

    /// <summary>
    /// Cliente al que se imputa el tramo, copiado al cerrarlo. Es una foto, no una
    /// referencia viva: si la conversación se reasigna después a otro Cliente, las
    /// horas ya trabajadas siguen contando donde se trabajaron. Null cuando el hilo
    /// todavía estaba en la cola de triage, sin Cliente asignado.
    /// </summary>
    public Guid? ClienteId { get; private set; }

    public DateTime InicioUtc { get; private set; }
    public DateTime FinUtc { get; private set; }
    public int SegundosActivos { get; private set; }
    public MotivoCierreSesionGestion Motivo { get; private set; }

    private RegistroTiempoGestion()
    {
    }

    public RegistroTiempoGestion(
        Guid conversacionId, Guid usuarioId, Guid? clienteId,
        DateTime inicioUtc, DateTime finUtc, int segundosActivos, MotivoCierreSesionGestion motivo)
    {
        if (conversacionId == Guid.Empty)
            throw new ArgumentException("El tramo debe pertenecer a una conversación.", nameof(conversacionId));
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El tramo debe pertenecer a un usuario.", nameof(usuarioId));
        if (finUtc < inicioUtc)
            throw new ArgumentException("El fin del tramo no puede ser anterior a su inicio.", nameof(finUtc));
        if (segundosActivos < 0)
            throw new ArgumentException("Los segundos activos no pueden ser negativos.", nameof(segundosActivos));

        // El tiempo activo es siempre un subconjunto del tramo: lo contrario significaría
        // que se está sumando tiempo que el reloj de pared no respalda.
        var segundosDelTramo = (int)Math.Ceiling((finUtc - inicioUtc).TotalSeconds);
        if (segundosActivos > segundosDelTramo)
            throw new ArgumentException("Los segundos activos no pueden superar la duración del tramo.", nameof(segundosActivos));

        ConversacionId = conversacionId;
        UsuarioId = usuarioId;
        ClienteId = clienteId;
        InicioUtc = inicioUtc;
        FinUtc = finUtc;
        SegundosActivos = segundosActivos;
        Motivo = motivo;
    }
}
