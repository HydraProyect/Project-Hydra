using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaCliente;

/// <summary>Nivel 2 (Gestor CAE u otro rol con acceso a este Cliente): override por Cliente+TipoDocumento — ver ConfiguracionIaDocumentoCliente.</summary>
public record ActualizarLecturaIaClienteCommand(Guid ClienteId, Guid TipoDocumentoId, bool Activa) : IRequest<Result>;

public class ActualizarLecturaIaClienteCommandHandler(
    IConfiguracionIaDocumentoClienteRepository repositorio, IApplicationDbContext dbContext,
    IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatosService)
    : IRequestHandler<ActualizarLecturaIaClienteCommand, Result>
{
    public async Task<Result> Handle(ActualizarLecturaIaClienteCommand request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatosService.TieneAccesoTotalAsync(cancellationToken))
        {
            var clienteIdsVisibles = await alcanceDatosService.ObtenerClienteIdsVisiblesAsync(cancellationToken);
            if (clienteIdsVisibles is null || !clienteIdsVisibles.Contains(request.ClienteId))
                return Result.Fallo(Error.Crear("LecturaIa.SinAcceso", "No tienes acceso a este cliente."));
        }
        else if (!await dbContext.Clientes.AnyAsync(c => c.Id == request.ClienteId, cancellationToken))
        {
            // Con acceso total (Administrador) el chequeo de arriba no corre —
            // verificación de Ids ajenos, ver P0-1 de docs/business/MATURITY_REVIEW.md.
            return Result.Fallo(Error.Crear("LecturaIa.ClienteNoEncontrado", "No encontramos este cliente."));
        }

        if (!await dbContext.TiposDocumento.AnyAsync(t => t.Id == request.TipoDocumentoId, cancellationToken))
            return Result.Fallo(Error.Crear("LecturaIa.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        var existente = await repositorio.ObtenerAsync(request.ClienteId, request.TipoDocumentoId, cancellationToken);
        if (existente is not null)
        {
            existente.Establecer(request.Activa);
        }
        else
        {
            // Sin fila = hereda el nivel 1 (ver ConfiguracionIaDocumentoCliente) —
            // solo se crea una fila cuando el valor difiere del default (activo).
            if (!request.Activa)
                repositorio.Agregar(new ConfiguracionIaDocumentoCliente(request.ClienteId, request.TipoDocumentoId, activa: false));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
