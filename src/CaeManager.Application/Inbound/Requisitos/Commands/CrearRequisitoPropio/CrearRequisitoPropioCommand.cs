using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Inbound.Requisitos.Commands.CrearRequisitoPropio;

/// <summary>
/// Requisito propio del Tenant TCE (DECISION_LOG 2026-11-14): lo que pide y no está en el catálogo base. Crea el tipo de
/// documento que lo acredita y lo añade a la plantilla en un solo paso. Antes, la pantalla muestra «¿Es alguno de estos?»
/// con <c>BuscarRequisitosParecidosQuery</c>; sugerir no impide crear.
///
/// <para>
/// <b>Origen.</b> No lo elige quien llama: <see cref="OrigenRequisito.PropioTenant"/>, salvo en la plantilla de exigencias
/// de un Cliente empresarial, donde todo es <see cref="OrigenRequisito.ExigenciaN3"/> (D-15). Nunca adopta un requisito de
/// base.
/// </para>
///
/// <para>
/// <b>Tipo de documento.</b> Nace con la naturaleza «requisito del cliente», sin pedirse de serie y con la sensibilidad
/// protectora por defecto (categoría especial de salud), como cualquier tipo creado por un Tenant. Por eso un requisito
/// propio no admite tolerancia (R-6) hasta que el tipo se reclasifique, algo que hoy solo hace la siembra.
/// </para>
/// </summary>
[AreaDelEncargo("TiposDocumento")]
public record CrearRequisitoPropioCommand(
    Guid PlantillaId,
    string Nombre,
    AmbitoAplicacion AmbitoAplicacion,
    IReadOnlyList<string>? Aliases,
    string? Descripcion,
    AtributosRequisito Atributos,
    Guid Version = default) : ICommand<Guid>, IComandoInbound;

public class CrearRequisitoPropioCommandValidator : AbstractValidator<CrearRequisitoPropioCommand>
{
    public CrearRequisitoPropioCommandValidator()
    {
        RuleFor(c => c.PlantillaId).NotEmpty();
        RuleFor(c => c.Version).NotEmpty().WithMessage(PlantillaRequisitosErrores.MensajeVersionObligatoria);
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("El nombre es obligatorio.").MaximumLength(TipoDocumento.LongitudMaximaNombre);
        RuleFor(c => c.AmbitoAplicacion)
            .Must(a => a is AmbitoAplicacion.Empresa or AmbitoAplicacion.Trabajador or AmbitoAplicacion.Vehiculo)
            .WithMessage("Un requisito se pide a la Empresa contratista, a un Trabajador o a un vehículo o máquina.");
        RuleForEach(c => c.Aliases).MaximumLength(TipoDocumentoAlias.LongitudMaximaTexto);
        RuleFor(c => c.Descripcion).MaximumLength(TipoDocumento.LongitudMaximaDescripcion);
        RuleFor(c => c.Atributos).NotNull().SetValidator(new AtributosRequisitoValidator());
    }
}

public class CrearRequisitoPropioCommandHandler(
    IPlantillaRequisitosRepository repositorio, ITipoDocumentoRepository tipos, IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<CrearRequisitoPropioCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearRequisitoPropioCommand request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return Result.Fallo<Guid>(AutorizacionInboundErrores.SinCapacidad);

        var plantilla = await repositorio.ObtenerPorIdAsync(request.PlantillaId, cancellationToken);
        // shortcut: autoriza con IAlcanceDatosService sin servicio CAE Inbound, sustituir por IAutorizacionInbound en B-03
        if (plantilla is null || !await PlantillaRequisitosAutorizacion.PuedeEscribirAsync(plantilla, alcanceDatos, cancellationToken))
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.NoEncontrada);

        if (ConcurrenciaOptimista.Verificar(plantilla, request.Version, "esta plantilla de requisitos") is { } conflicto)
            return Result.Fallo<Guid>(conflicto);

        if (await tipos.ExisteConNombreAsync(request.Nombre, cancellationToken: cancellationToken))
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.NombreDuplicado);

        var origen = plantilla.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial
            ? OrigenRequisito.ExigenciaN3
            : OrigenRequisito.PropioTenant;
        var atributos = request.Atributos with { Origen = origen };
        var fija = atributos.ModoVigencia == ModoVigenciaRequisito.Fija;

        RequisitoPlantilla requisito;
        TipoDocumento tipo;
        try
        {
            tipo = new TipoDocumento(
                request.Nombre,
                fija ? atributos.VigenciaMeses : null,
                aplicaVencimientoAutomatico: fija,
                orden: 0,
                request.AmbitoAplicacion,
                RequisitoDocumental.No,
                NaturalezaJuridica.RequisitoCliente,
                descripcion: request.Descripcion,
                criteriosValidacion: atributos.Criterio);
            tipo.EstablecerAliases(request.Aliases ?? []);
            requisito = plantilla.AnadirRequisito(tipo, requisitoBase: null, atributos);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Result.Fallo<Guid>(PlantillaRequisitosErrores.Invalido(ex.Message));
        }

        tipos.Agregar(tipo);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(requisito.Id);
    }
}
