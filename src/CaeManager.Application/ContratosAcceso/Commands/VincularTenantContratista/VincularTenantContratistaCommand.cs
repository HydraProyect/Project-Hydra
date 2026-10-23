using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.Commands.VincularTenantContratista;

/// <summary>
/// El Tenant del Cliente empresarial anota en su contrato de acceso cuál es el Tenant de la Empresa contratista: su
/// mitad del apretón de dos actos de Inbound M1 (la otra es la <c>AutorizacionLecturaDocumental</c> que concede el
/// Tenant de la Empresa contratista). Una sola vez: ver <see cref="ContratoAcceso.VincularTenantContratista"/>.
///
/// <para>
/// <b>Cómo conoce el Tenant del Cliente empresarial ese identificador está sin decidir (DN-1).</b> El comando lo recibe
/// como parámetro y no hay pantalla que lo pida; este Tenant tampoco puede comprobar que el Tenant exista, porque no
/// lo ve. Vincular no da lectura: la dará el PR 3, y solo con la autorización expresa y vigente del Tenant de la
/// Empresa contratista.
/// </para>
/// </summary>
public record VincularTenantContratistaCommand(Guid ContratoAccesoId, Guid TenantContratistaId, Guid Version = default) : ICommand;

public class VincularTenantContratistaCommandValidator : AbstractValidator<VincularTenantContratistaCommand>
{
    public VincularTenantContratistaCommandValidator()
    {
        RuleFor(c => c.ContratoAccesoId).NotEmpty();
        RuleFor(c => c.TenantContratistaId).NotEmpty();
    }
}

/// <summary>Misma autoridad que el resto de comandos del contrato de acceso (<see cref="ContratoAccesoAutorizacion"/>).</summary>
public class VincularTenantContratistaCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, ITenantActual tenantActual, IUnitOfWork unitOfWork)
    : IRequestHandler<VincularTenantContratistaCommand, Result>
{
    private const string MensajePropio = "La organización de la Empresa contratista no puede ser tu propia organización.";

    public async Task<Result> Handle(VincularTenantContratistaCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.ContratoAccesoId, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        if (ConcurrenciaOptimista.Verificar(contrato, request.Version, "este contrato de acceso") is { } conflicto)
            return Result.Fallo(conflicto);

        // Lo repiten el dominio (contra el TenantId ya sellado del contrato) y el CHECK de la base.
        if (tenantActual.TenantId is not { } tenantId || request.TenantContratistaId == tenantId)
            return Result.Fallo(Error.Crear(
                "ContratoAcceso.TenantContratistaPropio",
                MensajePropio));

        if (contrato.TenantContratistaId is not null)
            return Result.Fallo(Error.Crear(
                "ContratoAcceso.TenantContratistaYaVinculado",
                "Este contrato ya está vinculado con la organización de la Empresa contratista: para corregirlo, cierra el contrato y crea otro."));

        try
        {
            contrato.VincularTenantContratista(request.TenantContratistaId);
        }
        catch (ArgumentException)
        {
            return Result.Fallo(Error.Crear("ContratoAcceso.TenantContratistaPropio", MensajePropio));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
