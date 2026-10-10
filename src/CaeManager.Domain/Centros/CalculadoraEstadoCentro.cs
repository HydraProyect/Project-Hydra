using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Centros;

/// <summary>
/// Calcula el estado de cumplimiento de un Centro como el peor caso entre
/// los EstadoDocumento de sus Documentos aplicables (de su Empresa y de cada
/// Trabajador con Asignación activa) y las causas bloqueantes que vienen de la
/// plataforma del Cliente empresarial (<c>tieneRequisitoBloqueanteSinCumplir</c>:
/// vigencia vencida o acreditación rechazada allí). Los documentos de Trabajador
/// y de Empresa bloquean a Trabajadores por Centro (<see cref="Documentos.ReglaBloqueoDeAcceso"/>),
/// no ponen el Centro en <see cref="EstadoCentro.Bloqueado"/>. Función pura, sin dependencias — mismo criterio que
/// CalculadoraEstadoDocumento: quien llama ya resolvió qué Documentos y
/// Requisitos aplican a este Centro, aquí solo se agrega el peor caso.
///
/// El orden de «peor caso» es el único de todas las superficies (decisión del
/// propietario, 2026-10-03): Bloqueante → Vencido → Faltante, y después lo que
/// está por vencer (Urgente, Próximo). Un Centro con un documento vencido y otro
/// que falta está <see cref="EstadoCentro.Vencido"/>. Es el mismo orden que
/// <see cref="SeveridadEstadoDocumento"/> da a los documentos; hasta el
/// 2026-10-09 esta calculadora evaluaba Faltante antes que Vencido.
///
/// <see cref="EstadoCentro.Pendiente"/> (pendiente en la plataforma CAE del Centro,
/// <see cref="ReglaPendienteEnPlataforma"/>) va entre Faltante y Urgente (decisión
/// del propietario, 2026-10-10): un documento sin subir o sin validar en la
/// plataforma pesa más que uno que solo está por vencer, y menos que uno que no
/// existe o ya no vale.
///
/// Una Gestion Pendiente asociada a un hueco no cambia este cálculo a
/// propósito: es solo seguimiento operativo del Gestor, y el hueco real
/// sigue existiendo hasta que se suba el Documento (ver Gestion).
///
/// <see cref="EstadoDocumento.SinConfirmar"/> (vigencia sin anotar) no es
/// causa de color, mismo criterio que la vigencia en plataforma sin
/// confirmar: el semáforo solo refleja lo malo conocido. Quien calcula el
/// porcentaje de cumplimiento sí lo excluye de «al día».
/// </summary>
public static class CalculadoraEstadoCentro
{
    /// <param name="tienePendienteEnPlataforma">
    /// Algún documento aplicable a este Centro está pendiente en su plataforma CAE
    /// (<see cref="ReglaPendienteEnPlataforma.CuentaEnElCentro"/>). Siempre <c>false</c> en un Centro sin plataforma.
    /// </param>
    public static EstadoCentro Calcular(
        IReadOnlyCollection<EstadoDocumento> estadosDocumentos,
        bool tieneRequisitoBloqueanteSinCumplir,
        bool tienePendienteEnPlataforma = false)
    {
        if (tieneRequisitoBloqueanteSinCumplir)
            return EstadoCentro.Bloqueado;

        if (estadosDocumentos.Contains(EstadoDocumento.Vencido))
            return EstadoCentro.Vencido;

        if (estadosDocumentos.Contains(EstadoDocumento.Faltante))
            return EstadoCentro.Faltante;

        if (tienePendienteEnPlataforma)
            return EstadoCentro.Pendiente;

        if (estadosDocumentos.Contains(EstadoDocumento.Urgente))
            return EstadoCentro.Urgente;

        if (estadosDocumentos.Contains(EstadoDocumento.Proximo))
            return EstadoCentro.Proximo;

        return EstadoCentro.Vigente;
    }

    /// <summary>
    /// Clave para ordenar por gravedad (mayor = peor), con el mismo orden que
    /// <see cref="Calcular"/>: Bloqueado, Vencido, Faltante, Pendiente, Urgente, Próximo,
    /// Vigente. No es el valor numérico de <see cref="EstadoCentro"/>, que está
    /// congelado por la API v1 y difiere en tres puntos: tiene Faltante por
    /// encima de Vencido, <see cref="EstadoCentro.SinGestionCae"/> —que no es
    /// un grado de incumplimiento y va por debajo de <see cref="EstadoCentro.Vigente"/>—
    /// por encima de Bloqueado, y <see cref="EstadoCentro.Pendiente"/>, añadido al final,
    /// por encima de todos.
    /// </summary>
    public static int Gravedad(EstadoCentro estado) => estado switch
    {
        EstadoCentro.SinGestionCae => -1,
        EstadoCentro.Vigente => 0,
        EstadoCentro.Proximo => 1,
        EstadoCentro.Urgente => 2,
        EstadoCentro.Pendiente => 3,
        EstadoCentro.Faltante => 4,
        EstadoCentro.Vencido => 5,
        EstadoCentro.Bloqueado => 6,
        _ => (int)estado
    };
}
