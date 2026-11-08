using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.EstablecerRelacionDeEntradaCentro;

/// <summary>
/// Papel por Centro (DECISION_LOG, 2026-11-08, «Papel por Centro»), desde la Ficha del Centro: fija o quita la
/// Relación Empresarial <c>Empresa del Centro → X</c> por la que esa Empresa entra en el Centro como subcontrata
/// de X, la contratista principal. <c>RelacionEmpresarialId = null</c> quita el ancla: la Empresa entra como
/// contratista principal y no se muestra nada. El papel no se guarda: lo calcula
/// <see cref="CaeManager.Application.Centros.Queries.PapelDeEntradaEnCentro"/> a partir del ancla (ADR-011).
///
/// Autorización: rol de escritura (<see cref="AutorizacionEscrituraBehavior"/>) y alcance de gestión del Gestor
/// CAE sobre el Centro; fuera de alcance responde igual que un Centro inexistente. La arista se busca en el
/// Tenant propietario actual (filtro global y RLS) y su cliente X tiene que estar entre los Clientes
/// empresariales visibles de quien la fija: una arista de otro Tenant, inexistente o con X fuera de su cartera
/// responde lo mismo, para no revelar cuál de las tres es. La FK compuesta con <c>TenantId</c> repite en la base
/// la frontera de Tenant.
/// </summary>
public record EstablecerRelacionDeEntradaCentroCommand(
    Guid CentroId, Guid? RelacionEmpresarialId, Guid Version = default) : ICommand;

public class EstablecerRelacionDeEntradaCentroCommandValidator
    : AbstractValidator<EstablecerRelacionDeEntradaCentroCommand>
{
    public EstablecerRelacionDeEntradaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.RelacionEmpresarialId).NotEqual(Guid.Empty)
            .WithMessage("Elige una relación empresarial o quita el ancla.");
    }
}

public class EstablecerRelacionDeEntradaCentroCommandHandler(
    ICentroRepository repositorio,
    IEmpresasQueryContext empresasContext,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EstablecerRelacionDeEntradaCentroCommand, Result>
{
    private static readonly Error RelacionNoEncontrada =
        Error.Crear("Centro.RelacionEntradaNoEncontrada", "No encontramos esa relación empresarial.");

    public async Task<Result> Handle(
        EstablecerRelacionDeEntradaCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.CentroId, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);

        if (request.RelacionEmpresarialId is not { } relacionId)
        {
            centro.QuitarAnclaDeEntrada();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito();
        }

        var relacion = await empresasContext.RelacionesEmpresariales
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == relacionId && r.TenantId == centro.TenantId, cancellationToken);
        if (relacion is null)
            return Result.Fallo(RelacionNoEncontrada);

        var clientesVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clientesVisibles is not null && !clientesVisibles.Contains(relacion.ClienteId))
            return Result.Fallo(RelacionNoEncontrada);

        var anclada = centro.AnclarRelacionDeEntrada(relacion);
        if (anclada.EsFallido)
            return anclada;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
