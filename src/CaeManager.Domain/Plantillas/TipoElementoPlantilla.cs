namespace CaeManager.Domain.Plantillas;

/// <summary>ADR-010 § 2.8 — abstracción única para todo lo que se puede colocar sobre un PDF de plantilla, en vez de tablas hermanas por tipo.</summary>
public enum TipoElementoPlantilla
{
    Texto,
    Fecha,
    Checkbox,
    Firma,
    Imagen,
    Constante
}
