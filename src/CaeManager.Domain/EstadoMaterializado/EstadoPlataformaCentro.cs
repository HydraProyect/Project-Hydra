using CaeManager.Domain.Centros;

namespace CaeManager.Domain.EstadoMaterializado;

/// <summary>
/// Resumen del estado en la Plataforma CAE de un Centro (<see cref="EstadoEntidad.EstadoPlataforma"/>), aparte de su estado
/// en TALVEG. <c>null</c> en la fila: ninguna causa de la Plataforma CAE.
///
/// <para>
/// No es <c>EstadoAcreditacion</c>: el Centro se resume con dos causas que una acreditación sola no expresa. Una vigencia
/// vencida en la Plataforma CAE no es un rechazo, y las dos ponen hoy el Centro en Bloqueado; el pendiente (sin subir o
/// subido sin validar) lo pone en Pendiente. <b>Valores congelados</b> (<c>OrdinalesDeEnumsPublicadosTests</c>).
/// </para>
/// </summary>
public enum EstadoPlataformaCentro
{
    /// <summary>Algún documento sin subir o subido sin validar en la Plataforma CAE del Centro.</summary>
    Pendiente = 0,

    /// <summary>Alguna acreditación rechazada o con la vigencia en la Plataforma CAE ya vencida.</summary>
    Bloqueado = 1
}

/// <summary>
/// El rótulo combinado que el listado de Centros pinta hoy, derivado de las dos columnas separadas. Mientras D-2 no decida
/// otra cosa, ninguna pantalla cambia: quien lea el estado materializado de un Centro lo pinta con esto, y
/// <c>EstadoMaterializadoCoincideConRecalculoCompletoTests</c> comprueba que da lo mismo que
/// <see cref="CalculadoraEstadoCentro.Calcular"/> sobre todas las causas.
/// </summary>
public static class EstadoCentroCombinado
{
    /// <summary>
    /// Mismo orden que <see cref="CalculadoraEstadoCentro.Calcular"/>: Bloqueado (Plataforma CAE) &gt; Vencido &gt; Faltante
    /// &gt; Pendiente (Plataforma CAE) &gt; Urgente &gt; Próximo &gt; Vigente. Un Centro sin gestión CAE no tiene causas.
    /// </summary>
    public static EstadoCentro Derivar(EstadoCentro estadoTalveg, EstadoPlataformaCentro? estadoPlataforma) =>
        estadoTalveg == EstadoCentro.SinGestionCae ? EstadoCentro.SinGestionCae
        : estadoPlataforma == EstadoPlataformaCentro.Bloqueado ? EstadoCentro.Bloqueado
        : estadoTalveg is EstadoCentro.Vencido or EstadoCentro.Faltante ? estadoTalveg
        : estadoPlataforma == EstadoPlataformaCentro.Pendiente ? EstadoCentro.Pendiente
        : estadoTalveg;
}
