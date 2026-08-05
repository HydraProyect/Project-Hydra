using CaeManager.Domain.Documentos;

namespace CaeManager.Domain.Centros;

/// <summary>
/// Calcula el estado de cumplimiento de un Centro como el peor caso entre
/// los EstadoDocumento de sus Documentos aplicables (de su Empresa y de cada
/// Trabajador con Asignación activa) y sus RequisitosDocumentales
/// bloqueantes. Función pura, sin dependencias — mismo criterio que
/// CalculadoraEstadoDocumento: quien llama ya resolvió qué Documentos y
/// Requisitos aplican a este Centro, aquí solo se agrega el peor caso.
///
/// Una Gestion Pendiente asociada a un hueco no cambia este cálculo a
/// propósito: es solo seguimiento operativo del Gestor, y el hueco real
/// sigue existiendo hasta que se suba el Documento (ver Gestion).
/// </summary>
public static class CalculadoraEstadoCentro
{
    public static EstadoCentro Calcular(
        IReadOnlyCollection<EstadoDocumento> estadosDocumentos,
        bool tieneRequisitoBloqueanteSinCumplir)
    {
        if (tieneRequisitoBloqueanteSinCumplir)
            return EstadoCentro.Bloqueado;

        if (estadosDocumentos.Contains(EstadoDocumento.Faltante))
            return EstadoCentro.Faltante;

        if (estadosDocumentos.Contains(EstadoDocumento.Vencido))
            return EstadoCentro.Vencido;

        if (estadosDocumentos.Contains(EstadoDocumento.Urgente))
            return EstadoCentro.Urgente;

        if (estadosDocumentos.Contains(EstadoDocumento.Proximo))
            return EstadoCentro.Proximo;

        return EstadoCentro.Vigente;
    }
}
