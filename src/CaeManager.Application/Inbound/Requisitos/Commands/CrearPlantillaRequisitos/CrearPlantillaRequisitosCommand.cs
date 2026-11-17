using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Inbound.CatalogoBase;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.Requisitos.Commands.CrearPlantillaRequisitos;

/// <summary>
/// Crea una plantilla de requisitos vacía del Tenant TCE (B-14) para una Actividad o un riesgo del catálogo base, un
/// Centro propio o las exigencias de un Cliente empresarial propio (D-15). Una sola activa por referencia.
/// </summary>
/// <param name="ReferenciaId">El Id de la Actividad, el riesgo, el Centro o la Empresa Cliente empresarial, según <paramref name="Tipo"/>.</param>
[AreaDelEncargo("TiposDocumento")]
public record CrearPlantillaRequisitosCommand(TipoPlantillaRequisitos Tipo, Guid ReferenciaId) : ICommand<Guid>, IComandoInbound;

public class CrearPlantillaRequisitosCommandValidator : AbstractValidator<CrearPlantillaRequisitosCommand>
{
    public CrearPlantillaRequisitosCommandValidator()
    {
        RuleFor(c => c.Tipo).IsInEnum();
        RuleFor(c => c.ReferenciaId).NotEmpty();
    }
}

public class CrearPlantillaRequisitosCommandHandler(
    IPlantillaRequisitosRepository repositorio, ICatalogoBaseQueryContext catalogo, ICentrosQueryContext centros,
    IEmpresasQueryContext empresas, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<CrearPlantillaRequisitosCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearPlantillaRequisitosCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionInboundErrores.SinCapacidad);

        // La capacidad (GestionarInbound) la pide arriba IAutorizacionInbound (B-03); el alcance de cartera del
        // recurso lo decide PlantillaRequisitosAutorizacion con IAlcanceDatosService, del servicio del papel activo.
        // Fuera de la cartera, la referencia «no existe»: el mismo error que si no existiera.
        if (!await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(request.Tipo, request.ReferenciaId, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.ReferenciaNoEncontrada);

        PlantillaRequisitos plantilla;
        try
        {
            switch (request.Tipo)
            {
                case TipoPlantillaRequisitos.PorActividad:
                    var actividad = await catalogo.CatalogoActividades.FirstOrDefaultAsync(a => a.Id == request.ReferenciaId, cancellationToken);
                    if (actividad is null)
                        return Result.Fallo<Guid>(PlantillaRequisitosErrores.ReferenciaNoEncontrada);
                    plantilla = PlantillaRequisitos.ParaActividad(actividad);
                    break;
                case TipoPlantillaRequisitos.PorRiesgo:
                    var riesgo = await catalogo.CatalogoRiesgos.FirstOrDefaultAsync(r => r.Id == request.ReferenciaId, cancellationToken);
                    if (riesgo is null)
                        return Result.Fallo<Guid>(PlantillaRequisitosErrores.ReferenciaNoEncontrada);
                    plantilla = PlantillaRequisitos.ParaRiesgo(riesgo);
                    break;
                case TipoPlantillaRequisitos.DeCentro:
                    if (!await centros.Centros.AnyAsync(c => c.Id == request.ReferenciaId, cancellationToken))
                        return Result.Fallo<Guid>(PlantillaRequisitosErrores.ReferenciaNoEncontrada);
                    plantilla = PlantillaRequisitos.ParaCentro(request.ReferenciaId);
                    break;
                case TipoPlantillaRequisitos.ExigenciasClienteEmpresarial:
                    // Una Empresa contratista no es Cliente empresarial: EsCritico != null lo identifica, como en
                    // AlcanceDatosService. Con acceso total la cartera no lo filtra, así que se comprueba aquí.
                    if (!await empresas.Empresas.AnyAsync(e => e.Id == request.ReferenciaId && e.EsCritico != null, cancellationToken))
                        return Result.Fallo<Guid>(PlantillaRequisitosErrores.ReferenciaNoEncontrada);
                    plantilla = PlantillaRequisitos.ParaExigenciasDe(request.ReferenciaId);
                    break;
                default:
                    return Result.Fallo<Guid>(PlantillaRequisitosErrores.ReferenciaNoEncontrada);
            }
        }
        catch (InvalidOperationException ex)
        {
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.Invalido(ex.Message));
        }

        // El índice único parcial lo respalda si dos altas se cruzan.
        if (await repositorio.ExisteActivaAsync(request.Tipo, request.ReferenciaId, cancellationToken))
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.YaExiste);

        repositorio.Agregar(plantilla);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(plantilla.Id);
    }
}
