using MediatR;

namespace CaeManager.Application.Common;

/// <summary>
/// Dice si el flujo actual está dentro de una petición de <c>CaeManager.Application.Inbound</c> (B-03). Con papel
/// Inbound, <c>AlcanceDatosService</c> solo da alcance dentro de este ámbito: los perfiles de organización y la cartera
/// Inbound abren los datos del Tenant TCE para lo que hace Inbound, y no para el resto de lectores (Trabajadores,
/// Documentos, Empresas… de Outbound), que con papel Inbound siguen en alcance cero, como dejó B-02b (revisor-puente
/// Opus, ronda 1 de B-03). Lo fija <see cref="AmbitoInboundBehavior{TRequest,TResponse}"/> en cada petición; no lo decide
/// el cliente.
///
/// <para>
/// <b>Un marco compartido, no un booleano</b> (Codex, ronda 2 de B-03). Cada petición fija su propio marco, también
/// cerrado (una petición de otro módulo dentro de una de Inbound no hereda el ámbito), y al terminar lo invalida y
/// restaura el anterior. Una tarea lanzada dentro del ámbito copia la referencia al marco, no su valor: cuando la
/// petición termina, el marco deja de valer también para esa tarea, aunque siga viva.
/// </para>
/// </summary>
public static class AmbitoPeticionInbound
{
    private static readonly AsyncLocal<Marco?> Actual = new();

    /// <summary>Si el flujo actual está dentro de una petición de Inbound todavía en curso.</summary>
    public static bool Activo => Actual.Value is { Inbound: true, Vigente: true };

    /// <summary>Abre el ámbito de una petición de Inbound hasta liberar el resultado.</summary>
    public static IDisposable Abrir() => Fijar(inbound: true);

    /// <summary>
    /// Fija el ámbito de una petición: abierto si es de Inbound, cerrado si no, aunque la llame una de Inbound. Al liberar
    /// el resultado invalida el marco y restaura el anterior.
    /// </summary>
    public static IDisposable Fijar(bool inbound)
    {
        var anterior = Actual.Value;
        var marco = new Marco(inbound);
        Actual.Value = marco;
        return new Cierre(marco, anterior);
    }

    private sealed class Marco(bool inbound)
    {
        public bool Inbound { get; } = inbound;
        public bool Vigente { get; set; } = true;
    }

    private sealed class Cierre(Marco marco, Marco? anterior) : IDisposable
    {
        public void Dispose()
        {
            marco.Vigente = false;
            Actual.Value = anterior;
        }
    }

    /// <summary>Si <paramref name="tipoPeticion"/> es una petición del módulo Inbound.</summary>
    public static bool EsDeInbound(Type tipoPeticion) =>
        tipoPeticion.Namespace is { } espacio
        && (espacio == "CaeManager.Application.Inbound" || espacio.StartsWith("CaeManager.Application.Inbound.", StringComparison.Ordinal));
}

/// <summary>
/// Fija <see cref="AmbitoPeticionInbound"/> alrededor del handler de cada petición (B-03): abierto en las de
/// <c>CaeManager.Application.Inbound</c> y cerrado en todas las demás, también cuando una de Inbound envía una de otro
/// módulo. Al terminar restaura el de la petición que la envió.
/// </summary>
public sealed class AmbitoInboundBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private static readonly bool EsDeInbound = AmbitoPeticionInbound.EsDeInbound(typeof(TRequest));

    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        using (AmbitoPeticionInbound.Fijar(EsDeInbound))
            return await next(cancellationToken);
    }
}
