using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;

/// <summary>Sin Trabajador ni Empresa, el adjunto vuelve a quedar sin destino.</summary>
public record FijarDestinoAdjuntoCommand(Guid AdjuntoId, Guid? TrabajadorId, Guid? EmpresaId, Guid? TipoDocumentoId) : ICommand<Guid>;

public class FijarDestinoAdjuntoCommandValidator : AbstractValidator<FijarDestinoAdjuntoCommand>
{
    public FijarDestinoAdjuntoCommandValidator()
    {
        RuleFor(c => c.AdjuntoId).NotEmpty();
        RuleFor(c => c)
            .Must(c => c.TrabajadorId is null || c.EmpresaId is null)
            .WithMessage("Selecciona un trabajador o una empresa, no los dos.");
    }
}

/// <summary>
/// Propone a quién va un adjunto entrante y de qué tipo es. No crea ningún Documento ni envía nada: eso lo hace la
/// asignación, que vuelve a pasar por el alcance y por el alta normal. El rol lo decide
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>; aquí se añade el alcance sobre el hilo y sobre el
/// destino, y se comprueba que destino y tipo existan en el Tenant actual.
/// </summary>
public class FijarDestinoAdjuntoCommandHandler(
    IComunicacionesQueryContext comunicacionesContext, ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IAdjuntoMensajeRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<FijarDestinoAdjuntoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(FijarDestinoAdjuntoCommand request, CancellationToken cancellationToken)
    {
        var origen = await RepartoAdjuntos.ObtenerAsync(comunicacionesContext, request.AdjuntoId, cancellationToken);
        if (origen is null || !await origen.VisibleAsync(alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        if (origen.Direccion != DireccionMensaje.Entrante)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEntrante", "Solo se reparten los adjuntos recibidos."));

        if (!await DestinoValidoAsync(request, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.DestinoNoValido", "No encontramos ese destino."));

        if (request.TipoDocumentoId is { } tipoId && !await tiposDocumentoContext.TiposDocumento.AnyAsync(t => t.Id == tipoId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Adjunto.TipoNoValido", "No encontramos ese tipo de documento."));

        var adjunto = await repositorio.ObtenerPorIdAsync(request.AdjuntoId, cancellationToken);
        if (adjunto is null)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));
        if (!adjunto.EstaPendiente)
            return Result.Fallo<Guid>(Error.Crear("Adjunto.YaResuelto", "Este adjunto ya está resuelto."));

        adjunto.FijarDestino(request.TrabajadorId, request.EmpresaId, request.TipoDocumentoId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(adjunto.Id);
    }

    // El alcance responde sí a todo con acceso total: la existencia en el Tenant se comprueba aparte, bajo RLS.
    private async Task<bool> DestinoValidoAsync(FijarDestinoAdjuntoCommand request, CancellationToken cancellationToken)
    {
        if (request.TrabajadorId is { } trabajadorId)
            return await alcanceDatos.TrabajadorVisibleAsync(trabajadorId, cancellationToken)
                && await trabajadoresContext.Trabajadores.AnyAsync(t => t.Id == trabajadorId, cancellationToken);

        if (request.EmpresaId is { } empresaId)
            return await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken)
                && await empresasContext.Empresas.AnyAsync(e => e.Id == empresaId, cancellationToken);

        return true;
    }
}
