namespace CaeManager.Domain.Common;

/// <summary>
/// Representa el resultado de una operación que puede fallar por una razón de
/// negocio esperable. Se usa en vez de excepciones para esos casos (ver
/// ARCHITECTURE.md, "Manejo de errores").
/// </summary>
public class Result
{
    public bool EsExitoso { get; }
    public bool EsFallido => !EsExitoso;
    public Error Error { get; }

    protected Result(bool esExitoso, Error error)
    {
        if (esExitoso && error != Error.Ninguno)
            throw new InvalidOperationException("Un resultado exitoso no puede tener un error asociado.");
        if (!esExitoso && error == Error.Ninguno)
            throw new InvalidOperationException("Un resultado fallido debe tener un error asociado.");

        EsExitoso = esExitoso;
        Error = error;
    }

    public static Result Exito() => new(true, Error.Ninguno);
    public static Result Fallo(Error error) => new(false, error);
    public static Result<T> Exito<T>(T valor) => new(valor, true, Error.Ninguno);
    public static Result<T> Fallo<T>(Error error) => new(default, false, error);
}

public class Result<T> : Result
{
    private readonly T? _valor;

    public T Valor => EsExitoso
        ? _valor!
        : throw new InvalidOperationException("No se puede acceder al valor de un resultado fallido.");

    protected internal Result(T? valor, bool esExitoso, Error error) : base(esExitoso, error)
    {
        _valor = valor;
    }

    public static implicit operator Result<T>(T valor) => Exito(valor);
}
