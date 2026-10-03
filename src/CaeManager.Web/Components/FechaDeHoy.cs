using CaeManager.Domain.Common;

namespace CaeManager.Web.Components;

/// <summary>
/// La fecha que un formulario propone como «hoy» (emisión de un documento,
/// solicitud o respuesta del blindaje 42.1…).
///
/// Es el día de negocio de <see cref="DiaDeNegocio"/> (Europe/Madrid), el mismo con
/// el que el dominio y los validadores juzgan que una fecha «no puede ser futura»
/// (<c>Documento.CorregirVigencia</c>, <c>SolicitudCertificacionTgss</c>,
/// <c>Crear/RenovarDocumentoCommandValidator</c>). Proponer y juzgar tienen que usar
/// el mismo día: si el formulario propusiera un día posterior al del juez, el
/// guardado fallaría sin que el usuario tocara la fecha.
/// </summary>
public static class FechaDeHoy
{
    /// <summary>El día de negocio de <paramref name="reloj"/>, en el formato de un <c>input type="date"</c>.</summary>
    public static string ParaCampoFecha(TimeProvider reloj) =>
        DiaDeNegocio.Hoy(reloj).ToString("yyyy-MM-dd");
}
