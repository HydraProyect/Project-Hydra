using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;

/// <summary>
/// «Esta plataforma no lo pide» de la ventana de una Gestión automática (DECISION_LOG 2026-11-08, punto 3): el Gestor CAE
/// ha mirado el portal y esa plataforma no pide este documento en ese Centro. Deja la acreditación en
/// <see cref="EstadoAcreditacion.NoRequerida"/>, que cuenta como paso hecho; nunca toca el Documento ni su vigencia en
/// TALVEG. La vigencia en la plataforma sí se pierde (queda Sin confirmar): una fecha vencida de un documento que no se
/// pide no debe poner el Centro en rojo.
///
/// <para>
/// Mismo alcance que «Marcar subido» (<see cref="AcreditacionSubible"/>): el Centro del acceso tiene que estar en la
/// Asignación de Cartera de quien pide y el Documento tiene que ser visible; lo que no, sale como «no encontrada» sin
/// decir por qué. El rol Consulta no llega aquí: es un <see cref="ICommand"/> y lo para
/// <c>AutorizacionEscrituraBehavior</c>.
/// </para>
///
/// <para>
/// Sale de cualquier estado, también de un rechazo: el portal puede haber rechazado justo porque no lo pedía. Lo que no
/// hace es sobrevivir a una renovación: <see cref="AcreditacionDocumentoPlataforma.ReiniciarPorRenovacionDocumento"/>
/// devuelve todas las acreditaciones a Pendiente de subir, y por eso la ventana dice «hasta que se renueve el
/// documento». Para deshacerlo, «Cambiar» de Centro 360 la lleva a cualquiera de los cuatro estados del portal; la
/// vigencia que hubiera confirmada en la plataforma no vuelve.
/// </para>
///
/// <para>
/// <paramref name="VersionEsperada"/> es la versión de la acreditación que tenía delante quien lo marca
/// (<see cref="EntidadBase.Version"/>, la que da la pantalla de la Gestión). Si la acreditación ha cambiado desde entonces,
/// no se sobrescribe a ciegas: falla con <see cref="CambiadaDesdeQueSeAbrio"/> y no escribe. Es la versión y no el estado
/// porque hay cambios que no mueven el estado y que «no lo pide» borraría: una vigencia confirmada en la plataforma, o un
/// estado que cambia y vuelve al que se vio. Se lee de nuevo de la base para compararla; un choque entre esa lectura y el
/// guardado lo para el token de EF y llega como <see cref="ConcurrenciaOptimista.CodigoConflicto"/>.
/// </para>
/// </summary>
public record MarcarAcreditacionNoRequeridaCommand(Guid AcreditacionId, Guid VersionEsperada) : ICommand;

public class MarcarAcreditacionNoRequeridaCommandValidator : AbstractValidator<MarcarAcreditacionNoRequeridaCommand>
{
    public MarcarAcreditacionNoRequeridaCommandValidator()
    {
        RuleFor(c => c.AcreditacionId).NotEmpty();
        // Guid.Empty en ConcurrenciaOptimista significa «sin comprobación»: aquí no vale.
        RuleFor(c => c.VersionEsperada).NotEmpty();
    }
}

public class MarcarAcreditacionNoRequeridaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionNoRequeridaCommand, Result>
{
    public static readonly Error CambiadaDesdeQueSeAbrio = Error.Crear(
        "Acreditacion.CambiadaDesdeQueSeAbrio",
        "Este paso ha cambiado desde que abriste la Gestión. Revisa cómo está ahora antes de marcarlo.");

    public async Task<Result> Handle(MarcarAcreditacionNoRequeridaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext,
            centrosContext, recargar: true, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(AcreditacionSubible.NoEncontrada);

        // El historial es inmutable (D5/D8, la misma regla de RenovarDocumentoCommand): la acreditación de un Documento que
        // otro ya sustituyó no se marca. Una renovación con archivo nuevo no toca las acreditaciones del anterior, así que su
        // versión no cambia y una pantalla abierta antes la marcaría sin conflicto; lo que cuenta es la acreditación del
        // Documento vigente. Antes del atajo de «ya está»: sobre el historial no se responde éxito. Releído de la base y no
        // la instancia que ya rastreara el contexto (el del circuito vive toda la visita): este comando no escribe el
        // Documento, así que ningún token pararía una sustitución confirmada después de esa primera lectura.
        var documento = await documentoRepositorio.ObtenerIncluidoEliminadoActualizadoAsync(
            acreditacion.DocumentoId, acreditacion.TenantId, cancellationToken);
        if (documento is null || documento.EstaEliminado)
            return Result.Fallo(AcreditacionSubible.NoEncontrada);
        if (!DocumentoOperativo.Es(documento))
            return Result.Fallo(DocumentoEnHistorial.NuevoError());

        // Ya está como se pide: nada que sobrescribir, sea cual sea la versión que se vio. Una No requerida anterior a la
        // invariante que conserve una vigencia no cuenta como «ya está»: marcarla otra vez se la quita.
        if (acreditacion.Estado == EstadoAcreditacion.NoRequerida && acreditacion.Vigencia.EstaSinConfirmar)
            return Result.Exito();

        // Comparación directa y no ConcurrenciaOptimista.Verificar: ahí Guid.Empty es «sin comprobación», y aquí una
        // versión vacía que se colara sin validador tiene que fallar, no pasar.
        if (acreditacion.Version != request.VersionEsperada)
            return Result.Fallo(CambiadaDesdeQueSeAbrio);

        acreditacion.MarcarNoRequerida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
