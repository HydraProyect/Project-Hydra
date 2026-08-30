using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Tests.DocumentosIa;

public class ProveedorIaFalso(
    string codigo,
    CapacidadesProveedorIa capacidades,
    Result<TextoExtraccionDto>? resultadoTexto = null,
    Result<ExtraccionEstructuradaDto>? resultadoEstructurado = null,
    bool estaDisponible = true,
    Action? alExtraerTexto = null) : IDocumentAIProvider
{
    public string Codigo => codigo;
    public CapacidadesProveedorIa Capacidades => capacidades;

    /// <summary>Disponible salvo que el test quiera justo lo contrario: un proveedor sin credencial al que el router no debe elegir.</summary>
    public bool EstaDisponible => estaDisponible;

    public int VecesLlamadoParaTexto { get; private set; }
    public int VecesLlamadoParaEstructurado { get; private set; }
    public string? UltimoTextoRecibidoParaEstructurar { get; private set; }

    public Task<Result<TextoExtraccionDto>> ExtraerTextoAsync(byte[] contenidoArchivo, string nombreArchivo, CancellationToken cancellationToken = default)
    {
        VecesLlamadoParaTexto++;
        // Gancho para observar el ESTADO del pipeline en mitad del bucle de
        // OCR — es lo que permite comprobar que el router rasteriza y envía
        // página a página en vez de rasterizarlas todas primero.
        alExtraerTexto?.Invoke();
        return Task.FromResult(resultadoTexto ?? Result.Exito(new TextoExtraccionDto("texto falso")));
    }

    public Task<Result<ExtraccionEstructuradaDto>> ExtraerEstructuradoAsync(string texto, string tipoEsperado, CancellationToken cancellationToken = default)
    {
        VecesLlamadoParaEstructurado++;
        UltimoTextoRecibidoParaEstructurar = texto;
        return Task.FromResult(resultadoEstructurado ?? Result.Exito(new ExtraccionEstructuradaDto(tipoEsperado, new Dictionary<string, string?>(), 99, null)));
    }
}
