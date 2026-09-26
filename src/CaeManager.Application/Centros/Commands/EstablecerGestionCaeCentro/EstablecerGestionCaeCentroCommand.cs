using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.EstablecerGestionCaeCentro;

/// <summary>
/// Marca si un Centro de Trabajo exige gestión CAE (P1-X2). Pasar a
/// <see cref="ModalidadGestionCae.SinGestionCae"/> no borra nada: los canales,
/// requisitos y acreditaciones que tuviera se conservan y dejan de contar
/// mientras el Centro no requiera gestión; volver a
/// <see cref="ModalidadGestionCae.ConGestionCae"/> los recupera tal cual y
/// además da de alta, en el mismo guardado, las acreditaciones de plataforma
/// que no nacieron mientras el Centro no exigía nada
/// (<see cref="IAltaAcreditacionesPlataformaService"/>, idempotente: las que ya
/// existían no se duplican).
///
/// Autorización: rol de escritura (<see cref="AutorizacionEscrituraBehavior"/>)
/// y alcance de gestión del Gestor CAE sobre el Centro; fuera de alcance
/// responde igual que un Centro inexistente.
///
/// Auditoría: el cambio silencia el cumplimiento del Centro, así que queda en
/// RegistroAuditoria con Actor real, valor anterior y valor nuevo. Lo escribe
/// el interceptor genérico de auditoría al guardar (fijado por
/// <c>AuditoriaModalidadGestionCaeTests</c>), no este handler.
/// </summary>
public record EstablecerGestionCaeCentroCommand(Guid CentroId, ModalidadGestionCae Modalidad, Guid Version = default) : ICommand;

public class EstablecerGestionCaeCentroCommandValidator : AbstractValidator<EstablecerGestionCaeCentroCommand>
{
    public EstablecerGestionCaeCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.Modalidad).IsInEnum().WithMessage("Modalidad de gestión CAE desconocida.");
    }
}

public class EstablecerGestionCaeCentroCommandHandler(
    ICentroRepository repositorio,
    ICentrosQueryContext centrosContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EstablecerGestionCaeCentroCommand, Result>
{
    public async Task<Result> Handle(EstablecerGestionCaeCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.CentroId, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);

        var vuelveAGestionCae = centro.GestionCae == ModalidadGestionCae.SinGestionCae
            && request.Modalidad == ModalidadGestionCae.ConGestionCae;
        centro.EstablecerGestionCae(request.Modalidad);

        if (vuelveAGestionCae)
        {
            var accesosPlataforma = await centrosContext.CanalesGestionDocumental
                .Where(c => c.CentroId == centro.Id && c.Tipo == TipoCanalGestion.Plataforma)
                .ToListAsync(cancellationToken);
            if (accesosPlataforma.Count > 0)
                await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion
                {
                    Canales = accesosPlataforma,
                    CentrosQueVuelvenAGestionCae = [centro.Id]
                }, cancellationToken);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
