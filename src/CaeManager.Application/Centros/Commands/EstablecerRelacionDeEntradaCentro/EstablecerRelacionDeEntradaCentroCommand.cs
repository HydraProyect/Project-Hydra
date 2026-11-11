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
/// CAE sobre el Centro; fuera de alcance responde igual que un Centro inexistente, y lo mismo si el Centro ya
/// tiene un ancla cuya X no está entre los Clientes empresariales visibles de quien la quita o la cambia. Ese
/// error no nombra el ancla ni a X, pero el rechazo en sí sigue distinguiéndose del éxito que tendría el mismo
/// comando sin ancla: es un oráculo residual aceptado (DECISION_LOG, 2026-11-11, punto 11 a); quien no ve a X no
/// pisa el ancla. Si la arista anclada ya se cerró, el ancla no da papel (punto 11 b) y el comando la trata como
/// si no hubiera ancla, sin exigir ver a su X. La arista se busca en el
/// Tenant propietario actual (filtro global y RLS) y su cliente X tiene que estar entre los Clientes
/// empresariales visibles de quien la fija: una arista de otro Tenant, inexistente o con X fuera de su cartera
/// responde lo mismo, para no revelar cuál de las tres es. La FK compuesta con <c>TenantId</c> repite en la base
/// la frontera de Tenant. <c>Version</c> es la versión del Centro que se vio en pantalla y es obligatoria
/// (<see cref="Guid.Empty"/> se rechaza en la validación): un valor vacío desactivaría el control optimista.
/// </summary>
public record EstablecerRelacionDeEntradaCentroCommand(
    Guid CentroId, Guid? RelacionEmpresarialId, Guid Version) : ICommand;

public class EstablecerRelacionDeEntradaCentroCommandValidator
    : AbstractValidator<EstablecerRelacionDeEntradaCentroCommand>
{
    public EstablecerRelacionDeEntradaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        // Un Version vacío desactiva el control optimista (ConcurrenciaOptimista.Verificar lo deja pasar) y
        // permitiría pisar un cambio concurrente: se exige la versión que se vio en pantalla.
        RuleFor(c => c.Version).NotEmpty();
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
    private static readonly Error CentroNoEncontrado =
        Error.Crear("Centro.NoEncontrado", "No encontramos este centro.");

    private static readonly Error RelacionNoEncontrada =
        Error.Crear("Centro.RelacionEntradaNoEncontrada", "No encontramos esa relación empresarial.");

    public async Task<Result> Handle(
        EstablecerRelacionDeEntradaCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.CentroId, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(CentroNoEncontrado);

        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);

        // Quitar o cambiar un ancla también exige ver a su X: quien no la ve no pisa lo que no sabe que hay. Responde
        // con el mismo error de arriba, que no nombra el ancla ni a X; el rechazo frente al éxito sin ancla es el
        // oráculo residual descrito en la cabecera. Un ancla cuya arista ya se cerró (VigenciaHasta informada) no da
        // papel en la ficha (DECISION_LOG, 2026-11-11, punto 11 b) y aquí cuenta igual que sin ancla: se puede quitar
        // o sustituir sin ver a su X, y el comando responde lo mismo que en un Centro sin ancla.
        if (centro.RelacionEmpresarialEntradaId is { } anclaActual)
        {
            var xActual = await empresasContext.RelacionesEmpresariales.AsNoTracking()
                .Where(r => r.Id == anclaActual && r.TenantId == centro.TenantId && r.VigenciaHasta == null)
                .Select(r => (Guid?)r.ClienteId)
                .FirstOrDefaultAsync(cancellationToken);
            if (xActual is { } x && !await alcanceDatos.ClienteVisibleAsync(x, cancellationToken))
                return Result.Fallo(CentroNoEncontrado);
        }

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

        if (!await alcanceDatos.ClienteVisibleAsync(relacion.ClienteId, cancellationToken))
            return Result.Fallo(RelacionNoEncontrada);

        var anclada = centro.AnclarRelacionDeEntrada(relacion);
        if (anclada.EsFallido)
            return anclada;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}
