using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Puerto de contribución del Núcleo (ADR-013, mecanismo 10): cada módulo aporta los campos de sus entidades que guardan
/// una clave del almacenamiento de archivos, y los barridos transversales del Núcleo (hoy,
/// <see cref="ReparacionAdjuntosTemporales"/>) los recorren sin nombrar las entidades del módulo. Lo registra el módulo
/// en el punto de composición, junto con su nombre en <see cref="ModulosConReferenciasArchivo"/>: un módulo declarado
/// que no aporta hace fallar el barrido antes de tocar nada, en vez de dejar sus claves sin recorrer.
/// </summary>
public interface IContribucionReferenciasArchivo
{
    /// <summary>El módulo que aporta, con el nombre que declara <see cref="ModulosConReferenciasArchivo"/>.</summary>
    string Modulo { get; }

    IReadOnlyList<CampoReferenciaArchivo> Campos { get; }
}

/// <summary>
/// Los módulos que, según la composición, tienen que aportar sus campos a los barridos del Núcleo. Sin esta declaración,
/// o con un módulo declarado sin contribución, la reparación de adjuntos no se ejecuta (falla cerrada).
/// </summary>
public sealed record ModulosConReferenciasArchivo(IReadOnlyCollection<string> Modulos);

/// <summary>
/// Un campo de texto de una entidad de EF que guarda una clave del almacenamiento. Se construye con <see cref="De{T}"/>
/// para que el barrido lea el tipo concreto sin reflexión.
/// </summary>
public sealed class CampoReferenciaArchivo
{
    private CampoReferenciaArchivo(Type entidad, string campo,
        Func<ReparacionAdjuntosTemporales, List<PropertyEntry>, CancellationToken, Task> recoger)
    {
        Entidad = entidad;
        Campo = campo;
        Recoger = recoger;
    }

    public Type Entidad { get; }

    public string Campo { get; }

    internal Func<ReparacionAdjuntosTemporales, List<PropertyEntry>, CancellationToken, Task> Recoger { get; }

    public static CampoReferenciaArchivo De<T>(string campo) where T : class =>
        new(typeof(T), campo, (reparacion, referencias, ct) => reparacion.RecogerAsync<T>(campo, referencias, ct));
}
