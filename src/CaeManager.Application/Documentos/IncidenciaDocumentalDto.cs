using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Un documento operativo de un propietario (Trabajador, Empresa, Vehículo o Cliente empresarial) que pide
/// atención: está <see cref="EstadoDocumento.Vencido"/>, <see cref="EstadoDocumento.Urgente"/>,
/// <see cref="EstadoDocumento.Proximo"/> o <see cref="EstadoDocumento.SinConfirmar"/>. Es lo que un listado
/// enseña bajo la pastilla de estado de la fila: qué documento causa el estado y cuáles más hay detrás.
///
/// <para>
/// Lleva lo justo para nombrarlo y para abrirlo sin salir del listado (<see cref="DocumentoId"/> y
/// <see cref="TipoDocumentoId"/>). No incluye <see cref="EstadoDocumento.Faltante"/> ni
/// <see cref="EstadoDocumento.EnTolerancia"/>: los dos exigen un contexto (qué exige un Centro, qué tolera un
/// Cliente empresarial) que el estado documental de un propietario no tiene.
/// </para>
/// </summary>
/// <param name="TipoDocumentoNombre">
/// Nombre del Tipo de documento; cadena vacía si el Tipo ya no es legible (dado de baja): el documento sigue
/// contando, porque el estado de la fila lo cuenta.
/// </param>
public sealed record IncidenciaDocumentalDto(
    Guid DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre, EstadoDocumento Estado,
    DateOnly? FechaVencimiento);

/// <summary>
/// Desglose documental de un propietario para la fila de un listado: sus incidencias, de la más grave a la
/// menos (<see cref="SeveridadEstadoDocumento.Rango"/>) y a igualdad por nombre de Tipo, y cuántos de sus
/// documentos registrados están al día.
///
/// <para>
/// «Registrados» son sus documentos operativos (<see cref="DocumentoOperativo"/>): ni eliminados ni
/// sustituidos. «Vigentes» son los que <see cref="CumplimientoDocumental.EsConforme(EstadoDocumento)"/> da
/// por al día, la misma regla que cualquier otra fracción del producto: lo vencido y lo sin confirmar no
/// cuentan; lo que está por vencer sigue valiendo hoy y sí cuenta. Es una fracción sobre lo REGISTRADO, no
/// sobre lo exigido: no dice qué falta.
/// </para>
/// </summary>
public sealed record DesgloseDocumentalDto(
    IReadOnlyList<IncidenciaDocumentalDto> Incidencias, int DocumentosRegistrados, int DocumentosVigentes)
{
    /// <summary>El de un propietario sin ningún documento operativo.</summary>
    public static DesgloseDocumentalDto Vacio { get; } = new([], 0, 0);

    /// <summary>¿Es este estado una incidencia de las que el desglose lista?</summary>
    public static bool EsIncidencia(EstadoDocumento estado) => estado is
        EstadoDocumento.Vencido or EstadoDocumento.Urgente or EstadoDocumento.Proximo or EstadoDocumento.SinConfirmar;
}
