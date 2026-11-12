using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.ConfirmarVigenciaAcreditacion;

/// <summary>
/// Anota hasta cuándo vale un documento en una plataforma, sin tocar el estado
/// de la acreditación.
///
/// <para>
/// Hace falta porque confirmar la vigencia al aceptar no basta. El Gestor CAE
/// puede aceptar sin saber la fecha —«todavía no lo sé» es una respuesta
/// válida—, y sobre todo: la migración deja SIN CONFIRMAR todas las
/// acreditaciones que ya existían, incluidas las aceptadas hace meses. Sin este
/// comando ese estado sería un callejón sin salida, con la vigencia de medio
/// sistema imposible de completar desde el producto.
/// </para>
/// <para>
/// Solo sobre una acreditación Aceptada: es la única que la pantalla ofrece anotar (fila y bloque de la pestaña
/// Plataformas CAE, y «Anotar vigencia» del listado agrupado), y la precondición se comprueba aquí porque el estado
/// puede cambiar mientras el modal está abierto.
/// </para>
/// </summary>
public record ConfirmarVigenciaAcreditacionCommand(Guid AcreditacionId, VigenciaEnPlataforma Vigencia) : ICommand<ResultadoAnotacionAcreditacionDto>;

public class ConfirmarVigenciaAcreditacionCommandHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarVigenciaAcreditacionCommand, Result<ResultadoAnotacionAcreditacionDto>>
{
    public async Task<Result<ResultadoAnotacionAcreditacionDto>> Handle(ConfirmarVigenciaAcreditacionCommand request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext,
            centrosContext, recargar: true, cancellationToken);
        if (acreditacion is null)
            return Result.Fallo<ResultadoAnotacionAcreditacionDto>(Error.Crear("Acreditacion.NoEncontrada", "No encontramos esta acreditación."));

        // «Esta plataforma no lo pide» entre que se abrió la pantalla y se anota: no hay vigencia que anotar.
        if (!acreditacion.PuedeConfirmarVigencia)
            return Result.Fallo<ResultadoAnotacionAcreditacionDto>(Error.Crear(
                "Acreditacion.NoRequerida", "Esta plataforma no pide este documento: no hay vigencia que anotar."));

        // Solo se anota la vigencia de lo que la plataforma ya ha validado. Si entre que se abrió la pantalla y se
        // anota alguien la rechazó o la devolvió a pendiente, la fecha describiría una acreditación que ya no existe;
        // anotarla igualmente dejaría una vigencia sobre un documento que la plataforma no da por bueno.
        if (acreditacion.Estado != EstadoAcreditacion.Aceptada)
            return Result.Fallo<ResultadoAnotacionAcreditacionDto>(Error.Crear(
                "Acreditacion.NoAceptada", "Esta acreditación ya no está validada en la plataforma: no se anota su vigencia. Recarga la pantalla."));

        var estadoPrevio = acreditacion.Estado;
        var vigenciaPrevia = acreditacion.Vigencia;

        acreditacion.ConfirmarVigencia(request.Vigencia);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Lo que se sobrescribió y la versión resultante: es lo que necesita «Deshacer».
        return Result.Exito(new ResultadoAnotacionAcreditacionDto(acreditacion.Id, estadoPrevio, vigenciaPrevia, acreditacion.Version));
    }
}
