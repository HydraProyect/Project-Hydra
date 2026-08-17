namespace CaeManager.Web.Components.DesignSystem;

public enum TamanoAnillo
{
    Pequeno,
    Medio,

    /// <summary>180px (mockup Inicio TALVEG, Parte XV SCREEN 01) — anillo hero de una tarjeta de resumen, no un badge inline. Único tamaño que admite Leyenda.</summary>
    Grande,

    /// <summary>88px (mockup Centro 360 TALVEG, Parte XVI PROMPT 13) — el anillo de la cabecera de entidad, junto al nombre y el badge de bloqueo; más grande que Medio (fila de lista) pero sin el espacio de Grande (hero a pantalla completa).</summary>
    Cabecera
}
