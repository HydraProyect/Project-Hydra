using CaeManager.Domain.Common;

namespace CaeManager.Domain.ApiKeys;

/// <summary>Qué credencial es <see cref="UsoApiDiario.ApiKeyId"/>: las dos viven en tablas distintas.</summary>
public enum TipoClaveUsoApi
{
    ClaveSoporte = 1,
    ClaveIntegracion = 2,
}

/// <summary>
/// Rastro agregado de <c>/api/v1</c> (C-11, decisión D-5 opción a): una fila por
/// (<c>TenantId</c>, <c>ApiKeyId</c>, <see cref="Dia"/>, <see cref="Endpoint"/>), no una por petición.
/// Responde a qué clave leyó qué recurso y cuántas veces; no guarda parámetros ni identificadores de
/// la URL, porque <see cref="Endpoint"/> es el patrón de la ruta.
/// <para>
/// <see cref="ApiKeyId"/> no tiene clave foránea: vale para <c>ClaveApi</c> y para
/// <c>ClaveIntegracionTenant</c>, y <see cref="TipoClave"/> dice cuál.
/// </para>
/// </summary>
public class UsoApiDiario : EntidadConTenant
{
    public const int LongitudMaximaEndpoint = 200;

    public Guid ApiKeyId { get; private set; }
    public TipoClaveUsoApi TipoClave { get; private set; }
    public DateOnly Dia { get; private set; }
    public string Endpoint { get; private set; } = string.Empty;
    public long Peticiones { get; private set; }

    private UsoApiDiario()
    {
    }
}
