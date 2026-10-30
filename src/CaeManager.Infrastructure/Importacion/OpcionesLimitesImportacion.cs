using CaeManager.Application.Common;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Topes del análisis de un .xlsx subido a la importación (OWASP ASVS 4.0.3 V12.1 / V13.4, API4:2023). El análisis
/// carga el libro entero en memoria dentro del proceso web que comparten todos los Tenants, así que se acota cuántos
/// análisis corren a la vez —en total (<see cref="AnalisisConcurrentes"/>) y por Tenant propietario
/// (<see cref="AnalisisConcurrentesPorTenant"/>, que no puede superar el total, para que un solo Tenant no ocupe todos
/// los huecos)— y cuánto puede pesar el libro (filas y celdas por hoja, y celdas en todo el libro).
/// </summary>
public sealed class OpcionesLimitesImportacion
{
    public const string Seccion = "LimitesImportacion";
    public int AnalisisConcurrentes { get; set; } = 2;
    public int AnalisisConcurrentesPorTenant { get; set; } = 1;
    public int EsperaSegundos { get; set; } = 10;
    public int MaximoFilasPorHoja { get; set; } = 50_000;
    public int MaximoCeldasPorHoja { get; set; } = 500_000;
    public int MaximoCeldasPorLibro { get; set; } = 1_000_000;

    public bool EsValida() =>
        AnalisisConcurrentes >= 1 && AnalisisConcurrentesPorTenant >= 1
        && AnalisisConcurrentesPorTenant <= AnalisisConcurrentes && EsperaSegundos is >= 0 and <= 300
        && MaximoFilasPorHoja >= 1 && MaximoCeldasPorHoja >= 1 && MaximoCeldasPorLibro >= 1;

    public void Validar()
    {
        if (!EsValida())
            throw new InvalidOperationException("Los límites de importación no son válidos.");
    }
}

/// <summary>Una hoja supera el tope de filas o de celdas; <see cref="Filas"/> dice cuál de los dos.</summary>
public sealed class LimiteHojaImportacionException(bool filas)
    : ArchivoRechazadoException(filas
        ? "El archivo supera el máximo de filas por hoja."
        : "El archivo supera el máximo de celdas permitido.")
{
    public bool Filas { get; } = filas;
}
