namespace CaeManager.Domain.Common;

public sealed class Error : IEquatable<Error>
{
    public static readonly Error Ninguno = new(string.Empty, string.Empty);

    public string Codigo { get; }
    public string Mensaje { get; }

    private Error(string codigo, string mensaje)
    {
        Codigo = codigo;
        Mensaje = mensaje;
    }

    public static Error Crear(string codigo, string mensaje) => new(codigo, mensaje);

    public bool Equals(Error? other) => other is not null && Codigo == other.Codigo;
    public override bool Equals(object? obj) => Equals(obj as Error);
    public override int GetHashCode() => Codigo.GetHashCode();
}
