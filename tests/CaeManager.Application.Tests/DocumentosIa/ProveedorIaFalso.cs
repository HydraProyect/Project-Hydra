using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ProveedorIaFalso(
    string codigo,
    CapacidadesProveedorIa capacidades,
    Result<string>? resultadoTexto = null,
    Result<ExtraccionEstructuradaDto>? resultadoEstructurado = null) : IDocumentAIProvider
{
    public string Codigo => codigo;
    public CapacidadesProveedorIa Capacidades => capacidades;

    public int VecesLlamadoParaTexto { get; private set; }
    public int VecesLlamadoParaEstructurado { get; private set; }
    public string? UltimoTextoRecibidoParaEstructurar { get; private set; }

    public Task<Result<string>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default)
    {
        VecesLlamadoParaTexto++;
        return Task.FromResult(resultadoTexto ?? Result.Exito("texto falso"));
    }

    public Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(string texto, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        VecesLlamadoParaEstructurado++;
        UltimoTextoRecibidoParaEstructurar = texto;
        return Task.FromResult(resultadoEstructurado ?? Result.Exito(new ExtraccionEstructuradaDto(tipoEsperado, new Dictionary<string, string?>(), 99, null)));
    }
}
