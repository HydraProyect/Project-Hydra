using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Deteccion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.CrearDocumento;

/// <summary>
/// Exactamente uno de TrabajadorId/ClienteId/EmpresaId/VehiculoId debe venir
/// informado — el propietario del Documento (ver <see cref="Documento.DeTrabajador"/>/
/// <see cref="Documento.DeCliente"/>/<see cref="Documento.DeEmpresa"/>/
/// <see cref="Documento.DeVehiculo"/>), y debe coincidir con el
/// <see cref="AmbitoAplicacion"/> del TipoDocumento elegido.
/// FechaVencimientoManual solo se usa cuando el TipoDocumento no
/// tiene vencimiento automático (AplicaVencimientoAutomatico = false) — en
/// ese caso no hay vigencia en meses que calcular, así que se acepta la
/// fecha que introduce el usuario. Si el tipo sí es automático, se ignora y
/// se recalcula siempre a partir de la vigencia en meses.
/// </summary>
public record CrearDocumentoCommand(
    Guid? TrabajadorId, Guid? ClienteId, Guid? EmpresaId, Guid? VehiculoId, Guid TipoDocumentoId, DateOnly FechaEmision,
    DateOnly? FechaVencimientoManual, string? ArchivoUrl, string? Comentarios)
    : IRequest<Result<Guid>>;

public class CrearDocumentoCommandValidator : AbstractValidator<CrearDocumentoCommand>
{
    public CrearDocumentoCommandValidator()
    {
        RuleFor(c => c)
            .Must(c => new[] { c.TrabajadorId, c.ClienteId, c.EmpresaId, c.VehiculoId }.Count(id => id is not null) == 1)
            .WithMessage("Selecciona un trabajador, un cliente, una empresa o un vehículo (exactamente uno).");

        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona un tipo de documento.");
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }
}

public class CrearDocumentoCommandHandler(
    IDocumentoRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork,
    IDeteccionTrabajadoresService deteccionTrabajadores)
    : IRequestHandler<CrearDocumentoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearDocumentoCommand request, CancellationToken cancellationToken)
    {
        var tipoDocumento = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == request.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null)
            return Result.Fallo<Guid>(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        var ambitoSolicitado = request.TrabajadorId is not null ? AmbitoAplicacion.Trabajador
            : request.ClienteId is not null ? AmbitoAplicacion.Cliente
            : request.VehiculoId is not null ? AmbitoAplicacion.Vehiculo
            : AmbitoAplicacion.Empresa;

        if (tipoDocumento.AmbitoAplicacion != ambitoSolicitado)
            return Result.Fallo<Guid>(Error.Crear(
                "Documento.AmbitoIncorrecto",
                $"\"{tipoDocumento.Nombre}\" es un tipo de documento de {DescribirAmbito(tipoDocumento.AmbitoAplicacion)}, no de {DescribirAmbito(ambitoSolicitado)}."));

        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(request.FechaEmision, tipoDocumento.VigenciaMeses)
            : request.FechaVencimientoManual;

        var documento = ambitoSolicitado switch
        {
            AmbitoAplicacion.Trabajador => Documento.DeTrabajador(
                request.TrabajadorId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            AmbitoAplicacion.Cliente => Documento.DeCliente(
                request.ClienteId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            AmbitoAplicacion.Vehiculo => Documento.DeVehiculo(
                request.VehiculoId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios),
            _ => Documento.DeEmpresa(
                request.EmpresaId!.Value, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
                request.ArchivoUrl, request.Comentarios)
        };

        repositorio.Agregar(documento);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Mejor esfuerzo: la detección de altas/bajas de personal (Fase 36)
        // nunca debe impedir que la subida del propio Documento se dé por
        // completada, así que un fallo aquí solo se registra en el log
        // (dentro del propio servicio) y no se propaga.
        if (ambitoSolicitado == AmbitoAplicacion.Empresa && tipoDocumento.DeteccionTrabajadoresActiva && !string.IsNullOrWhiteSpace(request.ArchivoUrl))
            await deteccionTrabajadores.ProcesarDocumentoAsync(documento.Id, cancellationToken);

        return Result.Exito(documento.Id);
    }

    private static string DescribirAmbito(AmbitoAplicacion ambito) => ambito switch
    {
        AmbitoAplicacion.Trabajador => "Trabajador",
        AmbitoAplicacion.Cliente => "Cliente",
        AmbitoAplicacion.Vehiculo => "Vehículo",
        _ => "Empresa"
    };
}
