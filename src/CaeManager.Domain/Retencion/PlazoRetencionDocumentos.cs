namespace CaeManager.Domain.Retencion;

/// <summary>
/// Plazo de conservación de los Documentos, en años enteros (decisión del 2026-11-18, «Retención de documentos:
/// configurable por Tenant, sin 5 años fijos»). Lo fija el Tenant propietario de los datos
/// (<c>ParametroSistema.AniosRetencionDocumentos</c>) y, si quiere, cada uno de sus tipos de documento
/// (<c>TipoDocumento.AniosRetencionDocumentos</c>, nulo = hereda el del Tenant propietario).
/// <list type="bullet">
/// <item>R1: de <see cref="Minimo"/> a <see cref="Maximo"/> años.</item>
/// <item>R2: por defecto, <see cref="PorDefecto"/>.</item>
/// <item>R3: una versión sustituida lleva el plazo de su documento y empieza a contar en el primero de dos eventos,
/// su sustitución o el evento propio del documento
/// (<see cref="Documentos.CriterioInicioRetencion.EventoMasTemprano"/>).</item>
/// <item>R4: cambiar el plazo solo afecta a las propuestas de purga futuras; una solicitud ya abierta conserva la
/// lista de Documentos que se le propuso (<see cref="DocumentoPropuestoPurga"/>).</item>
/// </list>
/// </summary>
public static class PlazoRetencionDocumentos
{
    public const int Minimo = 1;
    public const int Maximo = 5;
    public const int PorDefecto = 5;

    public static bool EsValido(int anios) => anios is >= Minimo and <= Maximo;

    /// <summary>Lanza <see cref="ArgumentOutOfRangeException"/> si el plazo no está entre <see cref="Minimo"/> y <see cref="Maximo"/> años.</summary>
    public static int Validar(int anios, string nombreParametro) =>
        EsValido(anios)
            ? anios
            : throw new ArgumentOutOfRangeException(
                nombreParametro, anios, $"El plazo de conservación de documentos va de {Minimo} a {Maximo} años.");
}
