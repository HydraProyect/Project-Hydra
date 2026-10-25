namespace CaeManager.Domain.Documentos;

/// <summary>
/// Desde dónde se anotó un <see cref="CambioEstadoAcreditacionPlataforma"/>. Se guarda como entero: los valores no se
/// renumeran, porque los cambios ya anotados los conservan.
/// </summary>
public enum OrigenCambioEstadoAcreditacion
{
    /// <summary>«Marcar subido», «Marcar validado» o «Cambiar» de Centro 360. Es el valor de los cambios anteriores a que existiera el origen.</summary>
    Centro360 = 0,

    /// <summary>«Lo he enviado» de la extensión de navegador: el Gestor CAE confirma que envió en la plataforma el documento que la extensión puso en el formulario.</summary>
    Extension = 1,
}
