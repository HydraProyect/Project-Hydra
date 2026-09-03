using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Documentos.Commands.GuardarSelloEmpresa;

/// <summary>
/// Guarda o reemplaza el sello habitual de una Empresa (plan Fase A de
/// firma en campo) — una Empresa, un sello guardado. Solo por subida
/// (fotografía de un sello físico real), siempre con recorte de fondo claro.
/// </summary>
public record GuardarSelloEmpresaCommand(Guid EmpresaId, byte[] ImagenOriginal) : ICommand;

public class GuardarSelloEmpresaCommandValidator : AbstractValidator<GuardarSelloEmpresaCommand>
{
    private const int TamanoMaximoBytes = 5 * 1024 * 1024;

    public GuardarSelloEmpresaCommandValidator()
    {
        RuleFor(c => c.EmpresaId).NotEmpty();
        RuleFor(c => c.ImagenOriginal).NotEmpty();
        RuleFor(c => c.ImagenOriginal.Length).LessThanOrEqualTo(TamanoMaximoBytes)
            .WithMessage("La imagen no puede superar los 5 MB.");
    }
}

public class GuardarSelloEmpresaCommandHandler(
    IEmpresaRepository empresaRepositorio,
    ISelloEmpresaRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IConversorImagenSelloService conversor,
    IFileStorageService almacenamiento,
    IUnitOfWork unitOfWork,
    ILogger<GuardarSelloEmpresaCommandHandler> logger)
    : IRequestHandler<GuardarSelloEmpresaCommand, Result>
{
    public async Task<Result> Handle(GuardarSelloEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresaRepositorio.ObtenerPorIdAsync(request.EmpresaId, cancellationToken);
        // Defensa en profundidad (REC-149): inalcanzable para el rol Cliente
        // vía AutorizacionEscrituraBehavior; alcance de gestión como segunda
        // barrera independiente.
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        var pngNormalizado = conversor.NormalizarAPng(request.ImagenOriginal, recortarFondoClaro: true);

        var ahora = DateTime.UtcNow;
        using var flujo = new MemoryStream(pngNormalizado);
        var nuevaUrl = await almacenamiento.GuardarAsync(flujo, "sello.png", cancellationToken);

        var existente = await repositorio.ObtenerPorEmpresaAsync(empresa.Id, cancellationToken);
        string? urlAnterior = null;

        if (existente is null)
        {
            repositorio.Agregar(new SelloEmpresa(empresa.Id, nuevaUrl, ahora));
        }
        else
        {
            urlAnterior = existente.ImagenUrl;
            existente.Reemplazar(nuevaUrl, ahora);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (urlAnterior is not null)
        {
            try
            {
                await almacenamiento.EliminarAsync(urlAnterior, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "No se pudo borrar la imagen anterior {Archivo} del sello de la empresa {EmpresaId}.",
                    urlAnterior, empresa.Id);
            }
        }

        return Result.Exito();
    }
}
