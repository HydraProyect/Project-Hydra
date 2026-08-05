namespace CaeManager.Domain.Visitas;

/// <summary>
/// Fase F: cuánto de urgente es validar la documentación de una Visita antes
/// de que empiece — las plataformas de gestión documental de los clientes
/// suelen exigir un plazo mínimo de validación (24-48h típicamente) antes de
/// dejar entrar a los trabajadores. Orden de peor a mejor para que "filtrar
/// por lo peor" sea siempre el mismo gesto que el resto del sistema
/// (ver EstadoDocumentoUi.OpcionesDocumentales).
/// </summary>
public enum NivelUrgenciaVisita
{
    /// <summary>La visita ya empezó (FechaInicio &lt;= hoy &lt;= FechaFin) — validar ya no tiene sentido, solo queda constatarlo.</summary>
    EnCurso,

    /// <summary>Menos horas hasta el inicio que HorasCriticasVisita — es probable que la plataforma del cliente ya no llegue a validar a tiempo.</summary>
    Critica,

    /// <summary>Menos horas hasta el inicio que HorasAvisoVisita — dentro de la ventana mínima de validación habitual.</summary>
    Urgente,

    Normal
}
