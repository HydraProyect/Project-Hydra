using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;

/// <summary>
/// "Marcar subido" del drill-down por plataforma (mockup Documentos TALVEG,
/// pestaña Plataforma — cierra el hallazgo P-04) y del panel «Gestionar
/// acreditación en plataforma»: registra una subida que el Gestor CAE ya hizo
/// A MANO en la plataforma. No crea cambio de estado ni pide proveedor activo.
///
/// <para>
/// La extensión de navegador ya no pasa por aquí: su «Lo he enviado» es
/// <c>ConfirmarSubidaDesdeExtensionCommand</c>, que exige el proveedor activo
/// (kill switch remoto, MVP2 § 14.5) y deja un cambio de estado con origen
/// Extensión que se puede deshacer. Las dos vías comparten la carga con
/// alcance (<see cref="AcreditacionSubible"/>) y la regla de la rechazada.
/// </para>
///
/// Una acreditación <see cref="EstadoAcreditacion.Rechazada"/> no se marca
/// subida (P0-9b, FS-02): la plataforma ya evaluó y devolvió esa versión, y
/// marcarla subida sin versión nueva registraría como enviada la misma copia
/// rechazada y sacaría el rechazo de la cola. La salida es subir una versión
/// corregida (<c>RenovarDocumentoCommand</c>): con archivo nuevo sustituye el
/// Documento y crea acreditaciones pendientes; al corregir el mismo registro
/// reinicia sus acreditaciones a <see cref="EstadoAcreditacion.PendienteDeSubir"/>.
/// Desde ahí sí se marca subida. La lista de la extensión reutiliza esta regla.
/// </summary>
public record MarcarAcreditacionSubidaCommand(Guid AcreditacionId) : ICommand;

/// <summary>
/// Lo que comparten «Marcar subido» y «Lo he enviado» de la extensión antes de escribir: la acreditación existe, su
/// Documento está en la cartera de quien pide (lo que no, sale como «no encontrada», sin decir por qué) y, si la
/// plataforma la rechazó, hay una versión nueva.
/// </summary>
internal static class AcreditacionSubible
{
    public const string CodigoRechazadaSinVersionNueva = "Acreditacion.RechazadaSinVersionNueva";

    public static readonly Error NoEncontrada = Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación.");

    public static readonly Error RechazadaSinVersionNueva = Error.Crear(
        CodigoRechazadaSinVersionNueva,
        "La plataforma rechazó esta versión. Sube una versión corregida del documento: al guardarla volverá a quedar pendiente de subir.");

    /// <summary>
    /// La acreditación, si existe, el Centro de su canal está entre los que gestiona quien pide (la Asignación de Cartera
    /// del Gestor CAE; una cartera Total no restringe) y su Documento es visible; <c>null</c> si no. Con
    /// <paramref name="recargar"/> se lee de nuevo de la base con sus cambios de estado, que es lo que necesita quien crea
    /// o deshace un cambio.
    /// </summary>
    public static async Task<AcreditacionDocumentoPlataforma?> CargarVisibleAsync(
        Guid acreditacionId, IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio,
        IDocumentoRepository documentoRepositorio, IAlcanceDatosService alcanceDatos,
        IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, bool recargar, CancellationToken cancellationToken)
    {
        var acreditacion = recargar
            ? await acreditacionRepositorio.ObtenerPorIdActualizadoAsync(acreditacionId, cancellationToken)
            : await acreditacionRepositorio.ObtenerPorIdAsync(acreditacionId, cancellationToken);
        if (acreditacion is null)
            return null;

        var centrosParaGestion = await alcanceDatos.ObtenerCentroIdsParaGestionAsync(cancellationToken);
        if (centrosParaGestion is not null && !await centrosContext.CanalesGestionDocumental
            .AnyAsync(c => c.Id == acreditacion.CanalGestionDocumentalId && centrosParaGestion.Contains(c.CentroId), cancellationToken))
            return null;

        var documento = await documentoRepositorio.ObtenerPorIdAsync(acreditacion.DocumentoId, cancellationToken);
        return documento is not null && await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken)
            ? acreditacion
            : null;
    }
}

public class MarcarAcreditacionSubidaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionSubidaCommand, Result>
{
    public const string CodigoRechazadaSinVersionNueva = AcreditacionSubible.CodigoRechazadaSinVersionNueva;

    public async Task<Result> Handle(MarcarAcreditacionSubidaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext, centrosContext,
            recargar: false, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(AcreditacionSubible.NoEncontrada);

        if (!ReglaSubidaAcreditacion.PermiteSubir(acreditacion))
            return Result.Fallo(AcreditacionSubible.RechazadaSinVersionNueva);

        acreditacion.MarcarSubida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
