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
/// TALVEG.
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
/// documento». Para deshacerlo, «Cambiar» de Centro 360 la lleva a cualquiera de los cuatro estados del portal.
/// </para>
/// </summary>
public record MarcarAcreditacionNoRequeridaCommand(Guid AcreditacionId) : ICommand;

public class MarcarAcreditacionNoRequeridaCommandValidator : AbstractValidator<MarcarAcreditacionNoRequeridaCommand>
{
    public MarcarAcreditacionNoRequeridaCommandValidator()
    {
        RuleFor(c => c.AcreditacionId).NotEmpty();
    }
}

public class MarcarAcreditacionNoRequeridaCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarAcreditacionNoRequeridaCommand, Result>
{
    public async Task<Result> Handle(MarcarAcreditacionNoRequeridaCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext,
            centrosContext, recargar: false, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo(AcreditacionSubible.NoEncontrada);

        if (acreditacion.Estado == EstadoAcreditacion.NoRequerida)
            return Result.Exito();

        acreditacion.MarcarNoRequerida();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
