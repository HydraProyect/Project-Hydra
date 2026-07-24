using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ProveedorIaFalso(string codigo, CapacidadesProveedorIa capacidades) : IDocumentAIProvider
{
    public string Codigo => codigo;
    public CapacidadesProveedorIa Capacidades => capacidades;

    public Task<Result<string>> ExtraerTextoAsync(byte[] contenidoImagen, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Exito("texto falso"));

    public Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(string texto, string tipoEsperado, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Exito(new ExtraccionEstructuradaDto(tipoEsperado, new Dictionary<string, string?>(), 99, null)));
}
