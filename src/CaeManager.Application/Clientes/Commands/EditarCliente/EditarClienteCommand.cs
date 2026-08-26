using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Clientes.Commands.EditarCliente;

/// <summary>
/// <paramref name="Version"/> es la del registro tal como lo vio quien edita
/// (llega en <c>ClienteDetalleDto</c>). <see cref="Guid.Empty"/> significa
/// "sin comprobación", para los llamadores que todavía no la propagan.
/// </summary>
public record EditarClienteCommand(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, string? Notas, Guid Version = default) : ICommand;

public class EditarClienteCommandValidator : AbstractValidator<EditarClienteCommand>
{
    public EditarClienteCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.RazonSocial)
            .NotEmpty().WithMessage("La razón social es obligatoria.")
            .MaximumLength(Empresa.LongitudMaximaRazonSocial)
            .WithMessage($"La razón social no puede superar {Empresa.LongitudMaximaRazonSocial} caracteres.");

        RuleFor(c => c.Cif)
            .NotEmpty().WithMessage("El CIF es obligatorio.")
            .Must(EsCifValido).WithMessage("El CIF no es válido.");

        RuleFor(c => c.Notas)
            .MaximumLength(Empresa.LongitudMaximaNotas).WithMessage($"Las notas no pueden superar {Empresa.LongitudMaximaNotas} caracteres.");
    }

    private static bool EsCifValido(string cif)
    {
        var resultado = ValidadorIdentificacion.Analizar(cif);
        return resultado.Tipo == TipoIdentificacion.NifEmpresa && resultado.EsValido;
    }
}

/// <summary>
/// Patrón de concurrencia optimista de referencia para el resto de agregados.
///
/// La columna <c>Version</c> y <c>ConcurrenciaOptimistaInterceptor</c> por sí
/// solos no bastan en este flujo: el handler recarga la entidad justo antes de
/// escribir, así que el valor "original" que EF pone en el WHERE es el que
/// acaba de leer y el UPDATE nunca choca. Eso protege contra dos escrituras
/// simultáneas, pero no contra el caso que importa — dos personas con el
/// formulario abierto, que es donde una pisaba a la otra en silencio.
///
/// Por eso se compara explícitamente contra la versión que viajó al
/// formulario. La comprobación en memoria cubre la ventana larga (el rato que
/// el formulario está abierto) y el token de EF cubre la corta (dos guardados
/// en el mismo instante); ninguna de las dos sobra.
/// </summary>
public class EditarClienteCommandHandler(IEmpresaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarClienteCommand, Result>
{
    public async Task<Result> Handle(EditarClienteCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null || !await alcanceDatos.ClienteVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        if (ConcurrenciaOptimista.Verificar(empresa, request.Version, "este cliente") is { } conflicto)
            return Result.Fallo(conflicto);

        if (await repositorio.ExisteConRazonSocialAsync(request.RazonSocial, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.RazonSocialDuplicada", "Ya existe una organización con esta razón social."));

        if (await repositorio.ExisteConCifAsync(request.Cif, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.CifDuplicado", "Ya existe una organización con este CIF."));

        empresa.ActualizarComoCliente(request.RazonSocial, request.Cif, request.EsCritico, request.Notas);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
