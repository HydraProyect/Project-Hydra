using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace CaeManager.Infrastructure.FileStorage;

/// <summary>
/// Puerto de contribución del Núcleo (ADR-013, mecanismo 10): cada módulo aporta los campos de sus entidades que guardan
/// una clave del almacenamiento de archivos, y los barridos transversales del Núcleo (hoy,
/// <see cref="ReparacionAdjuntosTemporales"/>) los recorren sin nombrar las entidades del módulo. Lo registra el módulo
/// en el punto de composición; un campo que nadie aporta no se recorre.
/// </summary>
public interface IContribucionReferenciasArchivo
{
    IReadOnlyList<CampoReferenciaArchivo> Campos { get; }
}

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
