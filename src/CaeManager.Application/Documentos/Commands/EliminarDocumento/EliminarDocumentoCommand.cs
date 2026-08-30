using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.EliminarDocumento;

public record EliminarDocumentoCommand(Guid Id) : ICommand;

public class EliminarDocumentoCommandHandler(
    IDocumentoRepository repositorio, IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<EliminarDocumentoCommand, Result>
{
    public async Task<Result> Handle(EliminarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento."));

        // La identidad se resuelve aquí, no se recibe del comando: un
        // UsuarioId en el contrato público es auditoría falsificable —
        // cualquier llamador, presente o futuro, puede pasar el GUID que
        // quiera— y el llamador de Web sustituía la ausencia de sesión por
        // Guid.Empty, atribuyendo el borrado a nadie. Sin identidad se aborta.
        // Mismo criterio que EliminarCentro/Trabajador/Empresa/Cliente
        // (auditoría del Módulo 5).
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Documento.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        documento.MarcarComoEliminado(usuarioId.Value);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
