using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Marca que el flujo actual está dentro de una petición de <c>CaeManager.Application.Inbound</c> (B-03). Con papel
/// Inbound, <c>AlcanceDatosService</c> solo da alcance dentro de este ámbito: los perfiles de organización y la cartera
/// Inbound abren los datos del Tenant TCE para lo que hace Inbound, y no para el resto de lectores (Trabajadores,
/// Documentos, Empresas… de Outbound), que con papel Inbound siguen en alcance cero, como dejó B-02b (revisor-puente
/// Opus, ronda 1 de B-03). Lo abre <see cref="AmbitoInboundBehavior{TRequest,TResponse}"/>; no lo decide el cliente.
/// </summary>
public static class AmbitoPeticionInbound
{
    private static readonly AsyncLocal<bool> Abierto = new();

    /// <summary>Si el flujo actual está dentro de una petición de Inbound.</summary>
    public static bool Activo => Abierto.Value;

    /// <summary>Abre el ámbito hasta que se libera el resultado; restaura el valor anterior al cerrar.</summary>
    public static IDisposable Abrir()
    {
        var anterior = Abierto.Value;
        Abierto.Value = true;
        return new Cierre(anterior);
    }

    private sealed class Cierre(bool anterior) : IDisposable
    {
        public void Dispose() => Abierto.Value = anterior;
    }

    /// <summary>Si <paramref name="tipoPeticion"/> es una petición del módulo Inbound.</summary>
    public static bool EsDeInbound(Type tipoPeticion) =>
        tipoPeticion.Namespace is { } espacio
        && (espacio == "CaeManager.Application.Inbound" || espacio.StartsWith("CaeManager.Application.Inbound.", StringComparison.Ordinal));
}

/// <summary>
/// Abre <see cref="AmbitoPeticionInbound"/> alrededor del handler de toda petición de <c>CaeManager.Application.Inbound</c>
/// (B-03). Las demás peticiones pasan sin tocarlo, así que dentro de una petición de otro módulo el ámbito está cerrado
/// aunque la llame una pantalla de Inbound.
/// </summary>
public sealed class AmbitoInboundBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly bool EsDeInbound = AmbitoPeticionInbound.EsDeInbound(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        if (!EsDeInbound)
            return await next(cancellationToken);

        using (AmbitoPeticionInbound.Abrir())
            return await next(cancellationToken);
    }
}
